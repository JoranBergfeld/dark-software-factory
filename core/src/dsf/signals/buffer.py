"""In-memory signal buffer - the honest offline SignalBuffer.

Sources enqueue accepted signal payloads; the scheduled council worker drains
them on its own cadence (the governed pull of ADR 0011). This implementation is
at-most-once: ``drain`` takes ownership of the pending batch and clears it, so a
batch that fails downstream is not redelivered. That is acceptable offline and at
the current maturity because sources re-emit and debounce dedupes the repeat; the
real Azure Service Bus adapter will add lease/ack/dead-letter behind this port.
"""

from __future__ import annotations


class InMemorySignalBuffer:
    """A simple FIFO queue of pending signal payloads."""

    def __init__(self) -> None:
        self._items: list[dict] = []

    async def enqueue(self, payload: dict) -> None:
        """Append a snapshot of ``payload`` to the pending queue."""
        self._items.append(dict(payload))

    async def drain(self) -> list[dict]:
        """Return all pending payloads and clear the queue (at-most-once)."""
        items = self._items
        self._items = []
        return items


__all__ = ["InMemorySignalBuffer"]
