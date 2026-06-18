"""WebIQ source agent (plan Task 2.5).

WebIQ is the external/industry web-research source. In local/dry-run mode the
agent uses :class:`WebIqFixtureBackend`, which loads realistic industry/competitive
market-signal evidence from ``tests/fixtures/webiq_evidence.json`` and never
touches the network. In azure mode :class:`WebIqMcpBackend` derives industry
queries from the run scope and maps web search/fetch results to
:class:`~dsf.contracts.models.EvidenceItem` objects via an injected ``search``
client.
"""

from dsf.agents.webiq.backend import WebIqFixtureBackend, WebIqMcpBackend

__all__ = ["WebIqFixtureBackend", "WebIqMcpBackend"]
