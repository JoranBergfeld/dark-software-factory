"""Tests for memory tiers, dedup, and consolidation (plan Task 1.1)."""

from __future__ import annotations

from dsf.contracts.enums import RunStatus, TriggerKind, Verdict
from dsf.contracts.models import CouncilVerdict, CriticScore, Run
from dsf.fakes import FakeMemoryStore
from dsf.memory import Memory, consolidate_run, is_duplicate


async def test_working_put_get():
    mem = Memory(FakeMemoryStore())
    await mem.remember_working("run:42", {"status": "OPEN"}, ttl=60)
    assert await mem.recall_working("run:42") == {"status": "OPEN"}
    assert await mem.recall_working("missing") is None


async def test_record_and_similar_roundtrip():
    store = FakeMemoryStore()
    mem = Memory(store)
    await store.put_record(
        {"kind": "proposal", "text": "add export to csv button on dashboard"}
    )
    hits = await mem.similar("add export to csv button on dashboard", "proposal")
    assert hits
    assert hits[0]["similarity"] >= 0.8


async def test_is_duplicate_true_for_near_identical():
    store = FakeMemoryStore()
    text = "improve latency of the microbi ingestion pipeline under load"
    await store.put_record({"kind": "proposal", "text": text})
    # Highly overlapping query -> token overlap above threshold.
    assert await is_duplicate(text, store, "proposal", threshold=0.8) is True


async def test_is_duplicate_false_when_no_records():
    store = FakeMemoryStore()
    assert await is_duplicate("brand new idea", store, "proposal") is False


async def test_is_duplicate_false_for_dissimilar():
    store = FakeMemoryStore()
    await store.put_record({"kind": "proposal", "text": "alpha beta gamma delta"})
    assert await is_duplicate("zeta eta theta iota", store, "proposal") is False


async def test_consolidate_run_writes_retrievable_lesson():
    store = FakeMemoryStore()
    run = Run(
        trigger=TriggerKind.SIGNAL,
        status=RunStatus.FILED,
        scope_product_hints=["microbi"],
    )
    verdict = CouncilVerdict.from_scores(
        proposal_id="prop-1",
        scores=[CriticScore(critic="value", score=0.9)],
        threshold=0.6,
    )
    assert verdict.verdict == Verdict.ACCEPT

    lesson = await consolidate_run(run, verdict, store)

    assert lesson["product"] == "microbi"
    assert lesson["outcome"] == "accepted"

    lessons = await store.get_lessons("microbi")
    assert len(lessons) == 1
    assert lessons[0]["product"] == "microbi"
    assert lessons[0]["signal"] == TriggerKind.SIGNAL.value

    # A long-term record was also written and is retrievable.
    records = await store.query_similar("microbi", "run_outcome")
    assert records
