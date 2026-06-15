"""Grafana agent entrypoint (plan Task 2.3).

Builds the A2A app over the fake (fixture-backed) Grafana backend. Run with
``uvicorn dsf.agents.grafana.main:app``. The MCP backend is selected only in
azure mode; see :mod:`dsf.agents.grafana.backend`.

Deployment note: this agent is designed to run inside a homelab behind NAT, so
it reaches the orchestrator outbound. That is operational only and does not
affect the code here.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.grafana.backend import GrafanaFakeBackend
from dsf.contracts.enums import SourceKind
from dsf.fakes import FakeConfigStore


def build_agent(config: object | None = None) -> SourceAgent:
    """Build the Grafana :class:`SourceAgent` over the fake backend."""
    cfg = config if config is not None else FakeConfigStore.from_defaults()
    return SourceAgent(
        kind=SourceKind.GRAFANA,
        backend=GrafanaFakeBackend(),
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
