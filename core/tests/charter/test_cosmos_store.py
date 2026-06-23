from __future__ import annotations

import sys

from dsf.charter.cosmos_store import CosmosCharterStore
from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf_testing.azure_doubles import InMemoryCosmosGateway


async def test_put_get_roundtrip_is_singleton():
    gw = InMemoryCosmosGateway()
    store = CosmosCharterStore(gw)
    c = Charter(
        product="alpha",
        vision="V",
        target_users="U",
        goals=["g"],
        success_metrics=["m"],
        source_sha="abc",
        source_ref="main",
    )
    await store.put_charter(StoredCharter(product="alpha", charter=c, status=CharterStatus.OK))
    got = await store.get_charter("alpha")
    assert got is not None and got.charter is not None
    assert got.charter.source_sha == "abc" and got.status == CharterStatus.OK
    assert gw.containers["charters"][0]["id"] == "alpha"


async def test_get_missing_returns_none():
    store = CosmosCharterStore(InMemoryCosmosGateway())
    assert await store.get_charter("absent") is None


async def test_put_is_upsert_by_product():
    gw = InMemoryCosmosGateway()
    store = CosmosCharterStore(gw)
    await store.put_charter(
        StoredCharter(product="alpha", charter=None, status=CharterStatus.MISSING)
    )
    await store.put_charter(
        StoredCharter(product="alpha", charter=None, status=CharterStatus.INVALID, last_error="bad")
    )
    assert len(gw.containers["charters"]) == 1
    got = await store.get_charter("alpha")
    assert got is not None and got.status == CharterStatus.INVALID


def test_module_import_is_sdk_free():
    assert "azure.cosmos" not in sys.modules
