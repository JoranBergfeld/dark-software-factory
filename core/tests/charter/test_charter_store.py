from __future__ import annotations

from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf_testing import build_test_services
from dsf_testing.charter import InMemoryCharterStore


async def test_put_then_get_is_singleton_per_product():
    store = InMemoryCharterStore()
    assert await store.get_charter("alpha") is None
    c = Charter(product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"])
    await store.put_charter(StoredCharter(product="alpha", charter=c, status=CharterStatus.OK))
    got = await store.get_charter("alpha")
    assert got is not None and got.charter is not None and got.charter.vision == "V"
    await store.put_charter(
        StoredCharter(product="alpha", charter=None, status=CharterStatus.MISSING)
    )
    again = await store.get_charter("alpha")
    assert again is not None and again.status == CharterStatus.MISSING


def test_build_test_services_wires_charter_store():
    services = build_test_services(product="alpha")
    assert isinstance(services.charter, InMemoryCharterStore)
    assert services.repo is None
