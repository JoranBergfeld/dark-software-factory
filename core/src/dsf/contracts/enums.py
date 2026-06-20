"""Enumerations shared across the intake line contracts."""

from __future__ import annotations

from enum import StrEnum


class Severity(StrEnum):
    """Severity of a signal/issue."""

    LOW = "LOW"
    MEDIUM = "MEDIUM"
    HIGH = "HIGH"
    CRITICAL = "CRITICAL"


class SourceKind(StrEnum):
    """Kind of source backend that produced evidence."""

    SENTRY = "SENTRY"
    GRAFANA = "GRAFANA"
    FOUNDRYIQ = "FOUNDRYIQ"
    WEBIQ = "WEBIQ"
    TICKETS = "TICKETS"
    INCIDENTS = "INCIDENTS"
    AZUREMONITOR = "AZUREMONITOR"


class ProposalKind(StrEnum):
    """Whether a proposal is a new feature or a fix."""

    FEATURE = "FEATURE"
    FIX = "FIX"


class RunStatus(StrEnum):
    """Lifecycle status of a conveyor run."""

    OPEN = "OPEN"
    INVESTIGATING = "INVESTIGATING"
    SYNTHESIZING = "SYNTHESIZING"
    GROUNDING = "GROUNDING"
    COUNCIL = "COUNCIL"
    ROUTING = "ROUTING"
    FILED = "FILED"
    KILLED = "KILLED"
    ERROR = "ERROR"


class Verdict(StrEnum):
    """Council verdict outcome."""

    ACCEPT = "ACCEPT"
    ESCALATE = "ESCALATE"
    KILL = "KILL"


class TriggerKind(StrEnum):
    """How a run was triggered."""

    SCHEDULED = "SCHEDULED"
    SIGNAL = "SIGNAL"
