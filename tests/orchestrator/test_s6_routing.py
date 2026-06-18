"""Unit tests for S6 routing label assembly (council->squad handoff)."""

from __future__ import annotations

from dsf.config.registry import Product
from dsf.contracts.enums import ProposalKind
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.contracts.models import Proposal
from dsf.orchestrator.stations.s6_routing import _labels_for


def _product() -> Product:
    return Product(
        key="acme",
        github_repo="acme/acme",
        label_taxonomy={
            "type": ["feature", "bug", "chore"],
            "severity": ["sev-low", "sev-medium", "sev-high", "sev-critical"],
        },
    )


def _proposal(kind: ProposalKind, confidence: float) -> Proposal:
    return Proposal(
        run_id="r1",
        kind=kind,
        title="t",
        problem="p",
        proposed_change="c",
        confidence=confidence,
    )


def test_labels_include_handoff_label():
    labels = _labels_for(_proposal(ProposalKind.FIX, 0.9), _product())
    assert HANDOFF_LABEL in labels


def test_handoff_label_is_last_after_descriptive_labels():
    labels = _labels_for(_proposal(ProposalKind.FIX, 0.9), _product())
    assert labels[0] == "bug"
    assert "sev-critical" in labels
    assert labels[-1] == HANDOFF_LABEL


def test_handoff_label_present_even_without_taxonomy():
    bare = Product(key="bare", github_repo="o/bare", label_taxonomy={})
    labels = _labels_for(_proposal(ProposalKind.FEATURE, 0.4), bare)
    assert labels == [HANDOFF_LABEL]
