"""No-op Tracer double for tests.

Records the names (and attrs) of opened spans without emitting telemetry.
"""

from __future__ import annotations

from contextlib import contextmanager
from typing import Any


class NoOpTracer:
    """Null-object tracer that records the names (and attrs) of opened spans.

    Opens and immediately closes each span without emitting telemetry.
    """

    def __init__(self) -> None:
        self.spans: list[tuple[str, dict]] = []

    @contextmanager
    def span(self, name: str, **attrs: Any):
        """Open (and immediately close) a recorded no-op span."""
        self.spans.append((name, dict(attrs)))
        yield self


__all__ = ["NoOpTracer"]
