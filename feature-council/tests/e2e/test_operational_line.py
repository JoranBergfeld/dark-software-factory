"""End-to-end: an operational signal (INCIDENTS) flows through the whole conveyor
to a routed, grounded, squad:ready issue — offline, no real GitHub call."""

from __future__ import annotations

from dsf.contracts.enums import RunStatus, SourceKind
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.conveyor import run_line
from dsf.runtime.control import signal_to_run
from dsf_testing import build_test_services


async def test_incident_signal_flows_to_grounded_squad_issue() -> None:
    services = build_test_services()
    run = signal_to_run(
        {
            "id": "evt_incident_recurrence",
            "source": "incidents",
            "product_hints": ["microbi"],
            "source_kinds": ["INCIDENTS"],
            "title": "Recurring checkout 5xx",
            "text": "Checkout 5xx incident recurred several times this sprint.",
        }
    )
    # Dry-run is a user-invoked preview; set it explicitly (the system never
    # defaults to dry-run).
    run.dry_run = True

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


def _incident_signal() -> dict:
    return {
        "id": "evt_incident_recurrence",
        "source": "incidents",
        "product_hints": ["microbi"],
        "source_kinds": ["INCIDENTS"],
        "title": "Recurring checkout 5xx",
        "text": "Checkout 5xx incident recurred several times this sprint.",
    }


async def test_incident_line_files_real_issue_then_dedups_on_recurrence() -> None:
    """The system executes by default: operational INCIDENTS evidence files a real
    issue via the GitHub port; an identical second run files nothing (deduped at
    S5/S7)."""
    services = build_test_services()  # github = RecordingGitHubClient
    bb = Blackboard(services.memory)

    first = await run_line(signal_to_run(_incident_signal()), services)
    assert first.status is RunStatus.FILED
    issues = await bb.load_issues(first.id)
    assert issues, "expected at least one routed issue"

    # The real filing path was taken: create_issue once per routed issue, each
    # carrying the squad:ready handoff label and a returned URL.
    assert len(services.github.calls) == len(issues)
    for issue in issues:
        assert issue.filed_url is not None
        assert issue.filed_url.startswith("local://issue/")
        assert HANDOFF_LABEL in issue.labels
    filed_after_first = len(services.github.calls)

    # The identical recurrence must NOT re-file: the duplication critic vetoes the
    # repeat proposal at S5, and S7's title index is a second safety net.
    await run_line(signal_to_run(_incident_signal()), services)
    assert len(services.github.calls) == filed_after_first, (
        "a duplicate incident run must not file a second real issue"
    )
