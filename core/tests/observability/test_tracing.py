"""Tests for tracing wiring: real tracer factory + conveyor span emission."""

from __future__ import annotations

import pytest

from dsf.contracts.enums import TriggerKind
from dsf.contracts.models import Run
from dsf.observability.tracing import build_tracer, span_attrs_for_run
from dsf.orchestrator.conveyor import run_line
from dsf_testing import build_test_services

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


def _otel_installed() -> bool:
    try:
        import opentelemetry.trace  # noqa: F401
    except ImportError:
        return False
    return True


def test_build_tracer_is_real_only() -> None:
    """build_tracer constructs the real OTel tracer; it fails loud without OTel."""
    if _otel_installed():
        # OTel present: a usable tracer that records spans without raising.
        tracer = build_tracer()
        with tracer.span("probe", run_id="r1"):
            pass
    else:
        # OTel absent (the case here): fail loud rather than degrade to a no-op.
        with pytest.raises(RuntimeError):
            build_tracer()


def test_span_attrs_for_run_flattens_enums() -> None:
    run = Run(trigger=TriggerKind.SIGNAL, signal_payload={}, dry_run=True)
    attrs = span_attrs_for_run(run)
    assert attrs["run_id"] == run.id
    assert attrs["trigger"] == TriggerKind.SIGNAL.value
    assert attrs["status"] == run.status.value
    assert attrs["dry_run"] is True


async def test_conveyor_records_all_station_spans() -> None:
    services = build_test_services()
    run = Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload={"product_hints": ["microbi"], "text": "checkout error spike"},
    )

    await run_line(run, services)

    recorded = {name for name, _attrs in services.tracer.spans}
    assert recorded >= STATION_SPANS, f"missing spans: {STATION_SPANS - recorded}"
