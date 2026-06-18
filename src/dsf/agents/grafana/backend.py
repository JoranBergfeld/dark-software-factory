"""Grafana source backends (plan Task 2.3).

The Grafana source surfaces metric/log anomalies (latency p99 spikes,
saturation, error-rate jumps, log patterns) as grounded
:class:`~dsf.contracts.models.EvidenceItem` objects.

Deployment note: in production this agent runs as an Azure Container App alongside
the orchestrator (ADR 0004). That is an operational detail only — it does not change
any code here.

Two backends mirror the project's local/azure split:

* :class:`GrafanaFakeBackend` — deterministic, loads a JSON fixture; used in
  local/dry-run mode and tests. Never touches the network.
* :class:`GrafanaMcpBackend` — azure mode; calls Grafana via an injected
  ``mcp_call`` client and maps the results onto evidence. Never touches the
  network directly either (all I/O goes through ``mcp_call``).
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import TYPE_CHECKING, Any

from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def _fixture_path() -> Path:
    """Locate ``tests/fixtures/grafana_evidence.json`` at the repo root.

    ``src/dsf/agents/grafana/backend.py`` -> repo root is four parents up.
    """
    here = Path(__file__).resolve()
    return here.parents[4] / "tests" / "fixtures" / "grafana_evidence.json"


class GrafanaFakeBackend:
    """Local/dry-run Grafana backend — replays a JSON fixture.

    Satisfies the :class:`~dsf.ports.SourceBackend` protocol. Records each call
    for assertions and returns freshly-parsed :class:`EvidenceItem` objects so
    the conveyor can run end-to-end without a live Grafana.
    """

    def __init__(self, fixture: Path | None = None) -> None:
        self._fixture = fixture or _fixture_path()
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return evidence loaded from the fixture."""
        self.calls.append(dict(run_scope))
        raw = json.loads(self._fixture.read_text(encoding="utf-8"))
        return [EvidenceItem.model_validate(item) for item in raw]


class GrafanaMcpBackend:
    """Azure-mode Grafana backend — queries Grafana via an MCP client.

    ``mcp_call`` is an injected async callable that fronts the Grafana MCP
    server. Given a query spec it returns Grafana results (metric series, log
    matches, or dashboard panels). This backend asks it to evaluate the
    PromQL/LogQL queries (or dashboards) derived from ``run_scope`` — falling
    back to the product registry's ``grafana_dashboards`` — and maps each
    returned anomaly onto an :class:`EvidenceItem`:

    * ``claim``        -> the anomaly summary returned by Grafana,
    * ``raw_citation`` -> the panel URL or the PromQL/LogQL expression,
    * ``provenance``   -> :class:`Provenance` with ``query_used`` set to the
      query that surfaced the anomaly and ``source_kind`` = ``GRAFANA``.

    All network I/O is delegated to ``mcp_call``; this class never opens a
    socket itself. If ``mcp_call`` is ``None`` it raises immediately rather
    than silently fabricating coverage.
    """

    def __init__(
        self,
        mcp_call: Callable[[dict], Awaitable[Any]] | None,
    ) -> None:
        if mcp_call is None:
            raise RuntimeError(
                "GrafanaMcpBackend requires an mcp_call client (azure mode)"
            )
        self._mcp_call = mcp_call

    def _queries(self, run_scope: dict) -> list[dict]:
        """Derive the queries to run from ``run_scope`` / product registry.

        Prefers explicit ``grafana_queries`` on the scope; otherwise expands the
        product registry's ``grafana_dashboards`` entries. Each query is a small
        spec dict the ``mcp_call`` client understands.
        """
        queries = run_scope.get("grafana_queries")
        if queries:
            return list(queries)
        dashboards = (
            run_scope.get("product_registry", {}).get("grafana_dashboards")
            or run_scope.get("grafana_dashboards")
            or []
        )
        return [{"dashboard": d} for d in dashboards]

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Query Grafana via ``mcp_call`` and map anomalies to evidence."""
        evidence: list[EvidenceItem] = []
        for spec in self._queries(run_scope):
            results = await self._mcp_call(spec)
            for row in results or []:
                query = row.get("query") or spec.get("query") or spec.get("dashboard", "")
                citation = row.get("panel_url") or query
                evidence.append(
                    EvidenceItem(
                        source_agent="grafana",
                        claim=row["summary"],
                        raw_citation=citation,
                        provenance=Provenance(
                            query_used=query,
                            source_kind=SourceKind.GRAFANA,
                        ),
                        confidence=float(row.get("confidence", 0.0)),
                        product_hints=list(row.get("product_hints", [])),
                    )
                )
        return evidence


__all__ = ["GrafanaFakeBackend", "GrafanaMcpBackend"]
