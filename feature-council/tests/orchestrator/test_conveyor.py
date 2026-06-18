"""Tests for the full conveyor line."""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import RunStatus, TriggerKind
from dsf.contracts.models import Run
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.conveyor import STATIONS, run_line
from dsf.orchestrator.stations.s1_triage import SIGNAL_KIND


async def test_full_line_reaches_filed_dry_run() -> None:
    services = build_services("local")
    run = Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload={"product_hints": ["microbi"], "text": "checkout error spike"},
    )

    result = await run_line(run, services)

    # Reaches FILED.
    assert result.status == RunStatus.FILED

    # >= 1 RoutedIssue produced.
    issues = await Blackboard(services.memory).load_issues(result.id)
    assert len(issues) >= 1

    # Dry-run default: GitHub create_issue was NOT called; no filed_url set.
    assert services.github.calls == []
    assert all(issue.filed_url is None for issue in issues)

    # Grounding enforced: every routed issue's proposal had non-empty evidence_ids.
    proposals = await Blackboard(services.memory).load_proposals(result.id)
    by_id = {p.id: p for p in proposals}
    for issue in issues:
        proposal = by_id.get(issue.proposal_id)
        assert proposal is not None
        assert proposal.evidence_ids

    # >= 1 audit record per station.
    stations_audited = {a.station for a in result.audit}
    for name, _fn in STATIONS:
        assert name in stations_audited, f"no audit record for {name}"


async def test_duplicate_signal_at_s1_kills_run() -> None:
    services = build_services("local")
    text = "checkout error spike"

    # Pre-seed an in-flight signal record so S1 debounce fires.
    await services.memory.put_record({"kind": SIGNAL_KIND, "text": text, "run_id": "prior"})

    run = Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload={"product_hints": ["microbi"], "text": text},
    )
    result = await run_line(run, services)

    assert result.status == RunStatus.KILLED
    # GitHub never called.
    assert services.github.calls == []
    # Audited as a duplicate.
    messages = " ".join(a.message for a in result.audit)
    assert "duplicate" in messages.lower()


async def test_error_in_station_yields_error_status() -> None:
    services = build_services("local")
    run = Run(trigger=TriggerKind.SIGNAL, signal_payload={"product_hints": ["microbi"]})

    # Force S2 to blow up by removing the memory store's gather path indirectly:
    # patch one station to raise via monkeying the conveyor STATIONS is overkill;
    # instead corrupt config so build_agents path is fine but model raises. The
    # simplest deterministic failure: a station that raises. We emulate by
    # passing a services whose memory.put_working raises on save mid-line.
    original = services.memory.put_record

    calls = {"n": 0}

    async def boom(record: dict) -> None:
        calls["n"] += 1
        if calls["n"] == 1:
            raise RuntimeError("injected failure")
        await original(record)

    services.memory.put_record = boom  # type: ignore[method-assign]

    result = await run_line(run, services)
    assert result.status == RunStatus.ERROR
    messages = " ".join(a.message for a in result.audit)
    assert "station error" in messages.lower()


async def test_killed_run_does_not_resume_past_s1() -> None:
    """Regression: a KILLED run re-driven through run_line must stay KILLED.

    Before the fix, on resume the loop skipped S1 (already checkpointed) and
    bypassed the KILLED early-return, letting S2..S7 execute and file an issue.
    """
    services = build_services("local")
    text = "checkout error spike"

    # Pre-seed duplicate signal so S1 debounce fires and kills the run.
    await services.memory.put_record({"kind": SIGNAL_KIND, "text": text, "run_id": "prior"})

    run = Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload={"product_hints": ["microbi"], "text": text},
    )
    killed = await run_line(run, services)
    assert killed.status == RunStatus.KILLED

    # Simulate a retry/resume: re-drive the same run object through run_line.
    resumed = await run_line(killed, services)

    assert resumed.status == RunStatus.KILLED, (
        f"expected KILLED after resume, got {resumed.status}"
    )
    # No issues should have been routed or filed.
    issues = await Blackboard(services.memory).load_issues(killed.id)
    assert len(issues) == 0, f"expected no issues after resume, got {len(issues)}"
    # GitHub create_issue was never called.
    assert services.github.calls == []
