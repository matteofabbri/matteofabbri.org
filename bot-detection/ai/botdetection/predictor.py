"""Thread-safe holder of the active model bundle.

The concurrency contract is small and deliberate:

* a reader acquires the lock only to copy a reference, then releases it before
  running inference. Inference on a large model can take milliseconds; holding
  the lock for that long would serialise every worker behind the slowest one.
* a writer (the hot-reload watcher) swaps the reference under the same lock.
  Swapping is a pointer assignment, so it is atomic with respect to readers.
* the bundle itself is frozen, so a reader can never observe a partially
  initialised model.
"""

from __future__ import annotations

import threading
import time

from .model import ModelBundle


class Predictor:
    def __init__(self, bundle: ModelBundle) -> None:
        self._lock = threading.Lock()
        self._bundle = bundle
        self._reloads = 0
        self._started_at = time.monotonic()

    def acquire(self) -> ModelBundle:
        """Return the current immutable bundle. Lock held for one read."""
        with self._lock:
            return self._bundle

    def swap(self, bundle: ModelBundle) -> ModelBundle:
        with self._lock:
            previous = self._bundle
            self._bundle = bundle
            self._reloads += 1
            return previous

    @property
    def reload_count(self) -> int:
        with self._lock:
            return self._reloads

    @property
    def uptime_seconds(self) -> float:
        return time.monotonic() - self._started_at
