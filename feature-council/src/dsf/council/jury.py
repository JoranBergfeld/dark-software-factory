"""Validation jury - a model-diverse panel that validates a council recommendation.

Each juror is a distinct persona that calls the model port with a juror-specific
tag, asking for a structured :class:`JurorDecision`. When the model returns one,
the juror uses its go/no-go and rationale; otherwise the juror falls back to the
recommendation's own verdict so the panel mirrors it. With real models (or
scripted test handlers) the jurors diverge and the panel does real validation
work, separating the judge from the proposer.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from pydantic import BaseModel

from dsf.config.flags import jury_roster
from dsf.contracts.enums import Verdict
from dsf.contracts.models import JurorVote, JuryResult

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import CouncilVerdict, Proposal, Run

_DEFAULT_PERSONA = "You are a careful reviewer. Vote GO or NO-GO."


class JurorDecision(BaseModel):
    """A juror's structured go/no-go vote, as returned by the model port."""

    go: bool
    rationale: str = ""

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
        result = await services.model.complete(
            system=system, prompt=prompt, schema=JurorDecision
        )
        if isinstance(result, JurorDecision):
            go, rationale = result.go, result.rationale
        else:
            go, rationale = fallback_go, ""
        votes.append(JurorVote(juror=persona, go=go, rationale=rationale))
    return JuryResult(votes=votes)


__all__ = ["JurorDecision", "convene_jury"]
