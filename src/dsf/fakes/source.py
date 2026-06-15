"""Deterministic fake SourceBackend returning a provided evidence list."""

from __future__ import annotations

from dsf.contracts.models import EvidenceItem


class FakeSourceBackend:
    """Base fake source backend returning a fixed list of evidence."""

    def __init__(self, evidence: list[EvidenceItem] | None = None) -> None:
        self._evidence: list[EvidenceItem] = list(evidence or [])
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return a copy of the provided evidence."""
        self.calls.append(dict(run_scope))
        return list(self._evidence)
