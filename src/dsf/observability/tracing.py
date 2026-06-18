"""Tracing wiring — Tracer implementations behind the :class:`dsf.ports.Tracer` port.

This module must import cleanly *without* the ``opentelemetry`` packages
installed (they are not declared dependencies). Every OpenTelemetry import is
therefore guarded inside the function/class that needs it.

- ``local`` mode -> :class:`NoOpTracer` (records span names + attrs).
- ``azure`` mode -> :class:`OtelTracer`, which emits OpenTelemetry GenAI-convention
  spans *if* ``opentelemetry`` is importable; otherwise it falls back to the
  :class:`NoOpTracer` with a logged warning so dry-run/test never break.
"""

from __future__ import annotations

import logging
from contextlib import contextmanager
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from collections.abc import Iterator

    from dsf.contracts.models import Run
    from dsf.ports import Tracer

logger = logging.getLogger(__name__)

#: OpenTelemetry GenAI semantic-convention namespace for our span attributes.
_GENAI_NS = "gen_ai"


class NoOpTracer:
    """Null-object tracer that records the names (and attrs) of opened spans.

    The real :class:`~dsf.ports.Tracer` used for local/offline operation and as
    the fallback when OpenTelemetry is not installed. Opens and immediately closes
    each span without emitting telemetry.
    """

    def __init__(self) -> None:
        self.spans: list[tuple[str, dict]] = []

    @contextmanager
    def span(self, name: str, **attrs: Any):
        """Open (and immediately close) a recorded no-op span."""
        self.spans.append((name, dict(attrs)))
        yield self


def build_tracer(mode: str = "local") -> Tracer:
    """Build a :class:`~dsf.ports.Tracer` for ``mode``.

    ``local`` returns a :class:`NoOpTracer`. ``azure`` returns an
    :class:`OtelTracer` when OpenTelemetry is importable, otherwise it logs a
    warning and falls back to a :class:`NoOpTracer`. Unknown modes also fall back
    to the NoOpTracer (never raises).
    """
    if mode == "local":
        return NoOpTracer()
    if mode == "azure":
        if _otel_available():
            return OtelTracer()
        logger.warning(
            "opentelemetry not importable; azure tracer falling back to NoOpTracer"
        )
        return NoOpTracer()
    logger.warning("unknown tracer mode %r; falling back to NoOpTracer", mode)
    return NoOpTracer()


def _otel_available() -> bool:
    """Return whether the OpenTelemetry trace API can be imported."""
    try:
        import opentelemetry.trace  # noqa: F401
    except ImportError:
        return False
    return True


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


__all__ = ["NoOpTracer", "OtelTracer", "build_tracer", "span_attrs_for_run"]
