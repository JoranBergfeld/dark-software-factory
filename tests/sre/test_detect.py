"""Tests for SRE incident detection from telemetry evidence."""

from __future__ import annotations

from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance
from dsf.sre.detect import detect_incidents


def _ev(
    claim: str,
    confidence: float,
    *,
    product: str | None = "microbi",
    source: SourceKind = SourceKind.SENTRY,
) -> EvidenceItem:
    return EvidenceItem(
        source_agent="sentry",
        claim=claim,
        raw_citation="https://sentry.example/issues/1",
        provenance=Provenance(query_used="errors", source_kind=source),
        confidence=confidence,
        product_hints=[product] if product else [],
    )


def test_below_threshold_dropped() -> None:
    incidents = detect_incidents([_ev("checkout 500s spiking", 0.6)])
    assert incidents == []


def test_custom_threshold_allows_lower_confidence() -> None:
    incidents = detect_incidents([_ev("checkout 500s spiking", 0.6)], threshold=0.5)
    assert len(incidents) == 1


def test_severity_mapping() -> None:
    (crit,) = detect_incidents([_ev("db down", 0.95)])
    (high,) = detect_incidents([_ev("latency p99 high", 0.75)])
    (med,) = detect_incidents([_ev("minor blips", 0.55)], threshold=0.5)
    assert crit.severity == "sev-critical"
    assert high.severity == "sev-high"
    assert med.severity == "sev-medium"


def test_grouped_by_first_product_hint() -> None:
    incidents = detect_incidents(
        [
            _ev("checkout 500s", 0.9, product="microbi"),
            _ev("login errors", 0.9, product="other"),
        ]
    )
    assert {i.product for i in incidents} == {"microbi", "other"}


def test_identical_product_claim_share_fingerprint() -> None:
    a = detect_incidents([_ev("Checkout 500s  ", 0.9)])[0]
    b = detect_incidents([_ev("checkout 500S", 0.9)])[0]
    assert a.fingerprint == b.fingerprint


def test_different_claim_distinct_fingerprint() -> None:
    a = detect_incidents([_ev("checkout 500s", 0.9)])[0]
    b = detect_incidents([_ev("login timeouts", 0.9)])[0]
    assert a.fingerprint != b.fingerprint


def test_evidence_without_product_is_skipped() -> None:
    incidents = detect_incidents([_ev("orphan error", 0.99, product=None)])
    assert incidents == []


def test_incident_carries_citation_and_source_kind() -> None:
    (incident,) = detect_incidents([_ev("checkout 500s", 0.9)])
    assert incident.citations == ["https://sentry.example/issues/1"]
    assert incident.source_kinds == ["SENTRY"]
