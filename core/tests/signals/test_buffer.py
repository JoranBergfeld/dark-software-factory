"""InMemorySignalBuffer tests."""

from __future__ import annotations

from dsf.signals import InMemorySignalBuffer


async def test_enqueue_then_drain_returns_payloads_in_order():
    buf = InMemorySignalBuffer()
    await buf.enqueue({"text": "a"})
    await buf.enqueue({"text": "b"})

    drained = await buf.drain()
    assert drained == [{"text": "a"}, {"text": "b"}]


async def test_drain_clears_the_buffer():
    buf = InMemorySignalBuffer()
    await buf.enqueue({"text": "a"})

    assert await buf.drain() == [{"text": "a"}]
    # Second drain is empty: the first drain took ownership of the batch.
    assert await buf.drain() == []


async def test_drain_empty_returns_empty_list():
    buf = InMemorySignalBuffer()
    assert await buf.drain() == []


async def test_enqueue_copies_the_payload():
    buf = InMemorySignalBuffer()
    payload = {"text": "a"}
    await buf.enqueue(payload)
    payload["text"] = "mutated"

    # The buffer holds a snapshot, not a live reference to the caller's dict.
    assert await buf.drain() == [{"text": "a"}]
