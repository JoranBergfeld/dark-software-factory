"""Tickets stub agent tests (plan Task 2.6)."""

from __future__ import annotations

import pytest

from dsf.agents.tickets.backend import TicketsFakeBackend, TicketsMcpBackend
from dsf.agents.tickets.main import app, build_agent
from dsf.contracts.enums import SourceKind


async def test_tickets_fake_returns_empty():
    backend = TicketsFakeBackend()
    out = await backend.gather({"product_hints": ["alpha"]})
    assert out == []
    assert backend.calls == [{"product_hints": ["alpha"]}]


async def test_tickets_mcp_not_implemented():
    with pytest.raises(NotImplementedError, match="tickets source not yet integrated"):
        await TicketsMcpBackend().gather({})


def test_tickets_agent_builds_with_tickets_kind():
    agent = build_agent()
    assert agent.kind == SourceKind.TICKETS
    assert app is not None
