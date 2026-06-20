"""Single source of truth for the A2A-serveable source agents.

Maps each agent name to the import path of its ASGI ``app`` (``"module:attr"``).
Paths are strings so importing this registry has **no side effects** — the agent
``main`` modules build their app at import time, and the server (uvicorn) imports
the target lazily only when an agent is actually served. The CLI consumes this
registry instead of owning the list (issue #25).

The SRE agent is intentionally absent: DSF leverages the managed **Azure SRE
Agent** product (ADR 0009), onboarded interactively per product, not an
A2A-served app.
"""

from __future__ import annotations

#: agent name -> ASGI app import path (``"module:attr"``). Keys match
#: ``SourceKind.value.lower()`` (enforced by tests) so the registry and the
#: domain enum cannot silently diverge.
DEPLOYABLE_AGENTS: dict[str, str] = {
    "sentry": "dsf.agents.sentry.main:app",
    "grafana": "dsf.agents.grafana.main:app",
    "foundryiq": "dsf.agents.foundryiq.main:app",
    "webiq": "dsf.agents.webiq.main:app",
    "tickets": "dsf.agents.tickets.main:app",
    "incidents": "dsf.agents.incidents.main:app",
    "azuremonitor": "dsf.agents.azuremonitor.main:app",
}


def serveable_agents() -> list[str]:
    """Sorted names of the agents that can be served over A2A."""
    return sorted(DEPLOYABLE_AGENTS)


def app_path(name: str) -> str | None:
    """Import path of an agent's ASGI app, or ``None`` if ``name`` is unknown."""
    return DEPLOYABLE_AGENTS.get(name)


__all__ = ["DEPLOYABLE_AGENTS", "serveable_agents", "app_path"]
