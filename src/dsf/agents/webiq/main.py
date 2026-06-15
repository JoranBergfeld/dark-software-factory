"""WebIQ agent entrypoint (plan Task 2.5).

Builds the A2A app over the fake (fixture-backed) WebIQ backend. Run with
``uvicorn dsf.agents.webiq.main:app``. The azure-mode backend
(:class:`dsf.agents.webiq.backend.WebIqMcpBackend`) is selected only in azure
mode and requires an injected web ``search`` client.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.webiq.backend import WebIqFakeBackend
from dsf.contracts.enums import SourceKind
from dsf.fakes import FakeConfigStore


def build_agent(config: object | None = None) -> SourceAgent:
    """Build the WebIQ :class:`SourceAgent` over the fake backend."""
    cfg = config if config is not None else FakeConfigStore.from_defaults()
    return SourceAgent(
        kind=SourceKind.WEBIQ,
        backend=WebIqFakeBackend(),
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
