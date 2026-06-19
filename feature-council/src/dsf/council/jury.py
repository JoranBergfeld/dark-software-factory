"""Validation jury - a model-diverse panel that validates a council recommendation.

Each juror is a distinct persona that calls the model port with a juror-specific
tag. Offline (no registered handler) the model echoes, so each juror falls back
to the recommendation's own verdict; the panel then mirrors the deterministic
recommendation and the line stays green with no LLM. With real models (or scripted
test handlers) the jurors diverge and the panel does real validation work,
separating the judge from the proposer.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.config.flags import jury_roster
from dsf.contracts.enums import Verdict
from dsf.contracts.models import JurorVote, JuryResult
from dsf.model.client import ECHO_PREFIX

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import CouncilVerdict, Proposal, Run

_DEFAULT_PERSONA = "You are a careful reviewer. Vote GO or NO-GO."

#: Persona system prompts keyed by juror name.
_PERSONAS: dict[str, str] = {
    "pragmatist": (
        "You are a pragmatic engineer. Favor proposals that ship value soon "
        "with acceptable risk. Vote GO or NO-GO."
    ),
    "skeptic": (
        "You are a critical skeptic. Probe for weak evidence, hidden cost, and "
        "risk before agreeing. Vote GO or NO-GO."
    ),
    "user_advocate": (
        "You advocate for end users. Favor proposals that clearly improve the "
        "user experience. Vote GO or NO-GO."
    ),
}


def _vote_text(result: object) -> str:
    """Short rationale captured from a juror response (empty for echoes)."""
    if isinstance(result, str) and not result.startswith(ECHO_PREFIX):
        return result
    return ""


def _parse_vote(result: object, *, fallback: bool) -> bool:
    """Parse a juror's go/no-go from a model response.

    Falls back to ``fallback`` for the deterministic echo or any unparseable
    response. ``no-go`` is checked before ``go`` because it contains ``go``.
    """
    text = result if isinstance(result, str) else ""
    if not text or text.startswith(ECHO_PREFIX):
        return fallback
    low = text.lower()
    if "no-go" in low or "nogo" in low or "no go" in low or "reject" in low or "kill" in low:
        return False
    if "go" in low or "accept" in low or "proceed" in low:
        return True
    return fallback


async def convene_jury(
    recommendation: CouncilVerdict,
    proposal: Proposal,
    run: Run,
    services: Services,
) -> JuryResult:
    """Convene the validation jury over a council ``recommendation``."""
    fallback_go = recommendation.verdict == Verdict.ACCEPT
    votes: list[JurorVote] = []
    for persona in jury_roster(services.config):
        system = _PERSONAS.get(persona, _DEFAULT_PERSONA)
        prompt = (
            f"[jury:{persona}] Validate this council decision.\n"
            f"Proposal: {proposal.title}\n"
            f"Problem: {proposal.problem}\n"
            f"Recommendation: {recommendation.verdict.value} "
            f"(weighted score {recommendation.weighted_score:.2f} vs "
            f"threshold {recommendation.threshold:.2f}).\n"
            f"Rationale: {recommendation.rationale}\n"
            "Answer GO to proceed or NO-GO to reject."
        )
        result = await services.model.complete(system=system, prompt=prompt)
        votes.append(
            JurorVote(
                juror=persona,
                go=_parse_vote(result, fallback=fallback_go),
                rationale=_vote_text(result),
            )
        )
    return JuryResult(votes=votes)


__all__ = ["convene_jury"]
