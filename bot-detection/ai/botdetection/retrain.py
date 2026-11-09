"""Training pipeline with a promotion gate.

The pipeline never writes the live bundle directly: it writes a *candidate* to
the models volume, validates it by reloading it, and lets the hot-reload watcher
promote it. One writer, one reader, one hand-off mechanism.

Validation uses GroupKFold by client_id, not a random split. Requests from the
same client are heavily correlated; a random split would leak the same client
into train and test and report an F1 that production can never reach.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
import sqlite3
import threading
from dataclasses import dataclass, replace
from datetime import datetime, timezone
from pathlib import Path
from typing import Callable, Mapping, Sequence

import numpy as np
from sklearn.ensemble import HistGradientBoostingClassifier
from sklearn.metrics import f1_score, precision_score, recall_score, roc_auc_score
from sklearn.model_selection import GroupKFold

from .config import Settings, configure_logging
from .features import Observation, to_vector
from .model import ModelBundle, heuristic_bundle, load_bundle, save_bundle
from .predictor import Predictor
from .storage import SqliteStore

_LOGGER = logging.getLogger(__name__)

# A candidate must beat this absolute floor even when the current bundle has no
# metrics (heuristic). It prevents promoting a model that is merely "less bad".
_MINIMUM_F1 = 0.50


@dataclass(frozen=True, slots=True)
class TrainingResult:
    model: HistGradientBoostingClassifier
    metrics: dict[str, float]
    samples: int


class RetrainScheduler(threading.Thread):
    """Debounced, single-flight retraining trigger."""

    def __init__(self, settings: Settings, store: SqliteStore, predictor: Predictor) -> None:
        super().__init__(name="retrain-scheduler", daemon=True)
        self._settings = settings
        self._store = store
        self._predictor = predictor
        self._stop_event = threading.Event()
        self._trigger = threading.Event()
        self._train_lock = threading.Lock()

    def notify(self, inserted: int) -> None:
        if inserted <= 0 or self._trigger.is_set():
            return

        try:
            total_labelled = self._store.count_labelled()
            last_run_samples = self._store.last_training_samples()
        except sqlite3.Error:
            _LOGGER.exception("could not evaluate the retraining trigger")
            return

        if total_labelled - last_run_samples >= self._settings.retrain_min_new_samples:
            _LOGGER.info(
                "retraining threshold reached",
                extra={"samples": total_labelled - last_run_samples},
            )
            self._trigger.set()

    def request(self) -> None:
        self._trigger.set()

    def run(self) -> None:  # pragma: no cover - timing dependent
        while not self._stop_event.wait(self._settings.retrain_interval_seconds):
            if not self._trigger.is_set():
                continue

            self._trigger.clear()
            if not self._train_lock.acquire(blocking=False):
                continue

            try:
                result = maybe_train(self._store, self._settings, self._predictor)
                _LOGGER.info(
                    "retraining finished",
                    extra={
                        "promoted": result.get("promoted"),
                        "samples": result.get("samples"),
                        "f1": result.get("metrics", {}).get("f1_oof"),
                    },
                )
            except Exception:  # noqa: BLE001 - a failed run must not stop the loop
                _LOGGER.exception("retraining failed")
            finally:
                self._train_lock.release()

    def stop(self) -> None:
        self._stop_event.set()
        self.join(timeout=10.0)


def train_from_rows(rows: Sequence[Mapping[str, object]], test_folds: int = 5) -> TrainingResult:
    if len(rows) < 50:
        raise ValueError("not enough labelled samples to train")

    observations = [Observation.from_row(row) for row in rows]
    features = np.stack([to_vector(observation) for observation in observations])
    labels = np.asarray([int(row["weak_label"]) for row in rows], dtype=np.int8)
    groups = np.asarray([str(row["client_id"]) for row in rows])

    if len(np.unique(labels)) < 2:
        raise ValueError("training set contains a single class")

    unique_groups = len(np.unique(groups))
    if unique_groups < 2:
        raise ValueError("need at least two distinct clients to validate the model")

    n_splits = min(test_folds, unique_groups)
    out_of_fold = np.zeros(len(labels), dtype=np.float64)

    for train_index, test_index in GroupKFold(n_splits=n_splits).split(features, labels, groups):
        fold_model = _new_model()
        fold_model.fit(features[train_index], labels[train_index])
        out_of_fold[test_index] = fold_model.predict_proba(features[test_index])[:, 1]

    predictions = (out_of_fold >= 0.5).astype(np.int8)
    metrics = {
        "f1_oof": float(f1_score(labels, predictions, zero_division=0)),
        "precision_oof": float(precision_score(labels, predictions, zero_division=0)),
        "recall_oof": float(recall_score(labels, predictions, zero_division=0)),
        "roc_auc_oof": float(roc_auc_score(labels, out_of_fold)),
    }

    final_model = _new_model()
    final_model.fit(features, labels)

    return TrainingResult(model=final_model, metrics=metrics, samples=len(labels))


def maybe_train(
    store: SqliteStore,
    settings: Settings,
    predictor: Predictor,
    *,
    force: bool = False,
) -> dict[str, object]:
    rows = store.fetch_training_rows(settings.retrain_max_rows)
    if len(rows) < settings.retrain_min_samples:
        return {
            "promoted": False,
            "reason": "not-enough-samples",
            "samples": len(rows),
            "required": settings.retrain_min_samples,
        }

    started_at = _utc_now()
    try:
        result = train_from_rows(rows)
    except ValueError as exc:
        store.record_training_run(
            started_at=started_at,
            samples=len(rows),
            promoted=False,
            notes=str(exc),
        )
        return {"promoted": False, "reason": str(exc), "samples": len(rows)}

    current = predictor.acquire()
    current_f1 = current.metrics.get("f1_oof", 0.0)
    candidate_f1 = result.metrics["f1_oof"]
    threshold = max(current_f1 - settings.promotion_tolerance, _MINIMUM_F1)

    if not force and candidate_f1 < threshold:
        store.record_training_run(
            started_at=started_at,
            samples=result.samples,
            promoted=False,
            metrics=result.metrics,
            notes=f"candidate f1 {candidate_f1:.4f} below promotion threshold {threshold:.4f}",
        )
        return {
            "promoted": False,
            "reason": "below-threshold",
            "samples": result.samples,
            "metrics": result.metrics,
            "threshold": threshold,
            "current_version": current.version,
        }

    version = f"hgb-{datetime.now(timezone.utc):%Y%m%d-%H%M%S}"
    bundle = save_bundle(
        settings.model_path,
        result.model,
        version=version,
        trained_at=_utc_now(),
        metrics=result.metrics,
        training_samples=result.samples,
    )

    store.record_training_run(
        started_at=started_at,
        samples=result.samples,
        promoted=True,
        metrics=result.metrics,
        model_version=version,
        notes=f"promoted over {current.version}",
    )

    _LOGGER.info(
        "candidate model written; the watcher will hot-reload it",
        extra={"model_version": bundle.version, "f1": candidate_f1, "samples": result.samples},
    )

    return {
        "promoted": True,
        "version": version,
        "checksum": bundle.checksum,
        "samples": result.samples,
        "metrics": result.metrics,
        "previous_version": current.version,
    }


def _new_model() -> HistGradientBoostingClassifier:
    return HistGradientBoostingClassifier(
        max_iter=200,
        learning_rate=0.08,
        max_leaf_nodes=31,
        min_samples_leaf=20,
        l2_regularization=1.0,
        class_weight="balanced",
        random_state=42,
    )


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")


def _load_current(path: Path) -> ModelBundle:
    try:
        if Path(path).exists():
            return load_bundle(Path(path))
    except Exception:  # noqa: BLE001
        _LOGGER.exception("could not load the current bundle; assuming the heuristic model")
    return heuristic_bundle()


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Train a bot-detection model from the observation dataset.")
    parser.add_argument("--db", default=os.getenv("DATASET_PATH", "/data/observations.sqlite3"))
    parser.add_argument("--out", default=os.getenv("MODEL_PATH", "/models/bot_model.joblib"))
    parser.add_argument("--min-samples", type=int, default=None)
    parser.add_argument("--force", action="store_true", help="promote even if the candidate is not better")
    arguments = parser.parse_args(argv)

    configure_logging(os.getenv("LOG_LEVEL", "INFO"))

    settings = Settings.from_env()
    settings = replace(
        settings,
        dataset_path=Path(arguments.db),
        model_path=Path(arguments.out),
        retrain_min_samples=arguments.min_samples or settings.retrain_min_samples,
    )

    store = SqliteStore(settings.dataset_path)
    store.migrate()

    try:
        predictor = Predictor(_load_current(settings.model_path))
        result = maybe_train(store, settings, predictor, force=arguments.force)
        print(json.dumps(result, indent=2, default=str))
        return 0 if result.get("promoted") else 1
    finally:
        store.close()


if __name__ == "__main__":
    raise SystemExit(main())
