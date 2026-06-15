"""Tests for tracing wiring: tracer factory + conveyor span emission."""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import TriggerKind
from dsf.contracts.models import Run
from dsf.fakes import FakeTracer
from dsf.observability.tracing import build_tracer, span_attrs_for_run
from dsf.orchestrator.conveyor import run_line

#: The seven station span names the conveyor must emit.
STATION_SPANS = {
    "s1_triage",
    "s2_investigation",
    "s3_synthesis",
    "s4_grounding",
    "s5_council",
    "s6_routing",
    "s7_filing",
}


def test_build_tracer_local_returns_fake() -> None:
    tracer = build_tracer("local")
    assert isinstance(tracer, FakeTracer)


def test_build_tracer_azure_does_not_raise() -> None:
    # Without opentelemetry installed this falls back to FakeTracer; either way
    # it returns a usable tracer and never raises.
    tracer = build_tracer("azure")
    assert tracer is not None
    with tracer.span("probe", run_id="r1"):
        pass


def test_span_attrs_for_run_flattens_enums() -> None:
    run = Run(trigger=TriggerKind.SIGNAL, signal_payload={}, dry_run=True)
    attrs = span_attrs_for_run(run)
    assert attrs["run_id"] == run.id
    assert attrs["trigger"] == TriggerKind.SIGNAL.value
    assert attrs["status"] == run.status.value
    assert attrs["dry_run"] is True


async def test_conveyor_records_all_station_spans() -> None:
    services = build_services("local")
    run = Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload={"product_hints": ["microbi"], "text": "checkout error spike"},
    )

    await run_line(run, services)

    recorded = {name for name, _attrs in services.tracer.spans}
    assert recorded >= STATION_SPANS, f"missing spans: {STATION_SPANS - recorded}"
