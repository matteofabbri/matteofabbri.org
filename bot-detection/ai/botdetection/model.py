"""Model bundle, decision policy and the heuristic fallback.

A bundle is immutable and self-describing: it carries its feature schema, its
version, its checksum and its evaluation metrics. That is what makes a hot swap
safe and auditable.
"""

from __future__ import annotations

import hashlib
import io
import math
import os
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

import joblib
import numpy as np

from .config import Settings
from .features import (
    FEATURE_NAMES,
    FEATURE_SCHEMA_VERSION,
    Observation,
    explain,
    is_known_crawler,
    is_sensitive_path,
)


class ModelSchemaError(ValueError):
    """The bundle on disk does not match this process's feature schema."""


class Scorer(Protocol):
    def predict_proba(self, x: np.ndarray) -> np.ndarray: ...


@dataclass(frozen=True, slots=True)
class ModelBundle:
    scorer: Scorer
    feature_names: tuple[str, ...]
    schema_version: int
    version: str
    trained_at: str
    checksum: str
    metrics: dict[str, float]
    training_samples: int
    loaded_at: float

    def score(self, vector: np.ndarray) -> float:
        probabilities = self.scorer.predict_proba(vector.reshape(1, -1))
        row = probabilities[0]
        return float(row[1]) if row.size > 1 else float(row[0])


# Weights are in log-odds units. They are deliberately conservative: this model
# only has to keep protecting the site while the trained model is absent or the
# retraining pipeline is between runs.
_HEURISTIC_WEIGHTS: dict[str, float] = {
    "user_agent_length_scaled": -0.20,
    "user_agent_missing": 1.60,
    "user_agent_is_known_crawler": -1.80,
    "user_agent_is_browser_like": -0.70,
    "path_length_scaled": 0.10,
    "path_depth_scaled": 0.05,
    "path_is_sensitive": 2.40,
    "path_is_executable": 1.20,
    "path_has_long_token": 0.50,
    "path_has_query": 0.05,
    "log_requests_last_minute": 0.35,
    "log_distinct_paths_last_minute": 0.20,
    "distinct_path_ratio": 0.30,
    "log_errors_last_minute": 0.25,
    "log_auth_failures_last_minute": 0.40,
    "log_seconds_since_last_request": -0.05,
    "is_https": 0.0,
    "is_http2": -0.15,
    "has_referer": -0.25,
    "has_cookies": -0.20,
    "country_hash_bucket": 0.0,
    "country_is_missing": 0.20,
    "utc_hour_scaled": 0.0,
    "method_risk": 0.15,
}

_BIAS = -1.0


class HeuristicScorer:
    """Deterministic logistic model used until a trained bundle exists."""

    def __init__(self) -> None:
        missing = [name for name in FEATURE_NAMES if name not in _HEURISTIC_WEIGHTS]
        if missing:
            raise ModelSchemaError(f"heuristic weights missing for: {', '.join(missing)}")

        self._weights = np.asarray(
            [_HEURISTIC_WEIGHTS[name] for name in FEATURE_NAMES],
            dtype=np.float64,
        )

    def predict_proba(self, x: np.ndarray) -> np.ndarray:
        logit = float(np.dot(x[0].astype(np.float64), self._weights)) + _BIAS
        logit = max(min(logit, 30.0), -30.0)
        positive = 1.0 / (1.0 + math.exp(-logit))
        return np.asarray([[1.0 - positive, positive]], dtype=np.float64)


def heuristic_bundle() -> ModelBundle:
    return ModelBundle(
        scorer=HeuristicScorer(),
        feature_names=FEATURE_NAMES,
        schema_version=FEATURE_SCHEMA_VERSION,
        version="heuristic-2026.11",
        trained_at="",
        checksum="heuristic",
        metrics={},
        training_samples=0,
        loaded_at=time.time(),
    )


def load_bundle(path: Path) -> ModelBundle:
    """Load and validate a bundle. Raises :class:`ModelSchemaError` on mismatch.

    The file is read once: the checksum computed here is the checksum of the
    exact bytes that were deserialised, so the hot-reload watcher can compare it
    with the live bundle without a second read.
    """
    path = Path(path)
    raw = path.read_bytes()
    checksum = hashlib.sha256(raw).hexdigest()

    payload: Any = joblib.load(io.BytesIO(raw))
    if not isinstance(payload, dict):
        raise ModelSchemaError("bundle payload is not a dictionary")

    schema_version = int(payload.get("schema_version", -1))
    if schema_version != FEATURE_SCHEMA_VERSION:
        raise ModelSchemaError(
            f"feature schema mismatch: bundle={schema_version} runtime={FEATURE_SCHEMA_VERSION}"
        )

    feature_names = tuple(payload.get("feature_names") or ())
    if feature_names != FEATURE_NAMES:
        raise ModelSchemaError("feature names do not match the runtime schema")

    scorer = payload.get("model")
    if scorer is None or not hasattr(scorer, "predict_proba"):
        raise ModelSchemaError("bundle does not contain a scorer with predict_proba")

    return ModelBundle(
        scorer=scorer,
        feature_names=feature_names,
        schema_version=schema_version,
        version=str(payload.get("version") or f"model-{checksum[:8]}"),
        trained_at=str(payload.get("trained_at") or ""),
        checksum=checksum,
        metrics={str(key): float(value) for key, value in (payload.get("metrics") or {}).items()},
        training_samples=int(payload.get("training_samples") or 0),
        loaded_at=time.time(),
    )


def save_bundle(
    path: Path,
    scorer: Scorer,
    *,
    version: str,
    trained_at: str,
    metrics: dict[str, float],
    training_samples: int,
) -> ModelBundle:
    """Write atomically: a reader never sees a half-written joblib file.

    ``os.replace`` is atomic on the same filesystem, which is why the temporary
    file lives next to the destination and not in /tmp.
    """
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)

    payload = {
        "schema_version": FEATURE_SCHEMA_VERSION,
        "feature_names": list(FEATURE_NAMES),
        "model": scorer,
        "version": version,
        "trained_at": trained_at,
        "metrics": metrics,
        "training_samples": training_samples,
    }

    temporary = path.with_name(f"{path.name}.tmp")
    joblib.dump(payload, temporary, compress=3)
    os.replace(temporary, path)

    return load_bundle(path)


def decide(
    probability: float,
    observation: Observation,
    settings: Settings,
) -> tuple[bool, bool, list[str]]:
    """Map a probability to the two flags the site stores.

    ``is_bot`` says "automated agent"; ``is_malicious`` says "hostile". A
    legitimate crawler is a bot but not malicious; a human is neither; a
    scanner on /.env is both. Pure and testable.
    """
    signals = explain(observation)

    is_malicious = probability >= settings.malicious_threshold
    if not is_malicious and is_sensitive_path(observation.path):
        is_malicious = probability >= settings.bot_threshold

    is_bot = (
        is_malicious
        or probability >= settings.bot_threshold
        or is_known_crawler(observation.user_agent)
    )

    return is_bot, is_malicious, signals
