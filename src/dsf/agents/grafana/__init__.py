"""Grafana source agent (plan Task 2.3).

Surfaces metric/log anomalies (latency p99 spikes, saturation, error-rate,
log patterns) from Grafana as grounded evidence. In local/dry-run mode the
agent uses :class:`GrafanaFixtureBackend` (fixture replay); in azure mode it uses
:class:`GrafanaMcpBackend`, which queries Grafana through an injected MCP
client. Runs as an Azure Container App in the product's resource group (ADR 0004).
"""

from dsf.agents.grafana.backend import GrafanaFixtureBackend, GrafanaMcpBackend

__all__ = ["GrafanaFixtureBackend", "GrafanaMcpBackend"]
