"""End-to-end dry-run: a signal flows through the whole conveyor to a filed
(but not network-filed) issue, with grounding enforced and no real GitHub call."""

from __future__ import annotations

import json
from pathlib import Path

import pytest

from dsf.contracts.enums import RunStatus
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.conveyor import run_line
from dsf.runtime.control import main, signal_to_run
from dsf_testing import build_test_services

REPO_ROOT = Path(__file__).resolve().parents[3]
SAMPLE_SIGNAL = REPO_ROOT / "tests" / "fixtures" / "sample_signal.json"

STATION_NAMES = {
    "S1:triage",
    "S2:investigation",
    "S3:synthesis",
    "S4:grounding",
    "S5:council",
    "S6:routing",
    "S7:filing",
}


@pytest.fixture
def payload() -> dict:
    return json.loads(SAMPLE_SIGNAL.read_text(encoding="utf-8"))


async def test_dry_run_line_files_grounded_issue_without_network(payload: dict) -> None:
    services = build_test_services()
    run = signal_to_run(payload)
    run.dry_run = True

    final = await run_line(run, services)

    # 1. The line reaches FILED.
    assert final.status is RunStatus.FILED

    # 2. At least one issue was routed, and grounding was enforced: every routed
    #    issue's proposal cites only evidence that exists on the run.
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

    # 3. Dry-run means NO real issue was filed.
    assert services.github.calls == []
    assert all(issue.filed_url is None for issue in issues)

    # 4. Every station left an audit trail.
    stations_seen = {rec.station for rec in final.audit}
    assert STATION_NAMES <= stations_seen


async def test_line_files_real_issue_when_dry_run_off(payload: dict) -> None:
    """With both dry-run switches off, the wired line actually files via the
    GitHub port — regression for #13 (no code path could file a real issue)."""
    services = build_test_services()  # github = RecordingGitHubClient
    services.config.set_flag("dry_run", False)  # off the global kill switch

    run = signal_to_run(payload)
    run.dry_run = False  # caller deliberately chose to file

    final = await run_line(run, services)
    assert final.status is RunStatus.FILED

    bb = Blackboard(services.memory)
    issues = await bb.load_issues(final.id)
    assert issues, "expected at least one routed issue"

    # The real filing path was taken: create_issue was called once per routed
    # issue, each issue carries the returned URL, and the recorded call matches.
    assert len(services.github.calls) == len(issues)
    for issue in issues:
        assert issue.filed_url is not None
        assert issue.filed_url.startswith("local://issue/")
    filed = {(c["repo"], c["title"]) for c in services.github.calls}
    assert filed == {(issue.repo, issue.title) for issue in issues}


def test_cli_run_dry_run_exits_zero(capsys) -> None:
    code = main(["run", "--dry-run", "--signal", str(SAMPLE_SIGNAL)])
    out = capsys.readouterr().out
    assert code == 0
    assert "status=filed" in out.lower()


def test_cli_serve_orchestrator_exits_zero(capsys) -> None:
    # The orchestrator tick runs the source sweep (DSF is pull-only).
    code = main(["serve-orchestrator"])
    out = capsys.readouterr().out
    assert code == 0
    # A run summary was printed for the scheduled sweep.
    assert "[dsf] run" in out
