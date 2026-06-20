"""Tests for the orchestrator blackboard."""

from __future__ import annotations

from dsf.contracts.enums import TriggerKind
from dsf.contracts.models import Run
from dsf.orchestrator.blackboard import Blackboard
from dsf_testing import build_test_services


async def test_save_load_round_trip() -> None:
    services = build_test_services()
    bb = Blackboard(services.memory)

    run = Run(trigger=TriggerKind.SIGNAL, scope_product_hints=["microbi"])
    await bb.save(run)

    loaded = await bb.load(run.id)
    assert loaded is not None
    assert loaded.id == run.id
    assert loaded.trigger == TriggerKind.SIGNAL
    assert loaded.scope_product_hints == ["microbi"]


async def test_load_missing_returns_none() -> None:
    services = build_test_services()
    bb = Blackboard(services.memory)
    assert await bb.load("does-not-exist") is None


async def test_checkpoint_is_done_idempotency() -> None:
    services = build_test_services()
    bb = Blackboard(services.memory)
    run = Run(trigger=TriggerKind.SIGNAL)
    await bb.save(run)

    assert await bb.is_done(run.id, "S1:triage") is False
    await bb.checkpoint(run.id, "S1:triage")
    assert await bb.is_done(run.id, "S1:triage") is True
    # Idempotent — a second checkpoint keeps it done, other stations unaffected.
    await bb.checkpoint(run.id, "S1:triage")
    assert await bb.is_done(run.id, "S1:triage") is True
    assert await bb.is_done(run.id, "S2:investigation") is False


async def test_append_audit_mutates_and_persists() -> None:
    services = build_test_services()
    bb = Blackboard(services.memory)
    run = Run(trigger=TriggerKind.SIGNAL)
    await bb.save(run)

    await bb.append_audit(run, "S1:triage", "hello")
    assert len(run.audit) == 1
    reloaded = await bb.load(run.id)
    assert reloaded is not None
    assert reloaded.audit[0].message == "hello"
