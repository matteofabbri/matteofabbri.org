"""SQLite dataset and training history.

The Python service is the only writer: each ClassifyBatch call appends its
observations in one transaction. WAL mode lets the retraining thread read while
RPC workers write; each thread gets its own connection because sqlite3
connections are not thread-safe.

The table stores *raw observations*, not feature vectors. Features are derived
by features.to_vector at both training and serving time, so the dataset stays
auditable and a feature change is a schema-version bump, not a migration.
"""

from __future__ import annotations

import logging
import sqlite3
import threading
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable, Mapping, Sequence

from .features import derive_weak_label, is_executable_path, is_sensitive_path

_LOGGER = logging.getLogger(__name__)

_SCHEMA = """
CREATE TABLE IF NOT EXISTS observations (
    request_id                TEXT PRIMARY KEY,
    observed_at               TEXT NOT NULL,
    client_id                 TEXT NOT NULL,
    remote_ip                 TEXT NOT NULL,
    country_code              TEXT NOT NULL DEFAULT '',
    method                    TEXT NOT NULL,
    path                      TEXT NOT NULL,
    user_agent                TEXT NOT NULL,
    path_has_query            INTEGER NOT NULL,
    has_referer               INTEGER NOT NULL,
    has_cookies               INTEGER NOT NULL,
    is_https                  INTEGER NOT NULL,
    is_http2                  INTEGER NOT NULL,
    requests_last_minute      INTEGER NOT NULL,
    distinct_paths_last_minute INTEGER NOT NULL,
    errors_last_minute        INTEGER NOT NULL,
    auth_failures_last_minute INTEGER NOT NULL,
    seconds_since_last_request REAL NOT NULL,
    utc_hour                  INTEGER NOT NULL,
    http_status               INTEGER NOT NULL,
    duration_ms               REAL NOT NULL,
    is_bot                    INTEGER NOT NULL,
    is_malicious              INTEGER NOT NULL,
    probability               REAL NOT NULL,
    model_version             TEXT NOT NULL,
    weak_label                INTEGER,
    label_source              TEXT,
    ingested_at               TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_observations_client
    ON observations (client_id, observed_at);

CREATE INDEX IF NOT EXISTS ix_observations_training
    ON observations (weak_label)
    WHERE weak_label IS NOT NULL;

CREATE TABLE IF NOT EXISTS training_runs (
    id           INTEGER PRIMARY KEY AUTOINCREMENT,
    started_at   TEXT NOT NULL,
    finished_at  TEXT,
    samples      INTEGER NOT NULL DEFAULT 0,
    f1           REAL,
    precision    REAL,
    recall       REAL,
    promoted     INTEGER NOT NULL DEFAULT 0,
    model_version TEXT,
    notes        TEXT
);
"""

_INSERT_SQL = """
INSERT OR IGNORE INTO observations (
    request_id, observed_at, client_id, remote_ip, country_code, method, path,
    user_agent, path_has_query, has_referer, has_cookies, is_https, is_http2,
    requests_last_minute, distinct_paths_last_minute, errors_last_minute,
    auth_failures_last_minute, seconds_since_last_request, utc_hour,
    http_status, duration_ms, is_bot, is_malicious, probability, model_version,
    weak_label, label_source, ingested_at
) VALUES (
    ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?,
    ?, ?, ?
)
"""


@dataclass(frozen=True, slots=True)
class ObservationRecord:
    """One classified request, ready to be appended to the dataset."""

    request_id: str
    observed_at: str
    client_id: str
    remote_ip: str
    country_code: str
    method: str
    path: str
    user_agent: str
    path_has_query: bool
    has_referer: bool
    has_cookies: bool
    is_https: bool
    is_http2: bool
    requests_last_minute: int
    distinct_paths_last_minute: int
    errors_last_minute: int
    auth_failures_last_minute: int
    seconds_since_last_request: float
    utc_hour: int
    http_status: int
    duration_ms: float
    is_bot: bool
    is_malicious: bool
    probability: float
    model_version: str


class SqliteStore:
    def __init__(self, path: Path) -> None:
        self._path = Path(path)
        self._local = threading.local()

    @property
    def _connection(self) -> sqlite3.Connection:
        connection = getattr(self._local, "connection", None)
        if connection is not None:
            return connection

        self._path.parent.mkdir(parents=True, exist_ok=True)
        connection = sqlite3.connect(self._path, timeout=10.0, isolation_level="DEFERRED")
        connection.row_factory = sqlite3.Row
        connection.execute("PRAGMA journal_mode=WAL")
        connection.execute("PRAGMA synchronous=NORMAL")
        connection.execute("PRAGMA busy_timeout=5000")
        connection.execute("PRAGMA temp_store=MEMORY")
        self._local.connection = connection
        return connection

    def migrate(self) -> None:
        connection = self._connection
        connection.executescript(_SCHEMA)
        connection.execute("PRAGMA user_version = 2")
        _LOGGER.info("dataset schema ready")

    def close(self) -> None:
        connection = getattr(self._local, "connection", None)
        if connection is not None:
            connection.close()
            self._local.connection = None

    def insert_events(self, records: Iterable[ObservationRecord]) -> int:
        """Append a batch idempotently. Returns the number of new rows.

        The batch RPC answers only after this returns, so a client retry cannot
        duplicate a row: the request_id primary key ignores it.
        """
        rows = [self._to_row(record) for record in records]
        if not rows:
            return 0

        with self._connection as connection:
            cursor = connection.executemany(_INSERT_SQL, rows)
            inserted = cursor.rowcount if cursor.rowcount is not None else 0

        return max(inserted, 0)

    def count_observations(self) -> int:
        row = self._connection.execute("SELECT COUNT(*) AS total FROM observations").fetchone()
        return int(row["total"])

    def count_labelled(self) -> int:
        row = self._connection.execute(
            "SELECT COUNT(*) AS total FROM observations WHERE weak_label IS NOT NULL"
        ).fetchone()
        return int(row["total"])

    def last_training_samples(self) -> int:
        row = self._connection.execute(
            "SELECT samples FROM training_runs WHERE finished_at IS NOT NULL ORDER BY id DESC LIMIT 1"
        ).fetchone()
        return int(row["samples"]) if row is not None else 0

    def fetch_training_rows(self, limit: int) -> Sequence[sqlite3.Row]:
        return self._connection.execute(
            "SELECT * FROM observations"
            " WHERE weak_label IS NOT NULL"
            " ORDER BY observed_at DESC LIMIT ?",
            (limit,),
        ).fetchall()

    def record_training_run(
        self,
        *,
        started_at: str,
        samples: int,
        promoted: bool,
        metrics: Mapping[str, float] | None = None,
        model_version: str | None = None,
        notes: str | None = None,
    ) -> None:
        metrics = metrics or {}
        with self._connection as connection:
            connection.execute(
                "INSERT INTO training_runs"
                " (started_at, finished_at, samples, f1, precision, recall, promoted, model_version, notes)"
                " VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)",
                (
                    started_at,
                    _utc_now(),
                    samples,
                    metrics.get("f1_oof"),
                    metrics.get("precision_oof"),
                    metrics.get("recall_oof"),
                    1 if promoted else 0,
                    model_version,
                    notes,
                ),
            )

    def _to_row(self, record: ObservationRecord) -> tuple[object, ...]:
        weak_label = derive_weak_label(
            {
                "path": record.path,
                "user_agent": record.user_agent,
                "http_status": record.http_status,
                "auth_failures_last_minute": record.auth_failures_last_minute,
            }
        )

        return (
            record.request_id,
            record.observed_at,
            record.client_id,
            record.remote_ip,
            record.country_code,
            record.method,
            record.path,
            record.user_agent,
            int(record.path_has_query),
            int(record.has_referer),
            int(record.has_cookies),
            int(record.is_https),
            int(record.is_http2),
            record.requests_last_minute,
            record.distinct_paths_last_minute,
            record.errors_last_minute,
            record.auth_failures_last_minute,
            record.seconds_since_last_request,
            record.utc_hour,
            record.http_status,
            record.duration_ms,
            int(record.is_bot),
            int(record.is_malicious),
            record.probability,
            record.model_version,
            weak_label,
            "weak_rule",
            _utc_now(),
        )


def _utc_now() -> str:
    return datetime.now(timezone.utc).isoformat(timespec="milliseconds")
