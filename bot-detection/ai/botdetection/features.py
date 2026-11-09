"""The single, versioned feature schema.

Every input the model consumes is defined here and nowhere else. Both the gRPC
path and the retraining job call :func:`to_vector`, so a change to the schema
cannot silently desynchronise training from serving. If the schema changes,
bump ``FEATURE_SCHEMA_VERSION``: the loader refuses bundles with a different
version instead of scoring garbage.
"""

from __future__ import annotations

import hashlib
import math
from dataclasses import dataclass
from typing import Any, Mapping, Protocol
from urllib.parse import unquote

import numpy as np

FEATURE_SCHEMA_VERSION = 1

FEATURE_NAMES: tuple[str, ...] = (
    "user_agent_length_scaled",
    "user_agent_missing",
    "user_agent_is_known_crawler",
    "user_agent_is_browser_like",
    "path_length_scaled",
    "path_depth_scaled",
    "path_is_sensitive",
    "path_is_executable",
    "path_has_long_token",
    "path_has_query",
    "log_requests_last_minute",
    "log_distinct_paths_last_minute",
    "distinct_path_ratio",
    "log_errors_last_minute",
    "log_auth_failures_last_minute",
    "log_seconds_since_last_request",
    "is_https",
    "is_http2",
    "has_referer",
    "has_cookies",
    "country_hash_bucket",
    "country_is_missing",
    "utc_hour_scaled",
    "method_risk",
)

# Token list is the authoritative one. The .NET matcher is a fast pre-filter
# used only for the fail-closed policy; the label always comes from here.
SENSITIVE_TOKENS: tuple[str, ...] = (
    "/.env",
    "/.git",
    "/.svn",
    "/.hg",
    "/.aws",
    "/.ssh",
    "/.docker",
    "/.kube",
    "/.config/",
    "/.continue/",
    "/.jupyter/",
    "/notebooks/",
    "/.claude",
    "/.openai",
    "/.anthropic",
    "/.openrouter",
    "/.opencode",
    "/.minimax",
    "/.firebase",
    "wp-login",
    "wp-admin",
    "wp-config",
    "wordpress",
    "phpmyadmin",
    "xmlrpc",
    "wp-json",
    "filemanager",
    "shell.php",
    "/etc/passwd",
    "/vendor/phpunit",
    "actuator",
    "jupyter",
    "swagger-ui",
    "claude_desktop_config",
    "openai/config",
    "claude/config",
    "azure-openai",
    "bedrock/config",
    "appsettings",
    "launchsettings",
)

EXECUTABLE_EXTENSIONS: frozenset[str] = frozenset(
    {
        ".php",
        ".phtml",
        ".php3",
        ".php4",
        ".php5",
        ".phar",
        ".asp",
        ".aspx",
        ".ashx",
        ".asmx",
        ".jsp",
        ".jspx",
        ".cgi",
        ".pl",
        ".sh",
        ".bash",
        ".exe",
        ".dll",
    }
)

SENSITIVE_EXTENSIONS: frozenset[str] = frozenset(
    {
        ".env",
        ".ini",
        ".conf",
        ".config",
        ".plist",
        ".yml",
        ".yaml",
        ".sql",
        ".bak",
        ".old",
        ".backup",
        ".log",
        ".swp",
        ".tar",
        ".gz",
        ".zip",
        ".7z",
        ".rar",
        ".pem",
        ".key",
        ".crt",
    }
)

# Search-engine and monitoring agents we expect to see on a public site. Being
# on this list is a negative (benign) signal, not an allow-list: a fake Semrush
# user agent that hammers /.env is still classified as malicious.
KNOWN_CRAWLER_TOKENS: tuple[str, ...] = (
    "semrushbot",
    "mj12bot",
    "petalbot",
    "googlebot",
    "bingbot",
    "duckduckbot",
    "yandexbot",
    "baiduspider",
    "ahrefsbot",
    "claudebot",
    "gptbot",
    "applebot",
    "bingpreview",
    "facebookexternalhit",
    "linkedinbot",
    "twitterbot",
    "uptimerobot",
    "pingdom",
    "datadog",
    "munin",
    "nagios",
    "zabbix",
)

BROWSER_TOKENS: tuple[str, ...] = ("chrome/", "firefox/", "safari/", "edg/", "opr/", "version/")


class RequestLike(Protocol):
    """Anything with the proto field names (BotClassificationRequest)."""

    path: str
    user_agent: str
    country_code: str
    method: str
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


@dataclass(frozen=True, slots=True)
class Observation:
    """Raw request facts, normalised from the wire or from a database row."""

    path: str
    user_agent: str
    country_code: str
    method: str
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

    @classmethod
    def from_request(cls, request: RequestLike) -> "Observation":
        return cls(
            path=request.path or "/",
            user_agent=request.user_agent or "",
            country_code=request.country_code or "",
            method=request.method or "GET",
            path_has_query=request.path_has_query,
            has_referer=request.has_referer,
            has_cookies=request.has_cookies,
            is_https=request.is_https,
            is_http2=request.is_http2,
            requests_last_minute=max(request.requests_last_minute, 0),
            distinct_paths_last_minute=max(request.distinct_paths_last_minute, 0),
            errors_last_minute=max(request.errors_last_minute, 0),
            auth_failures_last_minute=max(request.auth_failures_last_minute, 0),
            seconds_since_last_request=float(request.seconds_since_last_request),
            utc_hour=int(request.utc_hour),
        )

    @classmethod
    def from_row(cls, row: Mapping[str, Any]) -> "Observation":
        return cls(
            path=row["path"] or "/",
            user_agent=row["user_agent"] or "",
            country_code=row["country_code"] or "",
            method=row["method"] or "GET",
            path_has_query=bool(row["path_has_query"]),
            has_referer=bool(row["has_referer"]),
            has_cookies=bool(row["has_cookies"]),
            is_https=bool(row["is_https"]),
            is_http2=bool(row["is_http2"]),
            requests_last_minute=int(row["requests_last_minute"] or 0),
            distinct_paths_last_minute=int(row["distinct_paths_last_minute"] or 0),
            errors_last_minute=int(row["errors_last_minute"] or 0),
            auth_failures_last_minute=int(row["auth_failures_last_minute"] or 0),
            seconds_since_last_request=float(row["seconds_since_last_request"] or -1.0),
            utc_hour=int(row["utc_hour"] or 0),
        )


def to_vector(observation: Observation) -> np.ndarray:
    """Build the float32 vector in ``FEATURE_NAMES`` order.

    Cost is ~5 us in CPython: a tuple of floats and one numpy conversion. The
    inference call is a single row, so no pandas object is created here.
    """
    path = observation.path
    user_agent = observation.user_agent
    extension = _extension(path)
    sensitive = is_sensitive_path(path)
    executable = extension in EXECUTABLE_EXTENSIONS
    seconds = observation.seconds_since_last_request
    requests = observation.requests_last_minute

    values = (
        min(len(user_agent), 512) / 512.0,
        1.0 if not user_agent else 0.0,
        1.0 if is_known_crawler(user_agent) else 0.0,
        1.0 if is_browser_like(user_agent) else 0.0,
        min(len(path), 512) / 512.0,
        min(path.count("/"), 16) / 16.0,
        1.0 if sensitive else 0.0,
        1.0 if executable else 0.0,
        1.0 if _has_long_token(path) else 0.0,
        1.0 if observation.path_has_query else 0.0,
        _log1p(requests),
        _log1p(observation.distinct_paths_last_minute),
        min(observation.distinct_paths_last_minute / max(requests, 1), 1.0),
        _log1p(observation.errors_last_minute),
        _log1p(observation.auth_failures_last_minute),
        # -1 means "no previous request for this client in this process".
        -1.0 if seconds < 0 else _log1p(min(seconds, 3600.0)),
        1.0 if observation.is_https else 0.0,
        1.0 if observation.is_http2 else 0.0,
        1.0 if observation.has_referer else 0.0,
        1.0 if observation.has_cookies else 0.0,
        float(_country_bucket(observation.country_code)),
        1.0 if not observation.country_code else 0.0,
        min(max(observation.utc_hour, 0), 23) / 23.0,
        _method_risk(observation.method),
    )

    return np.asarray(values, dtype=np.float32)


def explain(observation: Observation) -> list[str]:
    """Human-readable, data-free reasons attached to the response."""
    signals: list[str] = []
    path = observation.path
    user_agent = observation.user_agent

    if is_sensitive_path(path):
        signals.append("sensitive-path")
    if is_executable_path(path):
        signals.append("executable-path")
    if not user_agent:
        signals.append("missing-user-agent")
    if is_known_crawler(user_agent):
        signals.append("known-crawler")
    if observation.requests_last_minute >= 60:
        signals.append("high-request-rate")
    if observation.auth_failures_last_minute >= 3:
        signals.append("auth-failures")
    if _has_long_token(path):
        signals.append("random-path-token")

    return signals[:6]


def is_sensitive_path(path: str) -> bool:
    # Percent-decoding is an anti-evasion measure: scanners probe /%2F.env and
    # /%2F.git%2Fconfig to slip past naive prefix checks. One decode is enough;
    # the raw path is still what the model sees.
    lowered = unquote(path or "/").lower()
    if any(token in lowered for token in SENSITIVE_TOKENS):
        return True

    name = lowered.rsplit("/", 1)[-1]
    if "config" in name and name.endswith((".json", ".js", ".yml", ".yaml", ".php", ".txt", ".xml")):
        # Configuration-file exposure: /config.json, /config/s3_config.json,
        # /api/openai/config.js. Legitimate public JSON APIs do not usually carry
        # "config" in the file name; tune this rule per application.
        return True

    return _extension(lowered) in SENSITIVE_EXTENSIONS


def is_executable_path(path: str) -> bool:
    return _extension((path or "/").lower()) in EXECUTABLE_EXTENSIONS


def is_known_crawler(user_agent: str) -> bool:
    if not user_agent:
        return False
    lowered = user_agent.lower()
    return any(token in lowered for token in KNOWN_CRAWLER_TOKENS)


def is_browser_like(user_agent: str) -> bool:
    if not user_agent:
        return False
    lowered = user_agent.lower()
    return "mozilla/" in lowered and any(token in lowered for token in BROWSER_TOKENS)


def derive_weak_label(row: Mapping[str, Any]) -> int:
    """Deterministic label used to bootstrap and grow the training set.

    This is the part that makes continuous training honest: the model is not
    trained on its own predictions. A row is positive only when a cheap,
    high-precision rule fires, and every rule is able to explain itself to a
    human reviewer later.

    Note on soft-404: this site answers missing pages with HTTP 200 (an explicit
    product decision), so a 200 on /.env is a scanner reaching a fake page, not
    a legitimate page. That is why status 200 is *evidence*, not absence of it.
    """
    path = str(row["path"] or "/")
    user_agent = str(row["user_agent"] or "")
    status = int(row["http_status"] or 0)

    if is_sensitive_path(path) and status not in (301, 302, 403, 404):
        return 1

    if not user_agent and is_executable_path(path):
        return 1

    if is_executable_path(path) and int(row["auth_failures_last_minute"] or 0) >= 5:
        return 1

    if int(row["auth_failures_last_minute"] or 0) >= 10:
        return 1

    return 0


def _extension(path: str) -> str:
    name = path.split("?", 1)[0].rsplit("/", 1)[-1]
    if "." not in name or name.startswith("."):
        # A dotfile such as ".env" is matched by token, not by extension.
        return ""
    return "." + name.rsplit(".", 1)[-1].lower()


def _has_long_token(path: str) -> bool:
    for segment in path.split("/"):
        if len(segment) < 16:
            continue
        digits = sum(character.isdigit() for character in segment)
        if digits >= 3 or (any(character.isupper() for character in segment) and any(character.islower() for character in segment)):
            return True
    return False


def _country_bucket(country_code: str) -> int:
    """Stable 32-bucket hash: bounded cardinality, no growing one-hot matrix."""
    if not country_code:
        return 0
    digest = hashlib.blake2b(country_code.strip().upper().encode("utf-8"), digest_size=1).digest()
    return digest[0] % 32


def _method_risk(method: str) -> float:
    return {
        "GET": 0.0,
        "HEAD": 0.25,
        "OPTIONS": 0.5,
        "POST": 0.75,
    }.get((method or "GET").upper(), 1.0)


def _log1p(value: float) -> float:
    return math.log1p(max(float(value), 0.0))
