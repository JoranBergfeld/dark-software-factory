"""FoundryIQ agent entrypoint (plan Task 2.4).

Builds the A2A app over the fake company-knowledge backend. Run with
``uvicorn dsf.agents.foundryiq.main:app``. The azure-mode backend
(:class:`dsf.agents.foundryiq.backend.FoundryIqMcpBackend`) requires an injected
retrieve client and is selected only in azure mode.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.foundryiq.backend import FoundryIqFakeBackend, FoundryIqMcpBackend
from dsf.agents.mode import is_live, resolve_mode
from dsf.contracts.enums import SourceKind
from dsf.fakes import FakeConfigStore


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the FoundryIQ :class:`SourceAgent`, selecting the backend by mode.

    In live mode (``DSF_MODE`` set to anything but ``local``, or ``mode``
    explicitly live) the real MCP backend is wired to the Azure AI Search
    retrieve client built from env vars. Otherwise the deterministic
    fixture-backed fake is used.
    """
    cfg = config if config is not None else FakeConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.foundryiq.client import build_foundryiq_client_from_env

        backend = FoundryIqMcpBackend(retrieve=build_foundryiq_client_from_env())
    else:
        backend = FoundryIqFakeBackend()
    return SourceAgent(
        kind=SourceKind.FOUNDRYIQ,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
