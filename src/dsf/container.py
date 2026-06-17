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

    Supported modes
    ---------------
    ``local``
        Fully in-memory fakes — deterministic, no network calls, no credentials
        required.  All filing is dry-run unless explicitly overridden per-run.
    ``gh``
        Same fakes for model/memory/config/tracer, but with a real
        :class:`~dsf.github_client.RealGitHubClient` that calls the ``gh`` CLI.
        Requires ``gh`` to be authenticated in the environment.
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
    if mode == "gh":
        from dsf.github_client import RealGitHubClient

        return Services(
            mode=mode,
            model=FakeModelClient(),
            memory=FakeMemoryStore(),
            config=FakeConfigStore.from_defaults(),
            github=RealGitHubClient(),
            tracer=FakeTracer(),
        )
    raise NotImplementedError(
        f"mode {mode!r} is not yet supported (available: 'local', 'gh')."
    )
