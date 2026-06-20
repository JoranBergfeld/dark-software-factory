"""Incidents source agent tests (fixture backend + registry wiring)."""

from __future__ import annotations

from dsf.agents.incidents.backend import IncidentsFixtureBackend
from dsf.contracts.enums import SourceKind


async def test_fixture_backend_returns_grounded_incident_evidence():
    backend = IncidentsFixtureBackend()
    items = await backend.gather({"product_hints": ["microbi"]})

    assert len(items) >= 1
    for item in items:
        assert item.source_agent == "incidents"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.INCIDENTS
    assert backend.calls == [{"product_hints": ["microbi"]}]


def test_incidents_kind_exists():
    assert SourceKind.INCIDENTS.value == "INCIDENTS"
