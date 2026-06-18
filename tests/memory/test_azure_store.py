"""CosmosMemoryStore — Azure Cosmos DB adapter, exercised offline."""

import sys

from dsf.memory.azure_store import CosmosMemoryStore
from tests.support.azure_doubles import InMemoryCosmosGateway


def _store():
    return CosmosMemoryStore(InMemoryCosmosGateway())


async def test_put_and_get_working():
    store = _store()
    await store.put_working("k", {"v": 1})
    assert await store.get_working("k") == {"v": 1}


async def test_get_working_missing_returns_none():
    assert await _store().get_working("absent") is None


async def test_put_working_sets_ttl_field():
    gw = InMemoryCosmosGateway()
    store = CosmosMemoryStore(gw)
    await store.put_working("k", 1, ttl=30)
    assert gw.containers["working"][0]["ttl"] == 30


async def test_query_similar_ranks_by_overlap_and_limits_k():
    store = _store()
    await store.put_record({"kind": "bug", "text": "login fails on safari"})
    await store.put_record({"kind": "bug", "text": "unrelated payment timeout"})
    await store.put_record({"kind": "other", "text": "login fails on safari"})
    out = await store.query_similar("login fails safari", "bug", k=1)
    assert len(out) == 1
    assert "login" in out[0]["text"]
    assert out[0]["similarity"] > 0


async def test_get_lessons_filters_product_newest_first_limit_k():
    store = _store()
    await store.put_lesson({"product": "acme", "text": "first"})
    await store.put_lesson({"product": "acme", "text": "second"})
    await store.put_lesson({"product": "other", "text": "nope"})
    out = await store.get_lessons("acme", k=1)
    assert [le["text"] for le in out] == ["second"]


def test_module_import_is_sdk_free():
    assert "azure.cosmos" not in sys.modules
