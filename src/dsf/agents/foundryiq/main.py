"""FoundryIQ agent entrypoint (plan Task 2.4).

Builds the A2A app over the fake company-knowledge backend. Run with
``uvicorn dsf.agents.foundryiq.main:app``. The azure-mode backend
(:class:`dsf.agents.foundryiq.backend.FoundryIqMcpBackend`) requires an injected
retrieve client and is selected only in azure mode.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.foundryiq.backend import FoundryIqFakeBackend
from dsf.contracts.enums import SourceKind
from dsf.fakes import FakeConfigStore


def build_agent(config: object | None = None) -> SourceAgent:
    """Build the FoundryIQ :class:`SourceAgent` over the fake backend."""
    cfg = config if config is not None else FakeConfigStore.from_defaults()
    return SourceAgent(
        kind=SourceKind.FOUNDRYIQ,
        backend=FoundryIqFakeBackend(),
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
