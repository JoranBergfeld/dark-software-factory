"""Grafana agent entrypoint (plan Task 2.3).

Builds the A2A app over the fixture-backed Grafana backend. Run with
``uvicorn dsf.agents.grafana.main:app``. The MCP backend is selected only in
azure mode; see :mod:`dsf.agents.grafana.backend`.

Deployment note: this agent runs as an Azure Container App alongside the
orchestrator (ADR 0004). That is operational only and does not affect the code here.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.grafana.backend import GrafanaFixtureBackend, GrafanaMcpBackend
from dsf.agents.mode import is_live, resolve_mode
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the Grafana :class:`SourceAgent`, selecting the backend by mode.

    In live mode (``DSF_MODE`` set to anything but ``local``, or ``mode``
    explicitly live) the real MCP backend is wired to the Grafana HTTP client
    built from env vars. Otherwise the deterministic fixture-backed backend is used.
    """
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.grafana.client import build_grafana_client_from_env

        backend = GrafanaMcpBackend(mcp_call=build_grafana_client_from_env())
    else:
        backend = GrafanaFixtureBackend()
    return SourceAgent(
        kind=SourceKind.GRAFANA,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
