"""Sentry agent entrypoint (plan Task 2.2).

Builds the A2A app over the fake Sentry backend (canned fixture evidence). Run
with ``uvicorn dsf.agents.sentry.main:app``. The azure-mode MCP backend lives in
:mod:`dsf.agents.sentry.backend` and is selected only when an MCP client is
injected.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.sentry.backend import SentryFakeBackend
from dsf.contracts.enums import SourceKind
from dsf.fakes import FakeConfigStore


def build_agent(config: object | None = None) -> SourceAgent:
    """Build the Sentry :class:`SourceAgent` over the fake backend."""
    cfg = config if config is not None else FakeConfigStore.from_defaults()
    return SourceAgent(
        kind=SourceKind.SENTRY,
        backend=SentryFakeBackend(),
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
