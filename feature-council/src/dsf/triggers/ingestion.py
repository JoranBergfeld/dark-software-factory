"""Ingestion — map a webhook/Event Grid-shaped signal payload into a ``Run``.

``signal_to_run`` is deterministic and does no I/O: it normalizes whatever a
webhook/alert delivers into a :class:`~dsf.contracts.models.Run` with
``trigger=SIGNAL``. The full payload is stashed on ``signal_payload`` so later
stations (S1 triage onward) can re-derive any additional scope they need.
"""

from __future__ import annotations

from dsf.contracts.enums import SourceKind, TriggerKind
from dsf.contracts.models import Run

#: Default for ``dry_run`` when the payload does not specify it. Signals default
#: to dry-run so an inbound webhook never files a real issue by accident.
DEFAULT_DRY_RUN = True


def _coerce_hints(payload: dict) -> list[str]:
    """Pull product hints from ``payload['product_hints']`` (str or list)."""
    value = payload.get("product_hints")
    if isinstance(value, str) and value.strip():
        return [value.strip()]
    if isinstance(value, list):
        return [str(v).strip() for v in value if str(v).strip()]
    return []


def _coerce_source_kinds(payload: dict) -> list[SourceKind]:
    """Map ``payload['source_kinds']`` to :class:`SourceKind`, dropping unknowns."""
    raw = payload.get("source_kinds")
    if isinstance(raw, str):
        raw = [raw]
    if not isinstance(raw, list):
        return []
    kinds: list[SourceKind] = []
    for entry in raw:
        try:
            kinds.append(SourceKind(str(entry).upper()))
        except ValueError:
            continue
    return kinds


def signal_to_run(payload: dict) -> Run:
    """Build a SIGNAL-triggered :class:`Run` from a webhook ``payload``.

    * ``scope_product_hints`` <- ``payload['product_hints']`` (or ``[]``).
    * ``source_kinds`` <- ``payload['source_kinds']`` mapped to
      :class:`SourceKind` (unknown kinds dropped; missing -> ``[]``).
    * ``dry_run`` <- ``payload['dry_run']`` (default :data:`DEFAULT_DRY_RUN`).
    * the entire ``payload`` is preserved on ``signal_payload``.
    """
    payload = payload or {}
    return Run(
        trigger=TriggerKind.SIGNAL,
        signal_payload=dict(payload),
        scope_product_hints=_coerce_hints(payload),
        source_kinds=_coerce_source_kinds(payload),
        dry_run=bool(payload.get("dry_run", DEFAULT_DRY_RUN)),
    )


__all__ = ["DEFAULT_DRY_RUN", "signal_to_run"]
