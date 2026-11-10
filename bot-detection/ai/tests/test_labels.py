"""Training-label tests for the admin client feedback.

Run with:  PYTHONPATH=ai python -m unittest discover -s ai/tests -v
"""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

from botdetection.storage import ObservationRecord, SqliteStore


def _record(request_id: str, client_id: str, *, manual_label: int | None = None) -> ObservationRecord:
    return ObservationRecord(
        request_id=request_id,
        observed_at="2026-11-10T00:00:00.000+00:00",
        client_id=client_id,
        remote_ip="203.0.113.7",
        country_code="IT",
        method="GET",
        path="/blog",
        user_agent="Mozilla/5.0",
        path_has_query=False,
        has_referer=True,
        has_cookies=False,
        is_https=True,
        is_http2=True,
        requests_last_minute=1,
        distinct_paths_last_minute=1,
        errors_last_minute=0,
        auth_failures_last_minute=0,
        seconds_since_last_request=10.0,
        utc_hour=3,
        http_status=200,
        duration_ms=5.0,
        is_bot=True,
        is_malicious=False,
        probability=0.9,
        model_version="test",
        manual_label=manual_label,
    )


class ClientLabelTests(unittest.TestCase):
    def setUp(self) -> None:
        self._tmp = tempfile.TemporaryDirectory()
        self.store = SqliteStore(Path(self._tmp.name) / "observations.sqlite3")
        self.store.migrate()

    def tearDown(self) -> None:
        self.store.close()
        self._tmp.cleanup()

    def test_set_and_clear_client_label_rewrites_the_training_rows(self) -> None:
        self.store.insert_events([_record("r1", "aaaaaaaaaaaaaaaa"), _record("r2", "bbbbbbbbbbbbbbbb")])

        self.assertEqual({}, self.store.load_client_labels())
        self.assertEqual(1, self.store.set_client_label("aaaaaaaaaaaaaaaa", True, False, "matteo@example.org"))
        self.assertEqual({"aaaaaaaaaaaaaaaa": (True, False)}, self.store.load_client_labels())
        self.assertEqual((1, "manual"), self.store.observation_label("r1"))
        self.assertEqual("weak_rule", self.store.observation_label("r2")[1])

        self.assertEqual(1, self.store.clear_client_label("aaaaaaaaaaaaaaaa"))
        self.assertEqual({}, self.store.load_client_labels())
        self.assertEqual("weak_rule", self.store.observation_label("r1")[1])

    def test_a_new_observation_of_a_labelled_client_keeps_the_manual_label(self) -> None:
        self.store.set_client_label("aaaaaaaaaaaaaaaa", False, False, "matteo@example.org")

        self.store.insert_events([_record("r1", "aaaaaaaaaaaaaaaa", manual_label=0)])

        self.assertEqual((0, "manual"), self.store.observation_label("r1"))
