"""Sentry agent entrypoint (plan Task 2.2).

Builds the A2A app over the fake Sentry backend (canned fixture evidence). Run
with ``uvicorn dsf.agents.sentry.main:app``. The azure-mode MCP backend lives in
:mod:`dsf.agents.sentry.backend` and is selected only when an MCP client is
injected.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.mode import is_live, resolve_mode
from dsf.agents.sentry.backend import SentryFakeBackend, SentryMcpBackend
from dsf.contracts.enums import SourceKind
from dsf.fakes import FakeConfigStore


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the Sentry :class:`SourceAgent`, selecting the backend by mode.

    In live mode (``DSF_MODE`` set to anything but ``local``, or ``mode``
    explicitly live) the real MCP backend is wired to the Sentry HTTP client
    built from env vars. Otherwise the deterministic fixture-backed fake is used.
    """
    cfg = config if config is not None else FakeConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.sentry.client import build_sentry_client_from_env

        backend = SentryMcpBackend(mcp_call=build_sentry_client_from_env())
    else:
        backend = SentryFakeBackend()
    return SourceAgent(
        kind=SourceKind.SENTRY,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
