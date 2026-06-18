"""Tests for the in-memory fakes (plan Task 0.3)."""

from __future__ import annotations

import time

from dsf.fakes import (
    FakeConfigStore,
    FakeMemoryStore,
    FakeModelClient,
    FakeSourceBackend,
)
from dsf.ports import (
    ConfigStore,
    MemoryStore,
    ModelClient,
    SourceBackend,
)


def test_fakes_satisfy_protocols():
    assert isinstance(FakeModelClient(), ModelClient)
    assert isinstance(FakeMemoryStore(), MemoryStore)
    assert isinstance(FakeConfigStore.from_defaults(), ConfigStore)
    assert isinstance(FakeSourceBackend(), SourceBackend)


async def test_model_client_handler_keyed_on_tag():
    client = FakeModelClient()
    client.register("##SYNTH##", lambda s, p: "synthesized")
    out = await client.complete("sys", "please ##SYNTH## now")
    assert out == "synthesized"
    # Deterministic: same call, same result.
    again = await client.complete("sys", "please ##SYNTH## now")
    assert again == "synthesized"
    # Unmatched prompt falls back to deterministic echo.
    miss = await client.complete("sys", "nothing here")
    assert miss.startswith("[fake-model]")


async def test_memory_store_working_and_records():
    mem = FakeMemoryStore()
    await mem.put_working("k", {"x": 1})
    assert await mem.get_working("k") == {"x": 1}
    assert await mem.get_working("missing") is None

    await mem.put_record({"kind": "proposal", "text": "add retry logic to client"})
    await mem.put_record({"kind": "proposal", "text": "fix unrelated typo"})
    await mem.put_record({"kind": "other", "text": "add retry logic to client"})

    hits = await mem.query_similar("add retry logic to the http client", "proposal", k=5)
    assert hits  # only 'proposal' kind, ranked by overlap
    assert all(h["kind"] == "proposal" for h in hits)
    assert hits[0]["similarity"] >= hits[-1]["similarity"]
    assert "retry" in hits[0]["text"]


async def test_memory_store_lessons():
    mem = FakeMemoryStore()
    await mem.put_lesson({"product": "alpha", "text": "lesson one"})
    await mem.put_lesson({"product": "beta", "text": "other"})
    lessons = await mem.get_lessons("alpha")
    assert len(lessons) == 1
    assert lessons[0]["text"] == "lesson one"


def test_config_store_seeded_defaults():
    cfg = FakeConfigStore.from_defaults()
    assert cfg.is_enabled("dry_run") is True
    assert cfg.is_enabled("critic.grounding") is True
    assert cfg.is_enabled("agent.SENTRY") is True
    assert cfg.is_enabled("trigger.SIGNAL.paused") is False
    assert cfg.get_value("default_threshold") == 0.6
    assert cfg.get_value("critics.value.weight") == 1.0


async def test_memory_store_record_ttl_expires():
    """Records with a TTL are invisible to query_similar after they expire."""
    mem = FakeMemoryStore()
    # Insert a record with a very short TTL.
    await mem.put_record({"kind": "sig", "text": "checkout error"}, ttl=0.01)
    # Immediately visible.
    hits = await mem.query_similar("checkout error", "sig", k=1)
    assert hits and hits[0]["similarity"] >= 0.8
    # After expiry it must no longer appear.
    time.sleep(0.02)
    hits_after = await mem.query_similar("checkout error", "sig", k=1)
    assert not hits_after


async def test_memory_store_working_ttl_expires():
    """Working-tier entries expire after their ttl."""
    mem = FakeMemoryStore()
    await mem.put_working("k", "value", ttl=0.01)
    assert await mem.get_working("k") == "value"
    time.sleep(0.02)
    assert await mem.get_working("k") is None


async def test_memory_store_max_records_eviction():
    """Records beyond max_records are evicted (oldest first) to bound growth."""
    mem = FakeMemoryStore(max_records=5)
    for i in range(7):
        await mem.put_record({"kind": "evt", "text": f"event {i}"})
    # Only the 5 most recent should survive.
    assert len(mem._records) == 5
    texts = {r["text"] for r in mem._records}
    assert "event 0" not in texts
    assert "event 1" not in texts
    assert "event 6" in texts


async def test_memory_store_query_similar_strips_internal_keys():
    """_inserted_at and _ttl must not appear in query_similar results."""
    mem = FakeMemoryStore()
    await mem.put_record({"kind": "k", "text": "hello world"}, ttl=9999)
    hits = await mem.query_similar("hello world", "k", k=1)
    assert hits
    assert "_inserted_at" not in hits[0]
    assert "_ttl" not in hits[0]
    assert "similarity" in hits[0]
