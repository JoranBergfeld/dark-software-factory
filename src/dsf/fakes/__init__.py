"""Deterministic in-memory implementations of every port (for dry-run/tests)."""

from dsf.fakes.config_store import FakeConfigStore, load_defaults
from dsf.fakes.github import FakeGitHubClient
from dsf.fakes.memory import FakeMemoryStore
from dsf.fakes.model import FakeModelClient
from dsf.fakes.source import FakeSourceBackend
from dsf.fakes.tracer import FakeTracer

__all__ = [
    "FakeConfigStore",
    "FakeGitHubClient",
    "FakeMemoryStore",
    "FakeModelClient",
    "FakeSourceBackend",
    "FakeTracer",
    "load_defaults",
]
