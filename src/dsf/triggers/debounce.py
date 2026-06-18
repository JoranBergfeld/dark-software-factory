"""Signal debounce — suppress repeat signals within a configurable TTL window.

A burst of identical alerts (e.g. Sentry firing the same regression every few
seconds) should produce one run, not many. :func:`should_suppress` builds a
stable text key for the signal and asks the memory store whether a matching
record already exists via :func:`dsf.memory.dedup.is_duplicate`.

Records are stored with a TTL (default :data:`DEFAULT_DEBOUNCE_TTL` seconds)
so the suppression window expires and the same signal is accepted again after
the window closes. The :class:`~dsf.memory.store.InMemoryMemoryStore` honours the
TTL; a real backing store should use its native TTL mechanism.

The caller is responsible for recording the signal after it decides to *accept*
it (see :func:`record_signal`), so the very next duplicate is suppressed.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.memory.dedup import DEFAULT_DUP_THRESHOLD, is_duplicate

if TYPE_CHECKING:
    from dsf.container import Services

#: Default memory record-kind used as the debounce window.
DEFAULT_WINDOW_KIND = "signal_debounce"

#: Default TTL (seconds) for debounce records.  After this window the same
#: signal is no longer suppressed and may trigger a new run.
DEFAULT_DEBOUNCE_TTL: float = 300.0


def signal_text(payload: dict) -> str:
    """Build a stable, human-readable text key for a signal ``payload``.

    Prefers an explicit ``fingerprint`` (alert systems supply one for exactly
    this grouping purpose), then ``text``/``title``/``message``/``summary``.
    Falls back to the product hints so empty-ish payloads still group sanely.
    """
    payload = payload or {}
    for key in ("fingerprint", "text", "title", "message", "summary"):
        value = payload.get(key)
        if isinstance(value, str) and value.strip():
            return value.strip()
    hints = payload.get("product_hints")
    if isinstance(hints, list) and hints:
        return "signal for " + ", ".join(str(h).strip() for h in hints if str(h).strip())
    if isinstance(hints, str) and hints.strip():
        return f"signal for {hints.strip()}"
    return "signal"


async def should_suppress(
    payload: dict,
    services: Services,
    window_kind: str = DEFAULT_WINDOW_KIND,
    threshold: float = DEFAULT_DUP_THRESHOLD,
) -> bool:
    """Return True if this signal repeats one already seen within the window.

    True means *suppress* — a near-identical signal is already in the debounce
    window. The first occurrence returns False (nothing to match yet); record it
    via :func:`record_signal` so subsequent repeats are caught.
    """
    text = signal_text(payload)
    return await is_duplicate(text, services.memory, kind=window_kind, threshold=threshold)


async def record_signal(
    payload: dict,
    services: Services,
    window_kind: str = DEFAULT_WINDOW_KIND,
    ttl: float = DEFAULT_DEBOUNCE_TTL,
) -> None:
    """Record a signal in the debounce window so its next repeat is suppressed.

    The record expires after ``ttl`` seconds so the same signal may be accepted
    again once the window closes.
    """
    text = signal_text(payload)
    await services.memory.put_record({"kind": window_kind, "text": text}, ttl=ttl)


__all__ = [
    "DEFAULT_DEBOUNCE_TTL",
    "DEFAULT_WINDOW_KIND",
    "record_signal",
    "should_suppress",
    "signal_text",
]
