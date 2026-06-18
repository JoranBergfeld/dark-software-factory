"""FoundryIQ backends (plan Task 2.4).

FoundryIQ is the company-knowledge retrieval source: Azure AI Foundry's
knowledge index over internal docs, decisions (ADRs), roadmaps, and policies.
Its evidence answers the "have we decided/built/promised this already?" question
so the conveyor can ground proposals in real internal context.

Two backends satisfy :class:`~dsf.ports.SourceBackend`:

* :class:`FoundryIqFixtureBackend` — local/dry-run; loads canned evidence from a
  JSON fixture and never touches the network.
* :class:`FoundryIqMcpBackend` — azure mode; queries the Foundry knowledge index
  through an injected ``retrieve`` callable (an MCP/SDK client). It does no
  network I/O itself — the caller wires the transport.
"""

from __future__ import annotations

import json
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any

from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance

#: A retrieve client: given a knowledge query, return retrieved chunks. Each
#: chunk is a mapping carrying at least a summary/text and a document reference.
RetrieveFn = Callable[[str], Awaitable[list[dict[str, Any]]]]


def _fixture_path() -> Path:
    """Locate ``tests/fixtures/foundryiq_evidence.json`` at the repo root."""
    # src/dsf/agents/foundryiq/backend.py -> repo root is four parents up.
    root = Path(__file__).resolve().parents[4]
    return root / "tests" / "fixtures" / "foundryiq_evidence.json"


class FoundryIqFixtureBackend:
    """Local/dry-run FoundryIQ backend — loads canned company-knowledge evidence.

    Records each call and deserializes
    :class:`~dsf.contracts.models.EvidenceItem` objects from the JSON fixture.
    Never hits the network.
    """

    def __init__(self, fixture: str | Path | None = None) -> None:
        self.calls: list[dict] = []
        self._fixture = Path(fixture) if fixture is not None else _fixture_path()

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Return canned FoundryIQ evidence loaded from the JSON fixture."""
        self.calls.append(dict(run_scope))
        raw = json.loads(self._fixture.read_text(encoding="utf-8"))
        return [EvidenceItem.model_validate(item) for item in raw]


class FoundryIqMcpBackend:
    """Azure-mode FoundryIQ backend — queries Foundry knowledge via ``retrieve``.

    The backend queries the Azure AI Foundry company-knowledge index (FoundryIQ)
    scoped by the product registry's ``foundriq_scope`` (read from ``run_scope``
    under ``foundryiq_scope``). Retrieved chunks are mapped one-to-one onto
    :class:`~dsf.contracts.models.EvidenceItem`:

    * ``claim``        <- chunk summary/text,
    * ``raw_citation`` <- chunk document reference (doc id / URL),
    * ``provenance``   carries the exact ``query_used`` and ``FOUNDRYIQ`` kind.

    The injected ``retrieve`` callable owns the actual transport, so this class
    performs no network I/O directly. If ``retrieve`` is ``None`` it raises,
    because azure mode must never silently fabricate coverage.
    """

    def __init__(self, retrieve: RetrieveFn | None = None) -> None:
        if retrieve is None:
            raise RuntimeError(
                "FoundryIqMcpBackend requires a retrieve client (azure mode)"
            )
        self._retrieve = retrieve

    def _build_query(self, run_scope: dict) -> str:
        """Compose the knowledge query from the run scope.

        Scopes by the product registry ``foundryiq_scope`` and any product
        hints / free-text signal carried on the run scope.
        """
        scope = run_scope.get("foundryiq_scope") or ""
        hints = run_scope.get("product_hints") or []
        signal = run_scope.get("signal_text") or run_scope.get("query") or ""
        parts = [str(p) for p in (scope, *hints, signal) if p]
        return " ".join(parts).strip()

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Query Foundry knowledge and map retrieved chunks -> EvidenceItem."""
        query = self._build_query(run_scope)
        chunks = await self._retrieve(query)

        evidence: list[EvidenceItem] = []
        for chunk in chunks:
            summary = (
                chunk.get("summary")
                or chunk.get("text")
                or chunk.get("content")
                or ""
            )
            citation = (
                chunk.get("doc_ref")
                or chunk.get("doc_id")
                or chunk.get("url")
                or chunk.get("citation")
                or ""
            )
            evidence.append(
                EvidenceItem(
                    source_agent="foundryiq",
                    claim=str(summary),
                    raw_citation=str(citation),
                    provenance=Provenance(
                        query_used=query,
                        source_kind=SourceKind.FOUNDRYIQ,
                    ),
                    confidence=float(chunk.get("confidence", 0.0)),
                    product_hints=list(run_scope.get("product_hints") or []),
                )
            )
        return evidence


__all__ = ["FoundryIqFixtureBackend", "FoundryIqMcpBackend", "RetrieveFn"]
