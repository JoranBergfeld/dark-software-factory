"""Tests for tracing wiring: real tracer factory + conveyor span emission."""

from __future__ import annotations

import pytest

from dsf.contracts.enums import TriggerKind
from dsf.contracts.models import Run
from dsf.observability.tracing import build_tracer, span_attrs_for_run
from dsf.orchestrator.conveyor import run_line
from dsf_testing import build_test_services, config_with_product_record

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


def test_build_tracer_configures_azure_monitor_with_connection_string() -> None:
    """A connection string triggers Azure Monitor export configuration."""
    if not _otel_installed():
        pytest.skip("opentelemetry not installed")
    seen: list[str] = []
    tracer = build_tracer("InstrumentationKey=abc", configure=seen.append)
    assert seen == ["InstrumentationKey=abc"]
    with tracer.span("probe"):
        pass


def test_build_tracer_skips_export_without_connection_string() -> None:
    """No connection string means no exporter is configured (still a real tracer)."""
    if not _otel_installed():
        pytest.skip("opentelemetry not installed")
    seen: list[str] = []
    build_tracer("", configure=seen.append)
    assert seen == []


def test_build_tracer_with_connection_string_fails_loud_without_otel() -> None:
    """Asking for export without the azure extra installed fails loud (no no-op)."""
    if _otel_installed():
        pytest.skip("requires the opentelemetry/azure-monitor packages to be absent")
    with pytest.raises(RuntimeError):
        build_tracer("InstrumentationKey=abc")


def test_span_attrs_for_run_flattens_enums() -> None:
    run = Run(trigger=TriggerKind.SIGNAL, signal_payload={}, dry_run=True)
    attrs = span_attrs_for_run(run)
    assert attrs["run_id"] == run.id
    assert attrs["trigger"] == TriggerKind.SIGNAL.value
    assert attrs["status"] == run.status.value
    assert attrs["dry_run"] is True


async def test_conveyor_records_all_station_spans() -> None:
    services = build_test_services(
        product="microbi",
        config=config_with_product_record("microbi", github_repo="joranbergfeld/microbi"),
    )
    run = Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload={"product_hints": ["microbi"], "text": "checkout error spike"},
    )

    await run_line(run, services)

    recorded = {name for name, _attrs in services.tracer.spans}
    assert recorded >= STATION_SPANS, f"missing spans: {STATION_SPANS - recorded}"
