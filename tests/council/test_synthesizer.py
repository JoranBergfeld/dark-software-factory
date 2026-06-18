"""Synthesizer tests (plan Task 3.1)."""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import ProposalKind
from dsf.council.synthesizer import synthesize
from tests.council.conftest import make_evidence, make_run


async def test_two_evidence_same_product_yields_proposal_with_default_fake_model():
    """A run with 2 evidence sharing a product hint -> >=1 grounded proposal.

    Uses build_services("local") with the DEFAULT DeterministicModelClient (no handler
    registered), proving the synthesizer never depends on structured model
    output.
    """
    services = build_services("local")
    ev1 = make_evidence("checkout error spike", product="alpha")
    ev2 = make_evidence("payment regression observed", product="alpha")
    run = make_run([ev1, ev2])

    proposals = await synthesize(run, services)

    assert len(proposals) >= 1
    prop = proposals[0]
    assert prop.product == "alpha"
    run_ids = {ev1.id, ev2.id}
    assert set(prop.evidence_ids).issubset(run_ids)
    assert set(prop.evidence_ids) == run_ids
    # Error/regression claims -> FIX kind.
    assert prop.kind == ProposalKind.FIX
    # Confidence is the mean of evidence confidence.
    assert prop.confidence == 0.8
    # Title/problem are non-empty prose.
    assert prop.title.strip()
    assert prop.problem.strip()


async def test_distinct_product_hints_cluster_separately():
    services = build_services("local")
    run = make_run(
        [
            make_evidence("new dashboard request", product="alpha"),
            make_evidence("export feature ask", product="beta"),
        ]
    )

    proposals = await synthesize(run, services)

    products = {p.product for p in proposals}
    assert products == {"alpha", "beta"}
    # No error keywords -> FEATURE.
    assert all(p.kind == ProposalKind.FEATURE for p in proposals)


async def test_empty_run_yields_no_proposals():
    services = build_services("local")
    run = make_run([])
    assert await synthesize(run, services) == []
