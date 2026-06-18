"""Sentry agent entrypoint (plan Task 2.2).

Builds the A2A app over the fake Sentry backend (canned fixture evidence). Run
with ``uvicorn dsf.agents.sentry.main:app``. The azure-mode MCP backend lives in
:mod:`dsf.agents.sentry.backend` and is selected only when an MCP client is
injected.
"""

from __future__ import annotations

import os

from dsf.agents.base import SourceAgent
from dsf.agents.mode import is_live, resolve_mode
from dsf.agents.sentry.backend import SentryFixtureBackend, SentryMcpBackend
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the Sentry :class:`SourceAgent`, selecting the backend by mode.

    In live mode (``DSF_MODE`` set to anything but ``local``, or ``mode``
    explicitly live) the real backend is wired:

    * ``SENTRY_MCP_URL`` set -> speak MCP to that Sentry MCP server (preferred);
    * else ``SENTRY_AUTH_TOKEN`` set -> call the Sentry REST API directly;
    * else raise (live mode must not silently fabricate coverage).

    Otherwise the deterministic fixture-backed fake is used.
    """
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        if os.environ.get("SENTRY_MCP_URL"):
            from dsf.agents.sentry.mcp_client import build_sentry_mcp_call_from_env

            backend = SentryMcpBackend(mcp_call=build_sentry_mcp_call_from_env())
        elif os.environ.get("SENTRY_AUTH_TOKEN"):
            from dsf.agents.sentry.client import build_sentry_client_from_env

            backend = SentryMcpBackend(mcp_call=build_sentry_client_from_env())
        else:
            raise RuntimeError(
                "live mode requires SENTRY_MCP_URL (MCP server) or "
                "SENTRY_AUTH_TOKEN (Sentry REST API)"
            )
    else:
        backend = SentryFixtureBackend()
    return SourceAgent(
        kind=SourceKind.SENTRY,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
