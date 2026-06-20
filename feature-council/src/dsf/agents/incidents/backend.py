"""Incidents source backends.

The incidents source turns SRE-filed incident issues (those carrying
:data:`~dsf.contracts.handoff.INCIDENT_LABEL`) into grounded
:class:`~dsf.contracts.models.EvidenceItem` objects so the feature council can
reflect on recurring production faults and decide whether systemic hardening is
warranted.

Two backends mirror the project's local/azure split:

* :class:`IncidentsFixtureBackend` — deterministic, loads a JSON fixture; used in
  local/dry-run mode and tests. Never touches the network.
* :class:`IncidentsGitHubBackend` — azure mode; lists incident issues via an
  injected ``gh_call`` client and aggregates recurrences onto evidence. All I/O
  goes through ``gh_call``; this class never opens a socket itself.

Recurrence intelligence lives here (design Approach A): issues are grouped by a
stable signature and a repeated signature is surfaced as a single, higher-
confidence item. The conveyor's threshold then decides what to do with it; no new
conveyor stage is added.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import TYPE_CHECKING, Any

from dsf.contracts.enums import SourceKind
from dsf.contracts.handoff import INCIDENT_LABEL
from dsf.contracts.models import EvidenceItem, Provenance

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def _fixture_path() -> Path:
    """Locate ``tests/fixtures/incidents_evidence.json`` at the repo root.

    ``feature-council/src/dsf/agents/incidents/backend.py`` -> repo root is five
    parents up.
    """
    here = Path(__file__).resolve()
    return here.parents[5] / "tests" / "fixtures" / "incidents_evidence.json"


class IncidentsFixtureBackend:
    """Local/dry-run incidents backend — replays a JSON fixture."""

    def __init__(self, fixture: Path | None = None) -> None:
        self._fixture = fixture or _fixture_path()
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return evidence loaded from the fixture."""
        self.calls.append(dict(run_scope))
        raw = json.loads(self._fixture.read_text(encoding="utf-8"))
        return [EvidenceItem.model_validate(item) for item in raw]


def _signature(issue: dict) -> str:
    """Stable grouping key for an incident issue.

    Prefers an explicit ``signature`` field; otherwise normalizes the title
    (lowercased, whitespace-collapsed) so repeated incidents collapse together.
    """
    explicit = issue.get("signature")
    if explicit:
        return str(explicit).strip().lower()
    return " ".join(str(issue.get("title", "")).lower().split())


def _confidence(count: int) -> float:
    """Scale confidence by recurrence count.

    A one-off scores low (below the default 0.6 bar); each extra occurrence adds
    weight, capped so a single signature never dominates outright.
    """
    return min(0.40 + 0.15 * (count - 1), 0.95)


class IncidentsGitHubBackend:
    """Azure-mode incidents backend — lists incident issues via a GitHub client.

    ``gh_call`` is an injected async callable that returns the open issues in the
    product repository carrying :data:`INCIDENT_LABEL`. Each returned issue is a
    dict with at least ``title`` and ``html_url`` (optionally ``signature``).
    Issues are grouped by signature; each group becomes one
    :class:`EvidenceItem` whose ``confidence`` rises with the recurrence count.
    """

    def __init__(
        self,
        gh_call: Callable[[dict], Awaitable[Any]] | None,
    ) -> None:
        if gh_call is None:
            raise RuntimeError(
                "IncidentsGitHubBackend requires a gh_call client (azure mode)"
            )
        self._gh_call = gh_call

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """List incident issues, group by signature, emit aggregated evidence."""
        issues = await self._gh_call(dict(run_scope)) or []
        groups: dict[str, list[dict]] = {}
        for issue in issues:
            groups.setdefault(_signature(issue), []).append(issue)

        product_hints = list(run_scope.get("product_hints", []))
        evidence: list[EvidenceItem] = []
        for signature, members in groups.items():
            count = len(members)
            head = members[0]
            title = head.get("title", signature)
            if count > 1:
                claim = f"Incident '{title}' recurred {count} times (signature: {signature})."
            else:
                claim = f"Incident '{title}' filed once; no recurrence yet."
            evidence.append(
                EvidenceItem(
                    source_agent="incidents",
                    claim=claim,
                    raw_citation=head.get("html_url") or signature,
                    provenance=Provenance(
                        query_used=f"label:{INCIDENT_LABEL} signature={signature}",
                        source_kind=SourceKind.INCIDENTS,
                    ),
                    confidence=_confidence(count),
                    product_hints=product_hints,
                )
            )
        return evidence


__all__ = ["IncidentsFixtureBackend", "IncidentsGitHubBackend"]
