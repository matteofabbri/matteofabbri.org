"""Unit tests for the feature schema and the decision policy.

Run with:  PYTHONPATH=ai python -m unittest discover -s ai/tests -v
"""

from __future__ import annotations

import unittest

import numpy as np

from botdetection.config import Settings
from botdetection.features import (
    FEATURE_NAMES,
    FEATURE_SCHEMA_VERSION,
    Observation,
    derive_weak_label,
    is_browser_like,
    is_executable_path,
    is_known_crawler,
    is_sensitive_path,
    to_vector,
)
from botdetection.model import HeuristicScorer, decide, heuristic_bundle
from botdetection.predictor import Predictor


def _observation(**overrides: object) -> Observation:
    defaults: dict[str, object] = {
        "path": "/blog/matteo-fabbri-dot-org",
        "user_agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/152.0.0.0 Safari/537.36",
        "country_code": "IT",
        "method": "GET",
        "path_has_query": False,
        "has_referer": True,
        "has_cookies": True,
        "is_https": True,
        "is_http2": True,
        "requests_last_minute": 3,
        "distinct_paths_last_minute": 2,
        "errors_last_minute": 0,
        "auth_failures_last_minute": 0,
        "seconds_since_last_request": 12.0,
        "utc_hour": 10,
    }
    defaults.update(overrides)
    return Observation(**defaults)  # type: ignore[arg-type]


class PathClassifierTests(unittest.TestCase):
    def test_sensitive_paths_are_detected(self) -> None:
        for path in ("/.env", "/.git/config", "/.git/HEAD", "/wp-login.php", "/backups/.env",
                     "/phpmyadmin/index.php", "/%2F.env", "/.claude/config.json",
                     "/appsettings.Production.json", "/actuator/heapdump",
                     "/config.json", "/config/s3_config.json", "/minimax-config.json"):
            with self.subTest(path=path):
                self.assertTrue(is_sensitive_path(path))

    def test_normal_paths_are_not_sensitive(self) -> None:
        for path in ("/", "/blog/post", "/sitemap.xml", "/requests", "/css/blog.css",
                     "/.well-known/security.txt", "/blog/configuration-management"):
            with self.subTest(path=path):
                self.assertFalse(is_sensitive_path(path))

    def test_executable_extensions(self) -> None:
        self.assertTrue(is_executable_path("/1.php"))
        self.assertTrue(is_executable_path("/wp-content/plugins/x/filemanager.php"))
        self.assertFalse(is_executable_path("/blog/post"))
        self.assertFalse(is_executable_path("/app.js"))


class UserAgentTests(unittest.TestCase):
    def test_known_crawlers(self) -> None:
        self.assertTrue(is_known_crawler("Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)"))
        self.assertTrue(is_known_crawler("Mozilla/5.0 (compatible; MJ12bot/v1.4.8; http://mj12bot.com/)"))
        self.assertTrue(is_known_crawler("Munin - http_loadtime"))
        self.assertFalse(is_known_crawler("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/152.0.0.0"))

    def test_browser_detection(self) -> None:
        self.assertTrue(is_browser_like("Mozilla/5.0 (X11; Linux x86_64) Firefox/141.0"))
        self.assertFalse(is_browser_like("Go-http-client/1.1"))
        self.assertFalse(is_browser_like(""))


class VectorTests(unittest.TestCase):
    def test_vector_is_finite_and_aligned(self) -> None:
        vector = to_vector(_observation())
        self.assertEqual(vector.shape, (len(FEATURE_NAMES),))
        self.assertEqual(vector.dtype, np.float32)
        self.assertTrue(np.isfinite(vector).all())

    def test_missing_user_agent_is_encoded(self) -> None:
        vector = to_vector(_observation(user_agent=""))
        self.assertEqual(vector[FEATURE_NAMES.index("user_agent_missing")], 1.0)

    def test_schema_version_is_stable(self) -> None:
        self.assertEqual(FEATURE_SCHEMA_VERSION, 1)


class LabelTests(unittest.TestCase):
    def test_sensitive_path_with_soft_404_is_positive(self) -> None:
        # The site answers missing pages with 200; a 200 on /.env is a scanner.
        label = derive_weak_label(
            {"path": "/.env", "user_agent": "curl/8.0", "http_status": 200, "auth_failures_last_minute": 0}
        )
        self.assertEqual(label, 1)

    def test_normal_page_is_negative(self) -> None:
        label = derive_weak_label(
            {"path": "/blog/post", "user_agent": "Mozilla/5.0 Chrome/152.0", "http_status": 200, "auth_failures_last_minute": 0}
        )
        self.assertEqual(label, 0)

    def test_repeated_auth_failures_are_positive(self) -> None:
        label = derive_weak_label(
            {"path": "/login", "user_agent": "Mozilla/5.0 Chrome/152.0", "http_status": 200, "auth_failures_last_minute": 12}
        )
        self.assertEqual(label, 1)


class DecisionTests(unittest.TestCase):
    def setUp(self) -> None:
        self.settings = Settings()
        self.scorer = HeuristicScorer()

    def test_heuristic_flags_env_without_user_agent(self) -> None:
        observation = _observation(path="/.env", user_agent="", has_referer=False, has_cookies=False)
        probability = float(self.scorer.predict_proba(to_vector(observation).reshape(1, -1))[0, 1])
        is_bot, is_malicious, _ = decide(probability, observation, self.settings)
        self.assertTrue(is_bot)
        self.assertTrue(is_malicious)

    def test_heuristic_leaves_a_normal_browser_alone(self) -> None:
        observation = _observation()
        probability = float(self.scorer.predict_proba(to_vector(observation).reshape(1, -1))[0, 1])
        is_bot, is_malicious, _ = decide(probability, observation, self.settings)
        self.assertFalse(is_bot)
        self.assertFalse(is_malicious)

    def test_known_crawler_is_a_bot_but_not_malicious(self) -> None:
        observation = _observation(
            path="/sitemap.xml",
            user_agent="Mozilla/5.0 (compatible; SemrushBot/7~bl; +http://www.semrush.com/bot.html)",
            has_referer=False,
            has_cookies=False,
            requests_last_minute=5,
        )
        is_bot, is_malicious, signals = decide(0.05, observation, self.settings)
        self.assertTrue(is_bot)
        self.assertFalse(is_malicious)
        self.assertIn("known-crawler", signals)


class PredictorTests(unittest.TestCase):
    def test_swap_is_visible_to_readers(self) -> None:
        original = heuristic_bundle()
        replacement = heuristic_bundle()
        predictor = Predictor(original)

        previous = predictor.swap(replacement)

        self.assertIs(previous, original)
        self.assertIs(predictor.acquire(), replacement)
        self.assertEqual(predictor.reload_count, 1)


if __name__ == "__main__":
    unittest.main()
