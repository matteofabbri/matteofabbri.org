"""Environment configuration and structured logging."""

from __future__ import annotations

import json
import logging
import os
import sys
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


@dataclass(frozen=True, slots=True)
class Settings:
    """Immutable runtime configuration, built once at process start."""

    grpc_port: int = 50051
    http_port: int = 8081
    grpc_workers: int = 16
    max_concurrent_rpcs: int = 256

    model_path: Path = Path("/models/bot_model.joblib")
    dataset_path: Path = Path("/data/observations.sqlite3")
    reload_interval_seconds: float = 1.0

    log_level: str = "INFO"
    enable_reflection: bool = False

    # Label thresholds. `is_malicious` needs a higher score than `is_bot`;
    # a sensitive path is malicious already at the bot threshold.
    malicious_threshold: float = 0.90
    bot_threshold: float = 0.60

    # Continuous retraining.
    retrain_min_samples: int = 500
    retrain_max_rows: int = 200_000
    retrain_interval_seconds: float = 60.0
    retrain_min_new_samples: int = 200
    promotion_tolerance: float = 0.01

    @classmethod
    def from_env(cls) -> "Settings":
        return cls(
            grpc_port=_env_int("BOT_GRPC_PORT", 50051),
            http_port=_env_int("BOT_HTTP_PORT", 8081),
            grpc_workers=_env_int("BOT_GRPC_WORKERS", 16),
            max_concurrent_rpcs=_env_int("BOT_MAX_CONCURRENT_RPCS", 256),
            model_path=Path(os.getenv("MODEL_PATH", "/models/bot_model.joblib")),
            dataset_path=Path(os.getenv("DATASET_PATH", "/data/observations.sqlite3")),
            reload_interval_seconds=_env_float("MODEL_RELOAD_INTERVAL", 1.0),
            log_level=os.getenv("LOG_LEVEL", "INFO"),
            enable_reflection=_env_bool("GRPC_REFLECTION", False),
            malicious_threshold=_env_float("MALICIOUS_THRESHOLD", 0.90),
            bot_threshold=_env_float("BOT_THRESHOLD", 0.60),
            retrain_min_samples=_env_int("RETRAIN_MIN_SAMPLES", 500),
            retrain_max_rows=_env_int("RETRAIN_MAX_ROWS", 200_000),
            retrain_interval_seconds=_env_float("RETRAIN_INTERVAL_SECONDS", 60.0),
            retrain_min_new_samples=_env_int("RETRAIN_MIN_NEW_SAMPLES", 200),
            promotion_tolerance=_env_float("MODEL_PROMOTION_TOLERANCE", 0.01),
        )


class JsonFormatter(logging.Formatter):
    """One JSON object per line: ingestible by Loki/ELK without a parser."""

    _EXTRA_FIELDS = (
        "request_id",
        "client_id",
        "model_version",
        "samples",
        "f1",
        "promoted",
    )

    def format(self, record: logging.LogRecord) -> str:
        payload: dict[str, object] = {
            "ts": datetime.now(timezone.utc).isoformat(timespec="milliseconds"),
            "level": record.levelname,
            "logger": record.name,
            "message": record.getMessage(),
        }

        for field in self._EXTRA_FIELDS:
            value = getattr(record, field, None)
            if value is not None:
                payload[field] = value

        if record.exc_info:
            payload["exception"] = self.formatException(record.exc_info)

        return json.dumps(payload, ensure_ascii=False, separators=(",", ":"))


def configure_logging(level: str) -> None:
    handler = logging.StreamHandler(sys.stdout)
    handler.setFormatter(JsonFormatter())

    root = logging.getLogger()
    root.handlers.clear()
    root.addHandler(handler)
    root.setLevel(getattr(logging, level.upper(), logging.INFO))


def _env_int(name: str, default: int) -> int:
    raw = os.getenv(name)
    return default if raw is None or raw == "" else int(raw)


def _env_float(name: str, default: float) -> float:
    raw = os.getenv(name)
    return default if raw is None or raw == "" else float(raw)


def _env_bool(name: str, default: bool) -> bool:
    raw = os.getenv(name)
    if raw is None or raw == "":
        return default
    return raw.strip().lower() in {"1", "true", "yes", "on"}
