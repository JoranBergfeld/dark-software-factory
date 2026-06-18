"""S1 — Intake & Triage (deterministic).

Normalize the trigger into a scoped run: set status INVESTIGATING, populate
``scope_product_hints`` / ``source_kinds`` from the signal payload (or sensible
defaults). Deduplication is delegated to the shared debounce module
(:mod:`dsf.triggers.debounce`), which maintains a TTL-bounded window so the
same signal cannot trigger more than one run within the window.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import agent_enabled
from dsf.contracts.enums import RunStatus, SourceKind
from dsf.observability.tracing import span_attrs_for_run
from dsf.triggers.debounce import DEFAULT_DEBOUNCE_TTL, record_signal, should_suppress

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Run

STATION = "S1:triage"

#: Memory record-kind used to debounce signals in the orchestrator layer.
#: Kept as a named constant so the eval harness can seed the same kind.
SIGNAL_KIND = "signal"


def _derive_product_hints(run: Run) -> list[str]:
    """Derive product hints from the signal payload (various shapes)."""
    payload = run.signal_payload or {}
    for key in ("product_hints", "products", "product"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            return [value.strip()]
        if isinstance(value, list):
            hints = [str(v).strip() for v in value if str(v).strip()]
            if hints:
                return hints
    return []


def _derive_source_kinds(run: Run, services: Services) -> list[SourceKind]:
    """Derive source kinds from the payload, else all enabled source kinds."""
    payload = run.signal_payload or {}
    raw = payload.get("source_kinds") or payload.get("sources")
    if isinstance(raw, str):
        raw = [raw]
    if isinstance(raw, list):
        kinds: list[SourceKind] = []
        for entry in raw:
            try:
                kinds.append(SourceKind(str(entry).upper()))
            except ValueError:
                continue
        if kinds:
            return kinds
    # Default: every source kind currently enabled in config.
    return [k for k in SourceKind if agent_enabled(services.config, k)]


async def run(run: Run, services: Services) -> Run:
    """Triage: scope the run, debounce, and advance status."""
    with services.tracer.span("s1_triage", **span_attrs_for_run(run)):
        run.status = RunStatus.INVESTIGATING

        if not run.scope_product_hints:
            run.scope_product_hints = _derive_product_hints(run)
        if not run.source_kinds:
            run.source_kinds = _derive_source_kinds(run, services)

        hints = ", ".join(run.scope_product_hints) or "(none)"
        kinds = ", ".join(k.value for k in run.source_kinds) or "(none)"
        run.audit.append(_audit(f"triaged: products=[{hints}] sources=[{kinds}]"))

        payload = run.signal_payload or {}
        duplicate = await should_suppress(payload, services, window_kind=SIGNAL_KIND)
        if duplicate:
            run.status = RunStatus.KILLED
            run.audit.append(_audit("duplicate signal within debounce window — killing run"))
            return run

        # Record this signal so a repeat within the TTL window is suppressed.
        await record_signal(payload, services, window_kind=SIGNAL_KIND, ttl=DEFAULT_DEBOUNCE_TTL)
        return run


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "SIGNAL_KIND", "run"]
