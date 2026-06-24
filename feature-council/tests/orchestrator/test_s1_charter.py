from __future__ import annotations

from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus, TriggerKind
from dsf.contracts.models import Run
from dsf.council.charter_context import load_charter
from dsf.orchestrator.stations import s1_triage
from dsf_testing import build_test_services
from dsf_testing.charter import InMemoryCharterStore


def _charter() -> Charter:
    return Charter(
        product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"]
    )


def _run(payload: dict) -> Run:
    return Run(
        trigger=TriggerKind.SIGNAL, scope_product_hints=["alpha"], signal_payload=payload
    )


async def test_s1_audits_charter_present_and_warms_memo():
    store = InMemoryCharterStore()
    await store.put_charter(
        StoredCharter(product="alpha", charter=_charter(), status=CharterStatus.OK)
    )
    services = build_test_services(product="alpha", charter=store)
    run = _run({"id": "sig-present"})

    out = await s1_triage.run(run, services)

    assert any("charter: loaded for 'alpha'" in r.message for r in out.audit)
    assert any("status=OK" in r.message for r in out.audit)


async def test_s1_audits_uncharted_and_memoizes_none():
    services = build_test_services(product="alpha")  # empty charter store
    run = _run({"id": "sig-uncharted"})

    out = await s1_triage.run(run, services)

    assert any("uncharted" in r.message for r in out.audit)
    # Memo warmed to None: seeding the store now must not change the run's view.
    await services.charter.put_charter(
        StoredCharter(product="alpha", charter=_charter(), status=CharterStatus.OK)
    )
    assert await load_charter(services, out, "alpha") is None


async def test_s1_no_product_skips_charter_audit():
    services = build_test_services(product=None)
    run = Run(trigger=TriggerKind.SIGNAL, signal_payload={"id": "sig-noprod"})
    out = await s1_triage.run(run, services)
    assert not any("charter" in r.message for r in out.audit)
