"""Composition root and lifecycle.

Startup order matters: the health endpoint comes up before gRPC so the
orchestrator can distinguish "starting" from "dead"; readiness is flipped only
after the server is listening.
"""

from __future__ import annotations

import json
import logging
import signal
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Callable

from prometheus_client import CONTENT_TYPE_LATEST, generate_latest

from .config import Settings, configure_logging
from .hot_reload import HotReloadWatcher
from .model import ModelBundle, heuristic_bundle, load_bundle
from .predictor import Predictor
from .retrain import RetrainScheduler
from .service import ClientLabels, build_grpc_server
from .storage import SqliteStore

_LOGGER = logging.getLogger(__name__)


class Readiness:
    def __init__(self) -> None:
        self._ready = threading.Event()

    def set(self) -> None:
        self._ready.set()

    def clear(self) -> None:
        self._ready.clear()

    @property
    def ready(self) -> bool:
        return self._ready.is_set()


def _make_handler(payload_provider: Callable[[], dict[str, object]]):
    class Handler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def do_GET(self) -> None:  # noqa: N802 - BaseHTTPRequestHandler contract
            route = self.path.split("?", 1)[0]

            if route == "/healthz":
                payload = payload_provider()
                body = json.dumps(payload, separators=(",", ":")).encode("utf-8")
                self._respond(200 if payload.get("status") == "ok" else 503, body, "application/json")
            elif route == "/metrics":
                self._respond(200, generate_latest(), CONTENT_TYPE_LATEST)
            else:
                self._respond(404, b'{"error":"not found"}', "application/json")

        def _respond(self, status: int, body: bytes, content_type: str) -> None:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError):
                pass

        def log_message(self, format: str, *args: object) -> None:  # noqa: A002
            return

    return Handler


class HealthHttpServer(threading.Thread):
    def __init__(self, port: int, payload_provider: Callable[[], dict[str, object]]) -> None:
        super().__init__(name="http-health", daemon=True)
        self._server = ThreadingHTTPServer(("0.0.0.0", port), _make_handler(payload_provider))
        self._server.daemon_threads = True

    def run(self) -> None:  # pragma: no cover - container lifecycle
        _LOGGER.info("health endpoint listening on port %d", self._server.server_port)
        self._server.serve_forever(poll_interval=0.5)

    def stop(self) -> None:
        self._server.shutdown()
        self._server.server_close()
        self.join(timeout=5.0)


def _load_initial_bundle(path: Path) -> ModelBundle:
    try:
        if Path(path).exists():
            return load_bundle(Path(path))
    except Exception:  # noqa: BLE001 - a broken model must not prevent boot
        _LOGGER.exception("could not load the model bundle; starting with the heuristic model")

    _LOGGER.warning("no trained model available; serving the heuristic fallback")
    return heuristic_bundle()


def main() -> int:
    settings = Settings.from_env()
    configure_logging(settings.log_level)

    store = SqliteStore(settings.dataset_path)
    store.migrate()

    bundle = _load_initial_bundle(settings.model_path)
    predictor = Predictor(bundle)
    _LOGGER.info(
        "model loaded",
        extra={"model_version": bundle.version, "training_samples": bundle.training_samples},
    )

    readiness = Readiness()

    def health_payload() -> dict[str, object]:
        current = predictor.acquire()
        return {
            "status": "ok" if readiness.ready else "starting",
            "service": "botdetection-ai",
            "model_version": current.version,
            "model_checksum": current.checksum[:16],
            "feature_schema_version": current.schema_version,
            "reload_count": predictor.reload_count,
            "uptime_seconds": round(predictor.uptime_seconds, 1),
        }

    health_server = HealthHttpServer(settings.http_port, health_payload)
    health_server.start()

    scheduler = RetrainScheduler(settings, store, predictor)
    scheduler.start()

    watcher = HotReloadWatcher(settings.model_path, predictor, settings.reload_interval_seconds)
    watcher.start()

    labels = ClientLabels(store.load_client_labels())
    server = build_grpc_server(
        settings, predictor, store, labels, scheduler.notify, scheduler.request
    )
    server.start()
    readiness.set()
    _LOGGER.info("grpc server listening on port %d", settings.grpc_port)

    stop_event = threading.Event()

    def handle_signal(signum: int, frame: object) -> None:
        _LOGGER.info("signal %s received; shutting down", signum)
        stop_event.set()

    signal.signal(signal.SIGTERM, handle_signal)
    signal.signal(signal.SIGINT, handle_signal)

    stop_event.wait()

    readiness.clear()
    stopped = server.stop(grace=5)
    stopped.wait(timeout=10)
    watcher.stop()
    scheduler.stop()
    health_server.stop()
    store.close()

    _LOGGER.info("shutdown complete")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
