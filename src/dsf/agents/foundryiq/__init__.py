"""FoundryIQ source agent — Azure AI Foundry company-knowledge retrieval.

Surfaces internal/company context (prior decisions/ADRs, roadmap fit, existing
capabilities, internal policy) as grounded evidence. Local/dry-run mode uses
:class:`FoundryIqFakeBackend`; azure mode uses :class:`FoundryIqMcpBackend`.
"""

from dsf.agents.foundryiq.backend import FoundryIqFakeBackend, FoundryIqMcpBackend

__all__ = ["FoundryIqFakeBackend", "FoundryIqMcpBackend"]
