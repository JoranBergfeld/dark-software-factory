"""Sentry source agent (plan Task 2.2).

In local/dry-run mode the agent uses :class:`SentryFakeBackend`, which loads
realistic error-tracking evidence from ``tests/fixtures/sentry_evidence.json``.
In azure mode :class:`SentryMcpBackend` maps a Sentry query (via injected MCP
tools) to :class:`~dsf.contracts.models.EvidenceItem` objects.
"""

from dsf.agents.sentry.backend import SentryFakeBackend, SentryMcpBackend

__all__ = ["SentryFakeBackend", "SentryMcpBackend"]
