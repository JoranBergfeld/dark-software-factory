"""Service container — wires ports to implementations by mode."""

from __future__ import annotations

from dataclasses import dataclass

from dsf.fakes import (
    FakeConfigStore,
    FakeGitHubClient,
    FakeMemoryStore,
    FakeModelClient,
    FakeTracer,
)
from dsf.ports import (
    ConfigStore,
    GitHubClient,
    MemoryStore,
    ModelClient,
    Tracer,
)


@dataclass
class Services:
    """Bundle of every port instance, selected per mode."""

    mode: str
    model: ModelClient
    memory: MemoryStore
    config: ConfigStore
    github: GitHubClient
    tracer: Tracer


def build_services(mode: str = "local") -> Services:
    """Build a wired :class:`Services` bundle.

    ``local`` returns the deterministic in-memory fakes. ``azure`` is reserved
    for the cloud implementations authored in later phases.
    """
    if mode == "local":
        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=FakeMemoryStore(),
            config=FakeConfigStore.from_defaults(),
            github=FakeGitHubClient(),
            tracer=FakeTracer(),
        )
    raise NotImplementedError(
        f"build_services mode {mode!r} not available yet (only 'local')."
    )
