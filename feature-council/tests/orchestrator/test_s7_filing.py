"""S7 filing — dedup behavior."""

from __future__ import annotations

from dsf.contracts.enums import TriggerKind
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
