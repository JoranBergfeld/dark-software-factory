"""Wire an :class:`~dsf.sre.agent.SreAgent` from a service bundle.

The default backends are the offline fixture backends (the same ones the
feature council uses in local mode), so a wired SRE agent runs fully offline
unless real backends are injected.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.agents.grafana.backend import GrafanaFixtureBackend
from dsf.agents.sentry.backend import SentryFixtureBackend
from dsf.sre.agent import SreAgent

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.ports import SourceBackend


def build_sre_agent(
    services: Services, *, backends: list[SourceBackend] | None = None
) -> SreAgent:
    """Build an :class:`SreAgent` from ``services``.

    ``backends`` defaults to the Sentry and Grafana fixture backends so the
    agent is fully offline; pass real backends to observe live telemetry.
    """
    if backends is None:
        backends = [SentryFixtureBackend(), GrafanaFixtureBackend()]
    return SreAgent(services.github, services.memory, services.config, backends)


__all__ = ["build_sre_agent"]
