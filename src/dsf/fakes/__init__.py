"""Deterministic in-memory implementations of every port (for dry-run/tests)."""

from dsf.fakes.memory import FakeMemoryStore
from dsf.fakes.model import FakeModelClient
from dsf.fakes.source import FakeSourceBackend

__all__ = [
    "FakeMemoryStore",
    "FakeModelClient",
    "FakeSourceBackend",
]
