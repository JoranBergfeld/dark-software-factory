"""Incidents source agent.

Surfaces incident issues the managed Azure SRE Agent files into the product
repository (issues carrying :data:`~dsf.contracts.handoff.INCIDENT_LABEL`) as
grounded evidence, aggregating recurrences into higher-confidence claims. In
local/dry-run mode it uses :class:`IncidentsFixtureBackend` (fixture replay); in
azure mode it uses :class:`IncidentsGitHubBackend`, which lists incident issues
through an injected GitHub client. Runs as an Azure Container App in the product's
resource group (ADR 0004).
"""

from dsf.agents.incidents.backend import (
    IncidentsFixtureBackend,
    IncidentsGitHubBackend,
)

__all__ = ["IncidentsFixtureBackend", "IncidentsGitHubBackend"]
