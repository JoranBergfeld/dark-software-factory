"""Tracing wiring — the real OpenTelemetry Tracer behind the :class:`dsf.ports.Tracer` port.

The ``opentelemetry`` packages and the Azure Monitor exporter ship in the core
``azure`` extra, but every import is still guarded inside the function/class that
needs it, so importing this module never requires them. :func:`build_tracer`
constructs the real :class:`OtelTracer` and, given an Application Insights
connection string, configures Azure Monitor export first. It raises
``RuntimeError`` when the required packages are absent — there is no no-op
fallback in ``src``.
"""

from __future__ import annotations

from contextlib import contextmanager
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from collections.abc import Callable, Iterator

    from dsf.contracts.models import Run
    from dsf.ports import Tracer

#: OpenTelemetry GenAI semantic-convention namespace for our span attributes.
_GENAI_NS = "gen_ai"


#: Module-level guard so Azure Monitor is configured at most once per process.
_azure_monitor_configured = False


def _configure_azure_monitor(connection_string: str) -> None:
    """Configure the global OTel provider to export to Azure Monitor (App Insights).

    Real-only: lazily imports ``azure-monitor-opentelemetry`` (shipped in the
    core ``azure`` extra) and raises ``RuntimeError`` if it is absent. Idempotent
    per process — repeated calls after the first are no-ops.
    """
    global _azure_monitor_configured
    if _azure_monitor_configured:
        return
    try:
        from azure.monitor.opentelemetry import configure_azure_monitor
    except ImportError as exc:  # pragma: no cover - exercised only sans azure extra
        raise RuntimeError(
            "Azure Monitor trace export requires azure-monitor-opentelemetry "
            "(install the core 'azure' extra)"
        ) from exc
    configure_azure_monitor(connection_string=connection_string)
    _azure_monitor_configured = True


def build_tracer(
    connection_string: str = "",
    *,
    configure: Callable[[str], None] | None = None,
) -> Tracer:
    """Build the real :class:`~dsf.ports.Tracer`, optionally exporting to Azure Monitor.

    When ``connection_string`` is non-empty, configure the global OpenTelemetry
    provider to export spans to that Application Insights resource before
    constructing the tracer (``configure`` is injectable for tests; it defaults
    to the real :func:`_configure_azure_monitor`). Without a connection string the
    tracer is still real but no exporter is wired. Constructing the tracer raises
    ``RuntimeError`` when the ``opentelemetry`` packages are not installed (fail
    loud — there is no no-op fallback).
    """
    if connection_string:
        (configure or _configure_azure_monitor)(connection_string)
    return OtelTracer()


class OtelTracer:
    """Tracer that emits OpenTelemetry GenAI-convention spans.

    The OpenTelemetry imports are deferred to construction / span open so that
    importing this module never requires the optional packages. If OTel is
    unavailable at construction, this raises ``RuntimeError`` (fail loud — there
    is no no-op fallback); callers build it via :func:`build_tracer`.
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
