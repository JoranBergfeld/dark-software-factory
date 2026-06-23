from __future__ import annotations

from dsf.charter.context import charter_context as core_charter_context
from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf.council.charter_context import charter_context, load_charter, set_charter_memo
from dsf_testing import build_test_services, make_run
from dsf_testing.charter import InMemoryCharterStore


def _charter() -> Charter:
    return Charter(
        product="alpha", vision="Be great", target_users="U", goals=["g"], success_metrics=["m"]
    )


def test_reexports_core_charter_context():
    # Callers import the untrusted-envelope builder from the council module.
    assert charter_context is core_charter_context


async def test_load_charter_reads_store_and_memoizes():
    store = InMemoryCharterStore()
    await store.put_charter(
        StoredCharter(product="alpha", charter=_charter(), status=CharterStatus.OK)
    )
    services = build_test_services(product="alpha", charter=store)
    run = make_run([])

    first = await load_charter(services, run, "alpha")
    assert first is not None and first.vision == "Be great"

    # Mutating the store after the first load must NOT change the memoized result.
    await store.put_charter(
        StoredCharter(product="alpha", charter=None, status=CharterStatus.MISSING)
    )
    second = await load_charter(services, run, "alpha")
    assert second is not None and second.vision == "Be great"  # served from the run memo


async def test_load_charter_none_is_memoized_too():
    services = build_test_services(product="alpha")  # empty store
    run = make_run([])
    assert await load_charter(services, run, "alpha") is None
    # Seed the store now; the memoized "uncharted" result must persist for this run.
    await services.charter.put_charter(
        StoredCharter(product="alpha", charter=_charter(), status=CharterStatus.OK)
    )
    assert await load_charter(services, run, "alpha") is None


async def test_load_charter_no_product_is_none():
    services = build_test_services(product="alpha")
    run = make_run([])
    assert await load_charter(services, run, None) is None


async def test_set_charter_memo_seeds_loader():
    services = build_test_services(product="alpha")  # empty store
    run = make_run([])
    await set_charter_memo(services, run, "alpha", _charter())
    # load_charter now returns the seeded charter without touching the store.
    got = await load_charter(services, run, "alpha")
    assert got is not None and got.vision == "Be great"
