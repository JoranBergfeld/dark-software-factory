"""Tier-2 calibration — shift critic weights toward predictive critics.

Track each critic's score against the eventual human verdict; critics whose
scores correlate with approval get their weight nudged up, those that don't get
nudged down. Pure, deterministic recompute + an async helper that reads stored
history from memory and returns *proposed* weights for the Control Center to
accept (it never writes config itself).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import DEFAULT_WEIGHT
from dsf.config.flags import weights as current_weights

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.learning.feedback_watcher import PrOutcome

#: Weight clamp bounds.
MIN_WEIGHT = 0.5
MAX_WEIGHT = 1.5
#: How far a perfectly (anti-)correlated critic can move from neutral.
_MAX_SHIFT = MAX_WEIGHT - DEFAULT_WEIGHT


def _clamp(value: float) -> float:
    """Clamp a weight to ``[MIN_WEIGHT, MAX_WEIGHT]``."""
    return max(MIN_WEIGHT, min(MAX_WEIGHT, value))


def _point_biserial(history: list[tuple[float, bool]]) -> float:
    """Correlation-style signal in [-1, 1] between scores and approval.

    Returns the difference between the mean score of eventually-approved cases
    and the mean score of not-approved cases, normalized by the score spread.
    Positive => high scores track approval (a predictive critic). With only one
    class present, or no spread, returns 0.0 (no evidence to move the weight).
    """
    if not history:
        return 0.0
    approved = [s for s, ok in history if ok]
    rejected = [s for s, ok in history if not ok]
    if not approved or not rejected:
        return 0.0

    all_scores = [s for s, _ in history]
    spread = max(all_scores) - min(all_scores)
    if spread <= 0.0:
        return 0.0

    mean_approved = sum(approved) / len(approved)
    mean_rejected = sum(rejected) / len(rejected)
    return (mean_approved - mean_rejected) / spread


def recompute_weights(
    outcomes: list[PrOutcome],
    critic_scores: dict[str, list[tuple[float, bool]]],
) -> dict[str, float]:
    """Compute calibrated critic weights from per-critic score history.

    ``critic_scores`` maps each critic to a list of ``(score, was_eventually_approved)``
    pairs. A critic whose scores correlate with approval is shifted above the
    neutral default; an uncorrelated/anti-correlated critic is shifted below it.
    Pure and deterministic; output clamped to ``[0.5, 1.5]``. ``outcomes`` is
    accepted for context/future heuristics and does not affect the result here.
    """
    _ = outcomes
    result: dict[str, float] = {}
    for critic, history in critic_scores.items():
        corr = _point_biserial(history)
        result[critic] = _clamp(DEFAULT_WEIGHT + corr * _MAX_SHIFT)
    return result


def _outcome_to_approved(verdict: str) -> bool:
    """Whether a stored verdict counts as 'eventually approved'."""
    return verdict == "approved"


async def proposed_weight_update(services: Services, critics: list[str]) -> dict[str, float]:
    """Compute proposed critic weights from memory-stored outcome history.

    Reads ``pr_outcome`` records and any stored per-critic scores from the
    memory long-term tier. When no history is available, returns the *current*
    configured weights unchanged (no proposal). The Control Center later shows
    the returned map as proposals to accept.
    """
    records = await services.memory.query_similar("pr_outcome", "pr_outcome", k=1000)

    critic_scores: dict[str, list[tuple[float, bool]]] = {c: [] for c in critics}

    for rec in records:
        approved = _outcome_to_approved(str(rec.get("verdict", "")))
        scores = rec.get("critic_scores")
        if isinstance(scores, dict):
            for critic, score in scores.items():
                if critic in critic_scores:
                    try:
                        critic_scores[critic].append((float(score), approved))
                    except (TypeError, ValueError):
                        continue

    has_history = any(history for history in critic_scores.values())
    if not has_history:
        return current_weights(services.config, critics)

    # ``outcomes`` arg is contextual only; the records' ``critic_scores`` carry
    # the signal we actually use.
    return recompute_weights([], critic_scores)


__all__ = [
    "MAX_WEIGHT",
    "MIN_WEIGHT",
    "proposed_weight_update",
    "recompute_weights",
]
