"""Sentry source backends (plan Task 2.2).

Two interchangeable implementations of :class:`dsf.ports.SourceBackend`:

* :class:`SentryFakeBackend` — local/dry-run. Loads canned, EvidenceItem-shaped
  records from ``tests/fixtures/sentry_evidence.json`` and never touches the
  network. Used by default in :mod:`dsf.agents.sentry.main`.
* :class:`SentryMcpBackend` — azure mode. Maps a Sentry query to evidence by
  calling Sentry MCP tools through an *injected* ``mcp_call`` callable so it
  stays unit-testable and never hits the network in local mode.

Azure-mode contract (documented; not invoked here):
    In azure mode ``mcp_call`` is the Sentry MCP client. The backend issues a
    Sentry query against the organization/project(s) resolved from
    ``run_scope`` (or the product registry's ``sentry_projects`` mapping) and
    calls the Sentry MCP tools:

      * ``search_issues``       — find issues matching the query (regressions,
                                  high-frequency errors, newly seen issues);
      * ``search_events``       — drill into event counts / impacted users;
      * ``get_issue_tag_values``— enrich with release / environment tags.

    Each returned issue is mapped to one EvidenceItem::

        EvidenceItem(
            source_agent="sentry",
            claim=f"{issue.title} ({issue.count} events, {issue.users} users)",
            raw_citation=issue.permalink,
            provenance=Provenance(query_used=<the query>, source_kind=SENTRY),
            ...
        )
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import TYPE_CHECKING, Any

from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

# tests/fixtures/sentry_evidence.json relative to repo root.
# backend.py -> sentry -> agents -> dsf -> src -> <repo root>.
_FIXTURE_PATH = (
    Path(__file__).resolve().parents[4] / "tests" / "fixtures" / "sentry_evidence.json"
)


class SentryFakeBackend:
    """Local/dry-run Sentry backend — loads canned evidence from a fixture.

    Shaped like :class:`dsf.fakes.source.FakeSourceBackend`: records each call
    on ``.calls`` and satisfies the :class:`~dsf.ports.SourceBackend` protocol.
    Never performs any network/MCP I/O.
    """

    def __init__(self, fixture_path: Path | None = None) -> None:
        self.fixture_path = fixture_path or _FIXTURE_PATH
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return the fixture's evidence items."""
        self.calls.append(dict(run_scope))
        data = json.loads(self.fixture_path.read_text(encoding="utf-8"))
        return [EvidenceItem(**d) for d in data]


class SentryMcpBackend:
    """Azure-mode Sentry backend — maps a Sentry query to EvidenceItems.

    The MCP client is injected as ``mcp_call`` so this stays testable and never
    hits the network in local mode. ``mcp_call`` is an async callable taking the
    MCP tool name and keyword arguments, e.g.::

        await mcp_call("search_issues", organization_slug=..., query=...)

    It is expected to return a list of issue-shaped dicts (each with at least
    ``title``, ``permalink``, and optionally ``count`` / ``user_count``).
    """

    def __init__(
        self,
        mcp_call: Callable[..., Awaitable[Any]] | None = None,
    ) -> None:
        if mcp_call is None:
            raise RuntimeError("SentryMcpBackend requires an mcp_call client (azure mode)")
        self._mcp_call = mcp_call
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Query Sentry via the injected MCP client and map issues to evidence.

        Resolution of org/project and the Sentry query string:

        * ``organization`` / ``project`` come from ``run_scope`` when present,
          otherwise from the product registry's ``sentry_projects`` mapping
          (keyed by product hint).
        * The query defaults to surfacing actionable signal — unresolved
          regressions / high-frequency issues — and can be overridden via
          ``run_scope["sentry_query"]``.

        Each issue returned by ``search_issues`` is mapped to one EvidenceItem
        whose ``raw_citation`` is the issue permalink and whose provenance
        records the exact query used.
        """
        self.calls.append(dict(run_scope))

        product_hints = list(run_scope.get("product_hints", []))
        organization = run_scope.get("organization")
        sentry_projects: dict[str, str] = run_scope.get("sentry_projects", {})
        project = run_scope.get("project")
        if project is None and product_hints:
            project = sentry_projects.get(product_hints[0])

        query = run_scope.get("sentry_query", "is:unresolved is:regression")

        # In azure mode this is the Sentry MCP ``search_issues`` tool. In tests
        # it is a tiny fake returning canned issue dicts.
        issues = await self._mcp_call(
            "search_issues",
            organization_slug=organization,
            project_slug=project,
            query=query,
        )

        evidence: list[EvidenceItem] = []
        for issue in issues or []:
            title = issue.get("title", "Unknown Sentry issue")
            count = issue.get("count")
            users = issue.get("user_count")
            permalink = issue.get("permalink", "")

            claim = title
            stats: list[str] = []
            if count is not None:
                stats.append(f"{count} events")
            if users is not None:
                stats.append(f"{users} users")
            if stats:
                claim = f"{title} ({', '.join(stats)})"

            evidence.append(
                EvidenceItem(
                    source_agent="sentry",
                    claim=claim,
                    raw_citation=permalink,
                    provenance=Provenance(query_used=query, source_kind=SourceKind.SENTRY),
                    confidence=float(issue.get("confidence", 0.7)),
                    product_hints=product_hints,
                )
            )
        return evidence


__all__ = ["SentryFakeBackend", "SentryMcpBackend"]
