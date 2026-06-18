"""FoundryIQ source agent — Azure AI Foundry company-knowledge retrieval.

Surfaces internal/company context (prior decisions/ADRs, roadmap fit, existing
capabilities, internal policy) as grounded evidence. Local/dry-run mode uses
:class:`FoundryIqFixtureBackend`; azure mode uses :class:`FoundryIqMcpBackend`.
"""

from dsf.agents.foundryiq.backend import FoundryIqFixtureBackend, FoundryIqMcpBackend

__all__ = ["FoundryIqFixtureBackend", "FoundryIqMcpBackend"]
