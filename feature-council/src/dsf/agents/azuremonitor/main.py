"""Azure Monitor agent entrypoint.

Builds the A2A app over the Azure Monitor backend. Run with
``uvicorn dsf.agents.azuremonitor.main:app``. The live backend is selected only in
azure mode; see :mod:`dsf.agents.azuremonitor.backend`.
"""

from __future__ import annotations

from dsf.agents.azuremonitor.backend import (
    AzureMonitorBackend,
    AzureMonitorFixtureBackend,
)
from dsf.agents.base import SourceAgent
from dsf.agents.mode import is_live, resolve_mode
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the Azure Monitor :class:`SourceAgent`, selecting backend by mode."""
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.azuremonitor.client import (
            build_azure_monitor_client_from_env,
        )

        backend = AzureMonitorBackend(mcp_call=build_azure_monitor_client_from_env())
    else:
        backend = AzureMonitorFixtureBackend()
    return SourceAgent(
        kind=SourceKind.AZUREMONITOR,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
