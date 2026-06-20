"""Tracing wiring — the real OpenTelemetry Tracer behind the :class:`dsf.ports.Tracer` port.

This module must import cleanly *without* the ``opentelemetry`` packages
installed (they are not declared dependencies). Every OpenTelemetry import is
therefore guarded inside the function/class that needs it. :func:`build_tracer`
constructs the real :class:`OtelTracer`, which raises ``RuntimeError`` when
OpenTelemetry is not installed — there is no no-op fallback in ``src``.
"""

from __future__ import annotations

from contextlib import contextmanager
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from collections.abc import Iterator

    from dsf.contracts.models import Run
    from dsf.ports import Tracer

#: OpenTelemetry GenAI semantic-convention namespace for our span attributes.
_GENAI_NS = "gen_ai"


def build_tracer() -> Tracer:
    """Build the real :class:`~dsf.ports.Tracer`.

    Returns an :class:`OtelTracer`, whose constructor raises ``RuntimeError``
    when the ``opentelemetry`` packages are not installed (fail loud — there is
    no no-op fallback).
    """
    return OtelTracer()


class OtelTracer:
    """Tracer that emits OpenTelemetry GenAI-convention spans.

    The OpenTelemetry imports are deferred to construction / span open so that
    importing this module never requires the optional packages. If OTel becomes
    unavailable at construction, this raises ``RuntimeError`` — callers should
    use :func:`build_tracer`, which guards against that and falls back.
    """

    def __init__(self, tracer_name: str = "dsf") -> None:
        try:
            from opentelemetry import trace
        except ImportError as exc:  # pragma: no cover - exercised only sans otel
            raise RuntimeError(
                "OtelTracer requires the opentelemetry packages to be installed"
            ) from exc
        self._tracer = trace.get_tracer(tracer_name)

    @contextmanager
    def span(self, name: str, **attrs: Any) -> Iterator[Any]:
        """Open an OpenTelemetry span carrying GenAI-namespaced attributes."""
        with self._tracer.start_as_current_span(name) as otel_span:
            for key, value in attrs.items():
                otel_span.set_attribute(f"{_GENAI_NS}.{key}", _attr_value(value))
            yield otel_span


def _attr_value(value: Any) -> Any:
    """Coerce an attribute value to an OTel-acceptable scalar/sequence."""
    if isinstance(value, bool | int | float | str):
        return value
    if isinstance(value, list | tuple):
        return [str(v) for v in value]
    return str(value)


def span_attrs_for_run(run: Run) -> dict:
    """Return the standard span attributes for a conveyor run.

    Enum-typed fields are flattened to their ``.value`` for stable, serializable
    attribute values.
    """
    return {
        "run_id": run.id,
        "trigger": run.trigger.value,
        "status": run.status.value,
        "dry_run": run.dry_run,
    }


__all__ = ["OtelTracer", "build_tracer", "span_attrs_for_run"]
