"""Agent registry — map each :class:`SourceKind` to its ``build_agent``.

The Investigation station (S2) dispatches to source agents in-process. This
registry centralizes the ``SourceKind -> build_agent`` mapping so S2 can build
exactly the enabled agents for a run without hardcoding imports.
"""

from __future__ import annotations

from collections.abc import Callable
from typing import TYPE_CHECKING

from dsf.agents.foundryiq.main import build_agent as _build_foundryiq
from dsf.agents.grafana.main import build_agent as _build_grafana
from dsf.agents.sentry.main import build_agent as _build_sentry
from dsf.agents.tickets.main import build_agent as _build_tickets
from dsf.agents.webiq.main import build_agent as _build_webiq
from dsf.contracts.enums import SourceKind

if TYPE_CHECKING:
    from dsf.agents.base import SourceAgent
    from dsf.ports import ConfigStore

#: Map a :class:`SourceKind` to its agent factory.
AGENT_BUILDERS: dict[SourceKind, Callable[[ConfigStore], SourceAgent]] = {
    SourceKind.SENTRY: _build_sentry,
    SourceKind.GRAFANA: _build_grafana,
    SourceKind.FOUNDRYIQ: _build_foundryiq,
    SourceKind.WEBIQ: _build_webiq,
    SourceKind.TICKETS: _build_tickets,
}


def build_agents(
    kinds: list[SourceKind],
    config: ConfigStore,
) -> dict[SourceKind, SourceAgent]:
    """Build agents for each kind in ``kinds`` using ``config``.

    Unknown kinds are skipped. Each agent shares the supplied ``config`` so it
    honors the live ``agent.<KIND>`` enable flag.
    """
    agents: dict[SourceKind, SourceAgent] = {}
    for kind in kinds:
        builder = AGENT_BUILDERS.get(kind)
        if builder is None:
            continue
        agents[kind] = builder(config)
    return agents


__all__ = ["AGENT_BUILDERS", "build_agents"]
