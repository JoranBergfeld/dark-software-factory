from __future__ import annotations

from datetime import UTC, datetime

from dsf.charter.amendment import AMENDMENT_BRANCH_PREFIX, AmendmentDraft
from dsf.config.registry import Product
from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus, TriggerKind
from dsf.contracts.models import Run
from dsf.triggers import charter_amend
from dsf.triggers.charter_amend import STATION, propose_amendment_on_sweep
from dsf_testing import DeterministicModelClient, build_test_services
from dsf_testing.charter import InMemoryCharterStore
from dsf_testing.config import InMemoryConfigStore
from dsf_testing.github import RecordingRepoClient
from dsf_testing.memory import InMemoryMemoryStore


def _route_alpha(monkeypatch) -> None:
    monkeypatch.setattr(charter_amend, "load_registry", lambda: {})
    monkeypatch.setattr(
        charter_amend,
        "route_product",
        lambda hints, registry: Product(key="alpha", github_repo="org/alpha"),
    )


def _charter() -> Charter:
    return Charter(
        product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"]
    )


def _enabled_config() -> InMemoryConfigStore:
    cfg = InMemoryConfigStore.from_defaults()
    cfg.set_flag("charter.amendment.enabled", True)
    return cfg


async def _services_for_proposal(*, changed: Charter | None):
    store = InMemoryCharterStore(
        {
            "alpha": StoredCharter(
                product="alpha",
                charter=_charter(),
                status=CharterStatus.OK,
                last_synced_at=datetime(2026, 6, 23, tzinfo=UTC),
            )
        }
    )
    memory = InMemoryMemoryStore()
    for i in range(4):
        await memory.put_lesson(
            {"product": "alpha", "kind": "pr_outcome", "outcome": "rejected", "text": f"l{i}"}
        )
    model = DeterministicModelClient()
    draft = (
        AmendmentDraft(changed=True, rationale="evidence demands it", charter=changed)
        if changed is not None
        else AmendmentDraft(changed=False, rationale="still fits")
    )
    model.register("[charter-amendment]", lambda system, prompt: draft)
    return build_test_services(
        product="alpha",
        charter=store,
        memory=memory,
        model=model,
        config=_enabled_config(),
        repo=RecordingRepoClient(),
    )


def _sweep_run() -> Run:
    return Run(trigger=TriggerKind.SCHEDULED)


async def test_proposes_amendment_and_audits_pr(monkeypatch):
    _route_alpha(monkeypatch)
    amended = Charter(
        product="alpha", vision="V2", target_users="U", goals=["g", "g2"], success_metrics=["m"]
    )
    services = await _services_for_proposal(changed=amended)
    run = _sweep_run()

    await propose_amendment_on_sweep(services, run)

    assert len(services.repo.prs) == 1
    assert services.repo.prs[0]["branch"].startswith(AMENDMENT_BRANCH_PREFIX)
    assert any(
        r.station == STATION and "proposed PR" in r.message for r in run.audit
    )


async def test_no_change_is_audited_without_a_pr(monkeypatch):
    _route_alpha(monkeypatch)
    services = await _services_for_proposal(changed=None)
    run = _sweep_run()

    await propose_amendment_on_sweep(services, run)

    assert services.repo.prs == []
    assert any(r.station == STATION and "no_change" in r.message for r in run.audit)


async def test_disabled_is_audited(monkeypatch):
    _route_alpha(monkeypatch)
    services = await _services_for_proposal(changed=_charter())
    services.config.set_flag("charter.amendment.enabled", False)
    run = _sweep_run()

    await propose_amendment_on_sweep(services, run)

    assert any(r.station == STATION and "disabled" in r.message for r in run.audit)


async def test_without_app_audits_and_skips(monkeypatch):
    _route_alpha(monkeypatch)
    services = build_test_services(
        product="alpha", charter=InMemoryCharterStore(), config=_enabled_config(), repo=None
    )
    run = _sweep_run()

    await propose_amendment_on_sweep(services, run)

    assert any(r.station == STATION and "no GitHub App" in r.message for r in run.audit)


async def test_unregistered_product_skips(monkeypatch):
    monkeypatch.setattr(charter_amend, "load_registry", lambda: {})
    monkeypatch.setattr(charter_amend, "route_product", lambda hints, registry: None)
    services = build_test_services(
        product="alpha",
        charter=InMemoryCharterStore(),
        config=_enabled_config(),
        repo=RecordingRepoClient(),
    )
    run = _sweep_run()

    await propose_amendment_on_sweep(services, run)

    assert any(r.station == STATION and "not in registry" in r.message for r in run.audit)


async def test_no_product_is_noop():
    services = build_test_services(product=None, charter=InMemoryCharterStore())
    run = _sweep_run()
    await propose_amendment_on_sweep(services, run)
    assert run.audit == []


async def test_never_raises(monkeypatch):
    _route_alpha(monkeypatch)

    class Boom:
        async def latest_pr_with_head_prefix(self, *args, **kwargs):
            raise RuntimeError("network down")

    store = InMemoryCharterStore(
        {
            "alpha": StoredCharter(
                product="alpha", charter=_charter(), status=CharterStatus.OK
            )
        }
    )
    memory = InMemoryMemoryStore()
    for i in range(4):
        await memory.put_lesson({"product": "alpha", "kind": "k", "outcome": "o", "text": f"l{i}"})
    services = build_test_services(
        product="alpha", charter=store, memory=memory, config=_enabled_config(), repo=Boom()
    )
    run = _sweep_run()

    await propose_amendment_on_sweep(services, run)  # must not raise

    assert any(r.station == STATION and "error" in r.message.lower() for r in run.audit)


async def test_run_sweep_amends_after_sync_before_line(monkeypatch):
    from dsf.orchestrator import conveyor
    from dsf.triggers import charter_sync, scheduler

    order: list[str] = []

    async def fake_sync(services, run):
        order.append("sync")

    async def fake_amend(services, run):
        order.append("amend")

    async def fake_line(run, services):
        order.append("line")
        return run

    monkeypatch.setattr(charter_sync, "sync_charter_on_sweep", fake_sync)
    monkeypatch.setattr(charter_amend, "propose_amendment_on_sweep", fake_amend)
    monkeypatch.setattr(conveyor, "run_line", fake_line)
    services = build_test_services(product="alpha")

    await scheduler.run_sweep(services)
    assert order == ["sync", "amend", "line"]
