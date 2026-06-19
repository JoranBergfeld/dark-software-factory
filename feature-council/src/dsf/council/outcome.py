"""Deterministic, maturity-gated outcome policy for the council.

Maps a validation :class:`JuryResult` plus the product's maturity dial onto the
final :class:`Verdict` (ACCEPT / ESCALATE / KILL). The jury supplies judgment;
the maturity dial decides how much autonomy the line has before a human is asked.
Pure function: no I/O, fully deterministic.
"""

from __future__ import annotations

from enum import StrEnum

from dsf.contracts.enums import Verdict
from dsf.contracts.models import JuryResult


class Maturity(StrEnum):
    """How much autonomy the line has before a human is consulted."""

    SHADOW = "shadow"          # advise only: humans decide everything
    SUPERVISED = "supervised"  # act only on a near-unanimous jury, else escalate
    AUTONOMOUS = "autonomous"  # act on a jury majority; escalate only on a tie


def _coerce(maturity: str) -> Maturity:
    try:
        return Maturity(maturity)
    except ValueError:
        return Maturity.SUPERVISED


def decide_outcome(
    jury: JuryResult,
    *,
    maturity: str,
    consensus_bar: float,
) -> tuple[Verdict, str]:
    """Resolve the final verdict from the jury panel and the maturity dial."""
    level = _coerce(maturity)
    if not jury.votes:
        return Verdict.ESCALATE, "No jurors voted; escalating to a human."

    go = jury.go_fraction
    consensus = jury.consensus
    majority_go = jury.majority_go

    if level is Maturity.SHADOW:
        if not majority_go and consensus >= 1.0:
            return Verdict.KILL, (
                f"shadow maturity: jury unanimously against (go={go:.2f}); killed."
            )
        return Verdict.ESCALATE, (
            f"shadow maturity: routing to a human for the final call (go={go:.2f})."
        )

    if level is Maturity.AUTONOMOUS:
        if go == 0.5:
            return Verdict.ESCALATE, (
                f"autonomous maturity: jury split evenly (go={go:.2f}); escalating."
            )
        if majority_go:
            return Verdict.ACCEPT, (
                f"autonomous maturity: jury majority in favor (go={go:.2f}); proceeding."
            )
        return Verdict.KILL, (
            f"autonomous maturity: jury majority against (go={go:.2f}); killed."
        )

    # Maturity.SUPERVISED (default): act only on a strong (near-unanimous) jury.
    strong = consensus >= consensus_bar
    if strong and majority_go:
        return Verdict.ACCEPT, (
            f"supervised maturity: strong jury consensus to proceed "
            f"(go={go:.2f}, consensus={consensus:.2f} >= bar {consensus_bar:.2f})."
        )
    if strong and not majority_go:
        return Verdict.KILL, (
            f"supervised maturity: strong jury consensus against "
            f"(go={go:.2f}, consensus={consensus:.2f} >= bar {consensus_bar:.2f}); killed."
        )
    return Verdict.ESCALATE, (
        f"supervised maturity: jury split (consensus={consensus:.2f} < bar "
        f"{consensus_bar:.2f}); escalating to a human."
    )


__all__ = ["Maturity", "decide_outcome"]
