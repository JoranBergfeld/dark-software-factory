"""Deterministic in-memory implementations of every port (for dry-run/tests)."""

from dsf.fakes.config_store import FakeConfigStore, load_defaults
from dsf.fakes.memory import FakeMemoryStore
from dsf.fakes.model import FakeModelClient
from dsf.fakes.source import FakeSourceBackend

__all__ = [
    "FakeConfigStore",
    "FakeMemoryStore",
    "FakeModelClient",
    "FakeSourceBackend",
    "load_defaults",
]
