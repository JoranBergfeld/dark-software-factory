"""Azure Monitor source agent.

Surfaces production telemetry (Application Insights metrics, failures, and
exceptions) as grounded evidence. In local/dry-run mode the agent uses
:class:`AzureMonitorFixtureBackend` (fixture replay); in azure mode it uses
:class:`AzureMonitorBackend`, which queries Application Insights through an
injected client. Runs as an Azure Container App in the product's resource group
(ADR 0004).

The live telemetry role binding (Monitoring Reader on the product's Application
Insights resource) is a per-agent identity seam refined later; the offline path is
fully functional through the fixture backend.
"""

from dsf.agents.azuremonitor.backend import (
    AzureMonitorBackend,
    AzureMonitorFixtureBackend,
)

__all__ = ["AzureMonitorBackend", "AzureMonitorFixtureBackend"]
