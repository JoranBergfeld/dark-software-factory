"""No-op fake Tracer that records span names."""

from __future__ import annotations

from contextlib import contextmanager
from typing import Any


class FakeTracer:
    """No-op tracer that records the names (and attrs) of opened spans."""

    def __init__(self) -> None:
        self.spans: list[tuple[str, dict]] = []

    @contextmanager
    def span(self, name: str, **attrs: Any):
        """Open (and immediately close) a recorded no-op span."""
        self.spans.append((name, dict(attrs)))
        yield self
