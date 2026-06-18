"""Tickets backends — STUB.

This module is intentionally a stub: the support-ticket source is contract-only
for now (design §5.1, "Tickets ... stubbed"). The backend yields no evidence so the
conveyor runs end-to-end without it; the MCP backend is a deliberate
``NotImplementedError`` placeholder selected only in azure mode.
"""

from __future__ import annotations

from dsf.contracts.models import EvidenceItem


class TicketsFixtureBackend:
    """Local/dry-run tickets backend — returns no evidence.

    Records each call and satisfies the :class:`~dsf.ports.SourceBackend`
    protocol.
    """

    def __init__(self) -> None:
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return an empty evidence list (stub)."""
        self.calls.append(dict(run_scope))
        return []


class TicketsMcpBackend:
    """Azure-mode tickets backend — not yet integrated.

    Only selected in azure mode; raises so it can never silently fabricate
    coverage while the real ticketing integration is deferred.
    """

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Always raises — the tickets source is not yet integrated."""
        raise NotImplementedError("tickets source not yet integrated")


__all__ = ["TicketsFixtureBackend", "TicketsMcpBackend"]
