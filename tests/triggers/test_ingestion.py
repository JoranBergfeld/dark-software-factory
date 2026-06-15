"""Tests for signal -> Run ingestion (plan Task 5.1)."""

from __future__ import annotations

from dsf.contracts.enums import SourceKind, TriggerKind
from dsf.triggers.ingestion import signal_to_run


def test_signal_to_run_maps_payload_to_signal_run_with_hints() -> None:
    payload = {
        "product_hints": ["microbi", "atlas"],
        "source_kinds": ["SENTRY", "grafana", "BOGUS"],
        "text": "error spike in checkout",
        "dry_run": True,
    }

    run = signal_to_run(payload)

    assert run.trigger == TriggerKind.SIGNAL
    assert run.scope_product_hints == ["microbi", "atlas"]
    # Known kinds mapped (case-insensitive); unknown "BOGUS" dropped.
    assert run.source_kinds == [SourceKind.SENTRY, SourceKind.GRAFANA]
    assert run.signal_payload == payload
    assert run.dry_run is True


def test_signal_to_run_defaults_dry_run_true_and_empty_scope() -> None:
    run = signal_to_run({"text": "something happened"})

    assert run.trigger == TriggerKind.SIGNAL
    assert run.scope_product_hints == []
    assert run.source_kinds == []
    # dry_run defaults to True for inbound signals.
    assert run.dry_run is True


def test_signal_to_run_string_hints_and_sources_coerced() -> None:
    run = signal_to_run({"product_hints": "microbi", "source_kinds": "SENTRY"})

    assert run.scope_product_hints == ["microbi"]
    assert run.source_kinds == [SourceKind.SENTRY]
