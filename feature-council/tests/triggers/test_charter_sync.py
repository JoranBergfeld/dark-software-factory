from __future__ import annotations

from dsf.charter.markdown import render_charter
from dsf.charter.sync import CHARTER_PATH
from dsf.config.registry import Product
from dsf.contracts.charter import Charter
from dsf.contracts.enums import CharterStatus, TriggerKind
from dsf.contracts.models import Run
from dsf.triggers import charter_sync
from dsf.triggers.charter_sync import STATION, sync_charter_on_sweep
from dsf_testing import build_test_services
from dsf_testing.charter import InMemoryCharterStore
from dsf_testing.github import RecordingRepoClient


def _charter_md() -> str:
    return render_charter(
        Charter(product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"])
    )


def _route_alpha(monkeypatch) -> None:
    monkeypatch.setattr(charter_sync, "load_registry", lambda: {})
    monkeypatch.setattr(
        charter_sync,
        "route_product",
        lambda hints, registry: Product(key="alpha", github_repo="org/alpha"),
    )


def _sweep_run() -> Run:
    return Run(trigger=TriggerKind.SCHEDULED)


async def test_sync_on_sweep_reconciles_ok(monkeypatch):
    _route_alpha(monkeypatch)
    store = InMemoryCharterStore()
    repo = RecordingRepoClient({CHARTER_PATH: (_charter_md(), "blobsha")})
    services = build_test_services(product="alpha", charter=store, repo=repo)
    run = _sweep_run()

    await sync_charter_on_sweep(services, run)

    stored = await store.get_charter("alpha")
    assert stored is not None and stored.status == CharterStatus.OK
    assert any(r.station == STATION and "status=OK" in r.message for r in run.audit)


async def test_sync_on_sweep_without_app_audits_and_skips(monkeypatch):
    _route_alpha(monkeypatch)
    services = build_test_services(product="alpha", charter=InMemoryCharterStore(), repo=None)
    run = _sweep_run()

    await sync_charter_on_sweep(services, run)

    assert await services.charter.get_charter("alpha") is None
    assert any(r.station == STATION and "no GitHub App" in r.message for r in run.audit)


async def test_sync_on_sweep_unregistered_product_skips(monkeypatch):
    monkeypatch.setattr(charter_sync, "load_registry", lambda: {})
    monkeypatch.setattr(charter_sync, "route_product", lambda hints, registry: None)
    repo = RecordingRepoClient({CHARTER_PATH: (_charter_md(), "s")})
    services = build_test_services(product="alpha", charter=InMemoryCharterStore(), repo=repo)
    run = _sweep_run()

    await sync_charter_on_sweep(services, run)

    assert any(r.station == STATION and "not in registry" in r.message for r in run.audit)


async def test_sync_on_sweep_no_product_is_noop():
    services = build_test_services(product=None, charter=InMemoryCharterStore())
    run = _sweep_run()
    await sync_charter_on_sweep(services, run)
    assert run.audit == []


async def test_sync_on_sweep_never_raises(monkeypatch):
    _route_alpha(monkeypatch)

    class Boom:
        async def read_file(self, *args, **kwargs):
            raise RuntimeError("network down")

    services = build_test_services(product="alpha", charter=InMemoryCharterStore(), repo=Boom())
    run = _sweep_run()

    await sync_charter_on_sweep(services, run)  # must not raise
    assert any(r.station == STATION and "error" in r.message.lower() for r in run.audit)


async def test_run_sweep_invokes_charter_sync_before_the_line(monkeypatch):
    # The sweep must sync the charter *before* driving the conveyor.
    from dsf.orchestrator import conveyor
    from dsf.triggers import scheduler

    order: list[str] = []

    async def fake_sync(services, run):
        order.append("sync")

    async def fake_line(run, services):
        order.append("line")
        return run

    monkeypatch.setattr(charter_sync, "sync_charter_on_sweep", fake_sync)
    monkeypatch.setattr(conveyor, "run_line", fake_line)
    services = build_test_services(product="alpha")

    await scheduler.run_sweep(services)
    assert order == ["sync", "line"]
