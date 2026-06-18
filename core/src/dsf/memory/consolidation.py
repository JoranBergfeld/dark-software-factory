"""Consolidation ("sleep") — distill a closed run into durable memory.

On run close, write a long-term record summarizing the run + verdict and a
product-scoped :class:`Lesson` retrievable by synthesizer/critics/router on
future runs.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, TypedDict

from dsf.contracts.enums import Verdict

if TYPE_CHECKING:
    from dsf.contracts.models import CouncilVerdict, Run
    from dsf.ports import MemoryStore


class Lesson(TypedDict):
    """A product-scoped lesson distilled from one run episode."""

    product: str
    kind: str
    signal: str
    outcome: str
    rationale: str
    text: str


def _summary_text(run: Run, verdict: CouncilVerdict) -> str:
    """Build a searchable summary string for the long-term record."""
    hints = ", ".join(run.scope_product_hints) or "unknown"
    return (
        f"run={run.id} trigger={run.trigger.value} status={run.status.value} "
        f"products={hints} verdict={verdict.verdict.value} "
        f"score={verdict.weighted_score:.3f} threshold={verdict.threshold:.3f} "
        f"{verdict.rationale}"
    )


async def consolidate_run(run: Run, verdict: CouncilVerdict, store: MemoryStore) -> Lesson:
    """Write a long-term record + a Lesson for a closed run; return the Lesson.

    The Lesson is keyed by product (first scope hint, or ``"unknown"``) so it
    is retrievable via :meth:`MemoryStore.get_lessons`.
    """
    product = run.scope_product_hints[0] if run.scope_product_hints else "unknown"
    outcome = "accepted" if verdict.verdict == Verdict.ACCEPT else "killed"

    record = {
        "kind": "run_outcome",
        "run_id": run.id,
        "proposal_id": verdict.proposal_id,
        "product": product,
        "verdict": verdict.verdict.value,
        "weighted_score": verdict.weighted_score,
        "threshold": verdict.threshold,
        "text": _summary_text(run, verdict),
    }
    await store.put_record(record)

    lesson: Lesson = {
        "product": product,
        "kind": "run_outcome",
        "signal": run.trigger.value,
        "outcome": outcome,
        "rationale": verdict.rationale,
        "text": f"{outcome} {run.trigger.value} {verdict.rationale}",
    }
    await store.put_lesson(dict(lesson))
    return lesson


__all__ = ["Lesson", "consolidate_run"]
