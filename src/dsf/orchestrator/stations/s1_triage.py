"""S1 — Intake & Triage (deterministic).

Normalize the trigger into a scoped run: set status INVESTIGATING, populate
``scope_product_hints`` / ``source_kinds`` from the signal payload (or sensible
defaults), and debounce the signal against in-flight runs via working memory. A
duplicate in-flight signal kills the run (status KILLED) so the conveyor stops
early before any investigation happens.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import agent_enabled
from dsf.contracts.enums import RunStatus, SourceKind
from dsf.memory.dedup import is_duplicate

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Run

STATION = "S1:triage"

#: Memory record-kind used to debounce in-flight signals.
SIGNAL_KIND = "signal"


def _signal_text(run: Run) -> str:
    """Best-effort human-readable text for a run's signal (for debounce)."""
    payload = run.signal_payload or {}
    for key in ("text", "message", "title", "summary"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    # Fall back to product hints joined with the trigger kind.
    hints = ", ".join(run.scope_product_hints) or "unknown"
    return f"{run.trigger.value} signal for {hints}"


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
    run.status = RunStatus.INVESTIGATING

    if not run.scope_product_hints:
        run.scope_product_hints = _derive_product_hints(run)
    if not run.source_kinds:
        run.source_kinds = _derive_source_kinds(run, services)

    hints = ", ".join(run.scope_product_hints) or "(none)"
    kinds = ", ".join(k.value for k in run.source_kinds) or "(none)"
    run.audit.append(_audit(f"triaged: products=[{hints}] sources=[{kinds}]"))

    text = _signal_text(run)
    duplicate = await is_duplicate(text, services.memory, kind=SIGNAL_KIND)
    if duplicate:
        run.status = RunStatus.KILLED
        run.audit.append(_audit("duplicate in-flight signal — killing run"))
        return run

    # Record this signal in the in-flight debounce window so a repeat is caught.
    await services.memory.put_record({"kind": SIGNAL_KIND, "text": text, "run_id": run.id})
    return run


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "SIGNAL_KIND", "run"]
