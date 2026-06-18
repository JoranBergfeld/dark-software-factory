"""Orchestrator / conveyor — blackboard, stations S1..S7, and the driver.

The conveyor sequences seven stations over a :class:`~dsf.contracts.models.Run`,
persisting run state to the :class:`~dsf.orchestrator.blackboard.Blackboard`
after each station so a crashed/re-run line resumes from the last checkpoint.
"""

from __future__ import annotations

__all__: list[str] = []
