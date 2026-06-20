"""``build_test_services`` — wire a :class:`dsf.container.Services` from doubles.

Lets tests stop calling ``build_services("local")``. Each port can be overridden
via keyword; unset ports default to the honest in-memory double.
"""

from __future__ import annotations

from dsf.container import Services
from dsf.ports import ConfigStore, GitHubClient, MemoryStore, ModelClient, Tracer
from dsf_testing.config import InMemoryConfigStore
from dsf_testing.github import RecordingGitHubClient
from dsf_testing.memory import InMemoryMemoryStore
from dsf_testing.model import DeterministicModelClient
from dsf_testing.tracing import NoOpTracer


def build_test_services(
    *,
    model: ModelClient | None = None,
    memory: MemoryStore | None = None,
    config: ConfigStore | None = None,
    github: GitHubClient | None = None,
    tracer: Tracer | None = None,
    product: str | None = None,
) -> Services:
    """Build a :class:`Services` bundle wired from the in-memory doubles.

    Each port can be overridden via keyword; unset ports default to the honest
    in-memory double.
    """
    return Services(
        model=model or DeterministicModelClient(),
        memory=memory or InMemoryMemoryStore(),
        config=config or InMemoryConfigStore.from_defaults(),
        github=github or RecordingGitHubClient(),
        tracer=tracer or NoOpTracer(),
        product=product,
    )


__all__ = ["build_test_services"]
