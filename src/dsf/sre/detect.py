"""Detect production incidents from a batch of telemetry evidence.

The SRE fast path is deliberately simple and deterministic: keep evidence above a
confidence threshold, group it by the product it points at, and emit one
:class:`~dsf.sre.models.Incident` per (product, normalized-claim). Severity is
derived from confidence so the coding squad can prioritize. The fingerprint is a
stable hash so repeated sweeps can deduplicate.
"""

from __future__ import annotations

import hashlib

from dsf.contracts.models import EvidenceItem
from dsf.sre.models import Incident


def _severity(confidence: float) -> str:
    """Map a confidence score onto an SRE severity tier."""
    if confidence >= 0.85:
        return "sev-critical"
    if confidence >= 0.7:
        return "sev-high"
    if confidence >= 0.5:
        return "sev-medium"
    return "sev-low"


def _fingerprint(product: str, claim: str) -> str:
    """Stable 16-hex fingerprint for a (product, normalized-claim) pair."""
    normalized = " ".join(claim.lower().split())
    digest = hashlib.sha1(f"{product}|{normalized}".encode())
    return digest.hexdigest()[:16]


def detect_incidents(
    evidence: list[EvidenceItem], *, threshold: float = 0.7
) -> list[Incident]:
    """Turn evidence into incidents, dropping anything below ``threshold``.

    Evidence with no product hint is skipped — the SRE agent can only
    fix-forward to a product it can route to a repository.
    """
    incidents: list[Incident] = []
    for item in evidence:
        if item.confidence < threshold:
            continue
        if not item.product_hints:
            continue
        product = item.product_hints[0]
        claim = item.claim.strip()
        incidents.append(
            Incident(
                product=product,
                title=claim,
                summary=(
                    f"{claim} (confidence {item.confidence:.2f}, "
                    f"source {item.provenance.source_kind.value})"
                ),
                severity=_severity(item.confidence),
                citations=[item.raw_citation],
                source_kinds=[item.provenance.source_kind.value],
                fingerprint=_fingerprint(product, claim),
            )
        )
    return incidents


__all__ = ["detect_incidents"]
