"""Zero-downtime model hot reload.

A daemon thread polls the model file's (mtime_ns, size). When the fingerprint
changes it loads and validates a new bundle, then swaps the reference held by
:class:`~botdetection.predictor.Predictor`. The serving process never restarts
and in-flight requests keep using the previous bundle until the swap.

Why polling instead of inotify: the model lives on a Docker volume that may be
a network-backed filesystem, where inotify events are unreliable. One stat()
per second costs microseconds and works everywhere.
"""

from __future__ import annotations

import logging
import threading
import time
from pathlib import Path

from .model import ModelBundle, ModelSchemaError, load_bundle
from .predictor import Predictor

_LOGGER = logging.getLogger(__name__)
_FAILURE_LOG_INTERVAL_SECONDS = 30.0


class HotReloadWatcher(threading.Thread):
    def __init__(
        self,
        model_path: Path,
        predictor: Predictor,
        interval_seconds: float = 1.0,
    ) -> None:
        super().__init__(name="model-hot-reload", daemon=True)
        self._path = Path(model_path)
        self._predictor = predictor
        self._interval = max(interval_seconds, 0.1)
        self._stop_event = threading.Event()
        self._fingerprint: tuple[int, int] | None = None
        self._next_failure_log = 0.0

    def run(self) -> None:  # pragma: no cover - exercised by the container health check
        while not self._stop_event.wait(self._interval):
            self._reload_if_changed()

    def stop(self) -> None:
        self._stop_event.set()
        self.join(timeout=5.0)

    def _reload_if_changed(self) -> None:
        try:
            stat = self._path.stat()
        except OSError:
            return

        fingerprint = (stat.st_mtime_ns, stat.st_size)
        if fingerprint == self._fingerprint:
            return

        try:
            bundle = load_bundle(self._path)
        except (OSError, EOFError, ValueError, ModelSchemaError) as exc:
            # A corrupt or half-written file must never kill the watcher and
            # must never replace the model that is currently serving traffic.
            self._log_failure(exc)
            return
        except Exception as exc:  # noqa: BLE001 - a bad pickled object must not crash the thread
            self._log_failure(exc)
            return

        current = self._predictor.acquire()
        if bundle.checksum == current.checksum:
            self._fingerprint = fingerprint
            return

        previous = self._predictor.swap(bundle)
        self._fingerprint = fingerprint
        _LOGGER.info(
            "model hot-reloaded",
            extra={
                "model_version": bundle.version,
                "previous_version": previous.version,
                "training_samples": bundle.training_samples,
            },
        )

    def _log_failure(self, exc: Exception) -> None:
        now = time.monotonic()
        if now < self._next_failure_log:
            return

        self._next_failure_log = now + _FAILURE_LOG_INTERVAL_SECONDS
        _LOGGER.warning(
            "model reload rejected; keeping the current bundle: %s",
            exc,
            exc_info=not isinstance(exc, ModelSchemaError),
        )
