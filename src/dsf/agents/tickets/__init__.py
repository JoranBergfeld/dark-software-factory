"""Tickets source agent — STUB (plan Task 2.6).

The support-ticket source has a defined contract but no real integration yet.
In local/dry-run mode the agent uses :class:`TicketsFakeBackend`, which returns
no evidence. In azure mode :class:`TicketsMcpBackend` raises
``NotImplementedError`` because the backing system is not yet wired.
"""

from dsf.agents.tickets.backend import TicketsFakeBackend, TicketsMcpBackend

__all__ = ["TicketsFakeBackend", "TicketsMcpBackend"]
