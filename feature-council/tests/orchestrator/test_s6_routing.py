"""Unit tests for S6 routing label assembly (council->squad handoff)."""

from __future__ import annotations

from dsf.config.registry import Product
from dsf.contracts.enums import ProposalKind, TriggerKind
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.contracts.models import Proposal, Run
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations import s6_routing
from dsf.orchestrator.stations.s6_routing import _labels_for
from dsf_testing import build_test_services, config_with_product_record, make_proposal


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


async def test_run_routes_all_proposals_to_factory_product():
    services = build_test_services(
        product="acme",
        config=config_with_product_record(
            "acme",
            github_repo="acme/acme",
            label_taxonomy={"type": ["bug"], "severity": ["sev-low"]},
        ),
    )
    run = Run(trigger=TriggerKind.SIGNAL, scope_product_hints=["acme"])
    bb = Blackboard(services.memory)
    await bb.save_proposals(run.id, [make_proposal(run, product="acme")])

    result = await s6_routing.run(run, services)
    issues = await bb.load_issues(run.id)

    assert result.status.name == "ROUTING"
    assert len(issues) == 1
    assert issues[0].product == "acme"
    assert issues[0].repo == "acme/acme"
