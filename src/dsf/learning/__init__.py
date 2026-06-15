"""Learning loop (Phase 6) — PR feedback watcher, lessons, calibration.

This package closes the three-tier learning loop described in design §6.4:
feedback lands on the spec PR, capturing the human verdict and the
proposed-vs-final spec diff, and is distilled into:

* an immediate, retrievable product-scoped :class:`~dsf.memory.consolidation.Lesson`,
* a durable long-term record, and
* (periodically) recalibrated critic weights proposed to the Control Center.
"""

from __future__ import annotations

from dsf.learning.calibration import proposed_weight_update, recompute_weights
from dsf.learning.feedback_watcher import (
    PrOutcome,
    handle_pr_event,
    parse_pr_event,
    record_outcome,
)
from dsf.learning.lessons import outcome_to_lesson

__all__ = [
    "PrOutcome",
    "handle_pr_event",
    "outcome_to_lesson",
    "parse_pr_event",
    "proposed_weight_update",
    "recompute_weights",
    "record_outcome",
]
