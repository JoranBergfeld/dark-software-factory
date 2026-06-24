"""S7 filing — dedup behavior."""

from __future__ import annotations

from dsf.contracts.enums import RunStatus, TriggerKind
from dsf.contracts.models import RoutedIssue, Run
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations import s7_filing
from dsf_testing import build_test_services


async def _file_once(services, title: str, problem: str) -> Run:
    run = Run(trigger=TriggerKind.SIGNAL, dry_run=True)
    issue = RoutedIssue(
        proposal_id="p1",
        product="demo",
        repo="acme/demo",
        title=title,
        body="body",
        problem=problem,
    )
    await Blackboard(services.memory).save_issues(run.id, [issue])
    return await s7_filing.run(run, services)


async def test_s7_indexes_dedup_key_with_title_and_problem():
    services = build_test_services()
    problem = "checkout crashes on submit returning 500 errors"

    await _file_once(services, "Checkout returns 500", problem)

    hits = await services.memory.query_similar(problem, "issue", k=5)
    assert hits, "S7 should index a filed-issue record for dedup"
    indexed = hits[0]["text"]
    # The dedup key carries both the title and the problem (not title alone),
    # so the semantic embedder can match reworded-title duplicates.
    assert "Checkout returns 500" in indexed
    assert problem in indexed


async def test_s7_dedups_reworded_title_with_semantic_embedder():
    from dsf_testing import InMemoryMemoryStore
    from dsf_testing.azure_doubles import RecordingEmbeddingsGateway

    problem = "checkout crashes on submit returning 500 errors for many users"
    key_a = f"Checkout returns 500 {problem}"
    key_b = f"Payment flow broken {problem}"
    # Same problem -> same vector regardless of the (reworded) title.
    embedder = RecordingEmbeddingsGateway({key_a: [1.0, 0.0], key_b: [1.0, 0.0]}, dim=2)

    services = build_test_services()
    services.memory = InMemoryMemoryStore(embedder=embedder)

    await _file_once(services, "Checkout returns 500", problem)
    run2 = await _file_once(services, "Payment flow broken", problem)

    messages = " ".join(a.message for a in run2.audit)
    assert "duplicate" in messages


async def test_s7_records_dedup_and_files_when_coding_agent_assignment_fails():
    """If filing succeeds but Copilot assignment fails, S7 must still record the
    dedup key and complete FILED (never silently drop it and re-file a duplicate)."""
    from dsf.ports import CodingAgentAssignmentError

    class _AssignFailsClient:
        async def create_issue(self, repo, title, body, labels):
            raise CodingAgentAssignmentError(
                "filed but assign failed",
                issue_url="https://github.com/acme/demo/issues/9",
                issue_node_id="ISSUE_NODE_9",
            )

    services = build_test_services()
    services.github = _AssignFailsClient()

    run = Run(trigger=TriggerKind.SIGNAL, dry_run=False)
    problem = "checkout crashes on submit returning 500 errors"
    issue = RoutedIssue(
        proposal_id="p1",
        product="demo",
        repo="acme/demo",
        title="Checkout returns 500",
        body="body",
        problem=problem,
    )
    await Blackboard(services.memory).save_issues(run.id, [issue])

    result = await s7_filing.run(run, services)

    assert result.status == RunStatus.FILED
    # The filing was recorded for dedup despite the assignment failure.
    hits = await services.memory.query_similar(problem, "issue", k=5)
    assert hits, "S7 must dedup-index a filed issue even when assignment failed"
    # filed_url carried over from the error.
    saved = await Blackboard(services.memory).load_issues(run.id)
    assert saved[0].filed_url == "https://github.com/acme/demo/issues/9"
    # The assignment failure is surfaced loudly in the audit log.
    messages = " ".join(a.message for a in result.audit)
    assert "assignment FAILED" in messages
