"""Azure Monitor source backends.

Surfaces production telemetry from Application Insights as grounded
:class:`~dsf.contracts.models.EvidenceItem` objects.

Two backends mirror the project's local/azure split:

* :class:`AzureMonitorFixtureBackend` — deterministic, loads a JSON fixture; used
  in local/dry-run mode and tests. Never touches the network.
* :class:`AzureMonitorBackend` — azure mode; queries Application Insights via an
  injected ``mcp_call`` client and maps results onto evidence. All I/O goes
  through ``mcp_call``; this class never opens a socket itself.
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
    """Locate ``tests/fixtures/azuremonitor_evidence.json`` at the repo root.

    ``feature-council/src/dsf/agents/azuremonitor/backend.py`` -> repo root is
    five parents up.
    """
    here = Path(__file__).resolve()
    return here.parents[5] / "tests" / "fixtures" / "azuremonitor_evidence.json"


class AzureMonitorFixtureBackend:
    """Local/dry-run Azure Monitor backend — replays a JSON fixture."""

    def __init__(self, fixture: Path | None = None) -> None:
        self._fixture = fixture or _fixture_path()
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return evidence loaded from the fixture."""
        self.calls.append(dict(run_scope))
        raw = json.loads(self._fixture.read_text(encoding="utf-8"))
        return [EvidenceItem.model_validate(item) for item in raw]


class AzureMonitorBackend:
    """Azure-mode backend — queries Application Insights via an injected client.

    ``mcp_call`` is an injected async callable that, given a query spec, returns
    Application Insights rows. The spec's KQL queries are derived from
    ``run_scope`` (or the product registry's ``azure_monitor_scope``). Each row
    maps onto an :class:`EvidenceItem`.
    """

    def __init__(
        self,
        mcp_call: Callable[[dict], Awaitable[Any]] | None,
    ) -> None:
        if mcp_call is None:
            raise RuntimeError(
                "AzureMonitorBackend requires an mcp_call client (azure mode)"
            )
        self._mcp_call = mcp_call

    def _queries(self, run_scope: dict) -> list[dict]:
        """Derive query specs from ``run_scope`` / product registry scope."""
        queries = run_scope.get("azure_monitor_queries")
        if queries:
            return list(queries)
        scope = (
            run_scope.get("product_registry", {}).get("azure_monitor_scope")
            or run_scope.get("azure_monitor_scope")
            or ""
        )
        if not scope:
            return []
        return [{"app": scope}]

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Query Application Insights via ``mcp_call`` and map rows to evidence."""
        evidence: list[EvidenceItem] = []
        for spec in self._queries(run_scope):
            results = await self._mcp_call(spec)
            for row in results or []:
                query = row.get("query") or spec.get("query") or spec.get("app", "")
                citation = row.get("link") or query
                evidence.append(
                    EvidenceItem(
                        source_agent="azuremonitor",
                        claim=row["summary"],
                        raw_citation=citation,
                        provenance=Provenance(
                            query_used=query,
                            source_kind=SourceKind.AZUREMONITOR,
                        ),
                        confidence=float(row.get("confidence", 0.0)),
                        product_hints=list(row.get("product_hints", [])),
                    )
                )
        return evidence


__all__ = ["AzureMonitorBackend", "AzureMonitorFixtureBackend"]
