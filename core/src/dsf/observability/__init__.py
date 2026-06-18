"""Observability — tracing wiring and dashboards."""

from __future__ import annotations

from dsf.observability.tracing import (
    OtelTracer,
    build_tracer,
    span_attrs_for_run,
)

__all__ = ["OtelTracer", "build_tracer", "span_attrs_for_run"]
