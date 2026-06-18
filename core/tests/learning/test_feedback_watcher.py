"""Tests for the PR feedback watcher + lessons (plan Task 6.1)."""

from __future__ import annotations

from dsf.container import build_services
from dsf.learning import handle_pr_event, parse_pr_event


def _merged_event() -> dict:
    return {
        "action": "closed",
        "pull_request": {
            "id": 101,
            "number": 7,
            "title": "Add CSV export to dashboard",
            "state": "closed",
            "merged": True,
            "body": "Spec PR.\nproduct: analytics\nproposal: prop-7\n",
            "labels": [{"name": "spec"}, {"name": "product:analytics"}],
        },
        "spec_diff": "- export only PDF\n+ export PDF and CSV",
    }


def _closed_unmerged_event() -> dict:
    return {
        "action": "closed",
        "pull_request": {
            "id": 202,
            "number": 8,
            "title": "Add blockchain to login",
            "state": "closed",
            "merged": False,
            "body": "product: auth\n",
            "labels": [{"name": "spec"}],
        },
        "rationale": "out of scope and adds attack surface",
        "spec_diff": "+ require wallet signature on login",
    }


def test_parse_merged_pr_is_approved():
    outcome = parse_pr_event(_merged_event())
    assert outcome.verdict == "approved"
    assert outcome.product == "analytics"
    assert outcome.proposal_title == "Add CSV export to dashboard"


def test_parse_closed_unmerged_is_rejected():
    outcome = parse_pr_event(_closed_unmerged_event())
    assert outcome.verdict == "rejected"
    assert outcome.product == "auth"


def test_parse_changes_requested_review():
    event = {
        "action": "submitted",
        "pull_request": {
            "id": 303,
            "title": "Tweak retry policy",
            "state": "open",
            "body": "product: payments\n",
        },
        "review": {"state": "changes_requested", "body": "narrow the retry window"},
    }
    outcome = parse_pr_event(event)
    assert outcome.verdict == "changes_requested"
    assert outcome.product == "payments"
    assert "retry window" in outcome.rationale


def test_parse_product_from_label_fallback():
    event = {
        "action": "closed",
        "pull_request": {
            "id": 1,
            "title": "x",
            "state": "closed",
            "merged": True,
            "body": "no markers here",
            "labels": ["product:billing"],
        },
    }
    outcome = parse_pr_event(event)
    assert outcome.product == "billing"


def test_parse_tolerates_empty_event():
    outcome = parse_pr_event({})
    assert outcome.product == "unknown"
    assert outcome.verdict == "changes_requested"
    assert outcome.id == "unknown"


async def test_handle_approved_stores_lesson_and_record():
    services = build_services("local")
    outcome = await handle_pr_event(_merged_event(), services)

    assert outcome.verdict == "approved"

    lessons = await services.memory.get_lessons("analytics")
    assert lessons, "approved PR should produce a retrievable lesson"
    lesson = lessons[0]
    assert lesson["product"] == "analytics"
    assert lesson["kind"] == "pr_outcome"
    assert lesson["outcome"] == "approved"

    records = await services.memory.query_similar("pr_outcome", "pr_outcome", k=10)
    assert any(r.get("pr_id") == "101" for r in records), "outcome record should be stored"


async def test_handle_rejected_lesson_carries_negative_signal():
    services = build_services("local")
    outcome = await handle_pr_event(_closed_unmerged_event(), services)

    assert outcome.verdict == "rejected"

    lessons = await services.memory.get_lessons("auth")
    assert lessons
    lesson = lessons[0]
    assert lesson["outcome"] == "rejected"
    assert lesson["signal"] == "pr:rejected"
    # negative signal: the human rationale must be captured in the lesson
    assert "out of scope" in lesson["rationale"]
    # spec diff summary captured so future runs can avoid the rejected change
    assert "wallet signature" in lesson["rationale"]
