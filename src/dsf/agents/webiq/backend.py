"""WebIQ source backends (plan Task 2.5).

Two interchangeable implementations of :class:`dsf.ports.SourceBackend` for the
external/industry web-research source:

* :class:`WebIqFixtureBackend` — local/dry-run. Loads canned, EvidenceItem-shaped
  records from ``tests/fixtures/webiq_evidence.json`` and never touches the
  network. Used by default in :mod:`dsf.agents.webiq.main`.
* :class:`WebIqMcpBackend` — azure mode. Derives industry/competitive research
  queries from ``run_scope`` and maps web search/fetch results to evidence by
  calling an *injected* ``search`` callable so it stays unit-testable and never
  hits the network in local mode.

Azure-mode contract (documented; not invoked here):
    In azure mode ``search`` is a web search/fetch client (e.g. a Bing/Grounding
    or MCP-backed web tool). The backend builds one or more industry queries from
    ``run_scope`` — product hints plus competitive/market framing (e.g.
    ``"<product> competitor feature release"`` or
    ``"<product> industry trend market signal"``) — and calls ``search`` to
    retrieve external findings. Each result is mapped to one EvidenceItem::

        EvidenceItem(
            source_agent="webiq",
            claim=<the web finding / summary>,
            raw_citation=<the external source URL>,
            provenance=Provenance(query_used=<the query>, source_kind=WEBIQ),
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

# tests/fixtures/webiq_evidence.json relative to repo root.
# backend.py -> webiq -> agents -> dsf -> src -> <repo root>.
_FIXTURE_PATH = (
    Path(__file__).resolve().parents[4] / "tests" / "fixtures" / "webiq_evidence.json"
)


class WebIqFixtureBackend:
    """Local/dry-run WebIQ backend — loads canned evidence from a fixture.

    Records each call on ``.calls`` and satisfies the
    :class:`~dsf.ports.SourceBackend` protocol. Never performs any network/web
    I/O.
    """

    def __init__(self, fixture_path: Path | None = None) -> None:
        self.fixture_path = fixture_path or _FIXTURE_PATH
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return the fixture's evidence items."""
        self.calls.append(dict(run_scope))
        data = json.loads(self.fixture_path.read_text(encoding="utf-8"))
        return [EvidenceItem(**d) for d in data]


class WebIqMcpBackend:
    """Azure-mode WebIQ backend — maps web research results to EvidenceItems.

    The web client is injected as ``search`` so this stays testable and never
    hits the network in local mode. ``search`` is an async callable taking a
    query string, e.g.::

        await search("microbi competitor feature release 2026")

    It is expected to return a list of result-shaped dicts (each with at least a
    finding/summary under ``finding`` / ``snippet`` / ``title`` and a source URL
    under ``url`` / ``source_url``).
    """

    def __init__(
        self,
        search: Callable[..., Awaitable[Any]] | None = None,
    ) -> None:
        if search is None:
            raise RuntimeError("WebIqMcpBackend requires a search client (azure mode)")
        self._search = search
        self.calls: list[dict] = []

    def _queries(self, run_scope: dict) -> list[str]:
        """Derive industry/competitive research queries from the run scope.

        Honors an explicit ``run_scope["webiq_query"]`` (string or list) when
        present; otherwise builds competitive/market queries per product hint.
        """
        explicit = run_scope.get("webiq_query")
        if isinstance(explicit, str):
            return [explicit]
        if isinstance(explicit, list) and explicit:
            return [str(q) for q in explicit]

        product_hints = list(run_scope.get("product_hints", []))
        if not product_hints:
            return ["industry trend competitive market signal"]
        return [
            f"{hint} competitor feature release industry trend market signal"
            for hint in product_hints
        ]

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Run industry web research via the injected client and map findings.

        For each derived query, ``search`` is invoked (in azure mode this is the
        web search/fetch tool; in tests a tiny injected client returning canned result
        dicts). Each result is mapped to one EvidenceItem whose ``raw_citation``
        is the external source URL and whose provenance records the exact query
        used.
        """
        self.calls.append(dict(run_scope))

        product_hints = list(run_scope.get("product_hints", []))
        evidence: list[EvidenceItem] = []

        for query in self._queries(run_scope):
            results = await self._search(query)
            for result in results or []:
                finding = (
                    result.get("finding")
                    or result.get("snippet")
                    or result.get("title")
                    or "Industry web finding"
                )
                url = result.get("url") or result.get("source_url") or ""
                evidence.append(
                    EvidenceItem(
                        source_agent="webiq",
                        claim=finding,
                        raw_citation=url,
                        provenance=Provenance(
                            query_used=query, source_kind=SourceKind.WEBIQ
                        ),
                        confidence=float(result.get("confidence", 0.6)),
                        product_hints=product_hints,
                    )
                )
        return evidence


__all__ = ["WebIqFixtureBackend", "WebIqMcpBackend"]
