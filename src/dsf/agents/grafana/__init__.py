"""Grafana source agent (plan Task 2.3).

Surfaces metric/log anomalies (latency p99 spikes, saturation, error-rate,
log patterns) from Grafana as grounded evidence. In local/dry-run mode the
agent uses :class:`GrafanaFakeBackend` (fixture replay); in azure mode it uses
:class:`GrafanaMcpBackend`, which queries Grafana through an injected MCP
client. Runs as an Azure Container App in the product's resource group (ADR 0004).
"""

from dsf.agents.grafana.backend import GrafanaFakeBackend, GrafanaMcpBackend

__all__ = ["GrafanaFakeBackend", "GrafanaMcpBackend"]
