"""End-to-end: an operational signal (INCIDENTS) flows through the whole conveyor
to a routed, grounded, squad:ready issue — offline, no real GitHub call."""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import RunStatus, SourceKind
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.conveyor import run_line
from dsf.triggers.ingestion import signal_to_run


async def test_incident_signal_flows_to_grounded_squad_issue() -> None:
    services = build_services("local")
    run = signal_to_run(
        {
            "id": "evt_incident_recurrence",
            "source": "incidents",
            "product_hints": ["microbi"],
            "source_kinds": ["INCIDENTS"],
            "title": "Recurring checkout 5xx",
            "text": "Checkout 5xx incident recurred several times this sprint.",
            "dry_run": True,
        }
    )

    final = await run_line(run, services)

    assert final.status is RunStatus.FILED
    # Operational evidence rode the conveyor.
    assert any(
        e.provenance.source_kind is SourceKind.INCIDENTS for e in final.evidence
    ), "expected INCIDENTS evidence on the run"

    bb = Blackboard(services.memory)
    issues = await bb.load_issues(final.id)
    proposals = {p.id: p for p in await bb.load_proposals(final.id)}
    assert issues, "expected at least one routed issue"
    evidence_ids = {e.id for e in final.evidence}
    for issue in issues:
        prop = proposals.get(issue.proposal_id)
        assert prop is not None
        assert prop.evidence_ids, "filed proposal must be grounded"
        assert set(prop.evidence_ids) <= evidence_ids
        assert HANDOFF_LABEL in issue.labels

    # Dry-run: nothing filed for real.
    assert services.github.calls == []
    assert all(issue.filed_url is None for issue in issues)
