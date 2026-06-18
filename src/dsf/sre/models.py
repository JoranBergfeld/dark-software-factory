"""SRE contracts: the incident the agent fix-forwards, and a sweep result.

``SRE_LABEL`` is a provenance marker added (alongside the SP4
:data:`~dsf.contracts.handoff.HANDOFF_LABEL`) to every issue the SRE agent files,
so the coding squad triages SRE incidents through the same intake as council
issues while still being able to tell them apart.
"""

from __future__ import annotations

from pydantic import BaseModel, Field

#: Marker label on SRE-filed issues (the handoff label still drives triage).
SRE_LABEL = "sre"


class Incident(BaseModel):
    """A production incident detected from telemetry evidence."""

    product: str
    title: str
    summary: str
    severity: str
    citations: list[str] = Field(default_factory=list)
    source_kinds: list[str] = Field(default_factory=list)
    fingerprint: str


class SreSweepResult(BaseModel):
    """Outcome of one SRE sweep (observe -> detect -> fix-forward -> reflect)."""

    observed: int = 0
    incidents: int = 0
    filed: list[str] = Field(default_factory=list)
    duplicates: int = 0
    dry_run: bool = False


__all__ = ["SRE_LABEL", "Incident", "SreSweepResult"]
