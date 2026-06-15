"""Distill a :class:`~dsf.learning.feedback_watcher.PrOutcome` into a Lesson.

Reuses the existing :class:`~dsf.memory.consolidation.Lesson` shape so the
synthesizer/critics/router can retrieve "users rejected X because Y" at runtime
via ``MemoryStore.get_lessons(product)``.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.memory.consolidation import Lesson

if TYPE_CHECKING:
    from dsf.learning.feedback_watcher import PrOutcome

#: Lesson.kind tag for PR-outcome-derived lessons.
PR_OUTCOME_KIND = "pr_outcome"

#: Human verdict -> Lesson.outcome term.
_OUTCOME_TERMS = {
    "approved": "approved",
    "rejected": "rejected",
    "changes_requested": "changes_requested",
}


def _summarize_diff(spec_diff: str, limit: int = 240) -> str:
    """Return a short single-line summary of the proposed-vs-final spec diff."""
    if not spec_diff:
        return ""
    collapsed = " ".join(spec_diff.split())
    if len(collapsed) <= limit:
        return collapsed
    return collapsed[: limit - 1].rstrip() + "…"


def outcome_to_lesson(outcome: PrOutcome) -> Lesson:
    """Build a product-scoped :class:`Lesson` from a PR outcome.

    For ``rejected``/``changes_requested`` outcomes the rationale captures the
    *negative signal* — why the proposal was sent back — together with a short
    summary of the spec diff, so future runs can avoid repeating the rejected
    change. Approved outcomes record the positive reinforcement.
    """
    verdict_outcome = _OUTCOME_TERMS.get(outcome.verdict, outcome.verdict)
    diff_summary = _summarize_diff(outcome.spec_diff)

    parts: list[str] = []
    if outcome.verdict == "approved":
        parts.append(f"Users approved '{outcome.proposal_title}'.")
    else:
        why = outcome.rationale.strip() or "no rationale given"
        verb = "rejected" if outcome.verdict == "rejected" else "requested changes to"
        parts.append(f"Users {verb} '{outcome.proposal_title}' because {why}.")

    if outcome.rationale.strip() and outcome.verdict == "approved":
        parts.append(outcome.rationale.strip())
    if diff_summary:
        parts.append(f"Spec diff: {diff_summary}")

    rationale = " ".join(parts)

    lesson: Lesson = {
        "product": outcome.product,
        "kind": PR_OUTCOME_KIND,
        "signal": f"pr:{outcome.verdict}",
        "outcome": verdict_outcome,
        "rationale": rationale,
    }
    return lesson


__all__ = ["PR_OUTCOME_KIND", "outcome_to_lesson"]
