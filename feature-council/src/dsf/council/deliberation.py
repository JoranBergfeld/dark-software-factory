"""Deliberation council - role-persona lens agents that argue before scoring.

The five substantive decision lenses (value, cost, feasibility, security,
strategic fit) each state a position on a proposal through the model port. With
real models registered they deliberate with genuine perspective diversity; with
no handler registered the model echoes, so each lens falls back to its existing
deterministic critic in :data:`~dsf.council.critics.ALL_CRITICS`. The offline
positions are therefore identical to the critic scores that drive the golden
suite, which keeps the synthesized recommendation byte-identical to the pre-slice
behavior.

Grounding and duplication are *gates*, not lenses: they are matters of fact, run
deterministically in the decision engine, and can veto. They are listed here as
:data:`GATE_NAMES` only so the partition is documented in one place.

This module produces lens positions. The synthesis into a single recommendation
(weighted aggregation, veto handling, threshold) stays in
:func:`dsf.council.decision._recommend` via :meth:`CouncilVerdict.from_scores`,
so the council's decision rule remains deterministic and auditable.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from pydantic import BaseModel, Field

from dsf.config.flags import critic_enabled, deliberation_rounds
from dsf.contracts.models import CriticScore
from dsf.council.critics import ALL_CRITICS
from dsf.model.client import ECHO_PREFIX

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Proposal, Run

#: The debated decision lenses (the deterministic critics minus the gates).
LENS_NAMES: tuple[str, ...] = ("value", "cost", "feasibility", "security", "strategic_fit")

#: The deterministic veto gates (matters of fact, never debated).
GATE_NAMES: tuple[str, ...] = ("grounding", "duplication")

_DEFAULT_PERSONA = "You are a careful reviewer. Score this proposal on your lens from 0.0 to 1.0."

#: Persona system prompts keyed by lens name.
_PERSONAS: dict[str, str] = {
    "value": (
        "You weigh user and business value. Score higher when the evidence shows "
        "real, severe impact. Score 0.0 to 1.0."
    ),
    "cost": (
        "You weigh cost to build. Score higher when the change is small and cheap, "
        "lower when it implies large effort. Score 0.0 to 1.0."
    ),
    "feasibility": (
        "You weigh feasibility and delivery risk. Score lower for oversized or "
        "risky scope. Score 0.0 to 1.0."
    ),
    "security": (
        "You weigh security and compliance. Veto clearly unsafe changes; otherwise "
        "score 0.0 to 1.0."
    ),
    "strategic_fit": (
        "You weigh strategic fit with the product roadmap and prior lessons. Score "
        "0.0 to 1.0."
    ),
}


class LensPosition(BaseModel):
    """A lens agent's position on a proposal, as returned by the model port."""

    score: float = Field(ge=0.0, le=1.0)
    veto: bool = False
    rationale: str = ""


def _lens_prompt(
    name: str,
    proposal: Proposal,
    peers: dict[str, CriticScore],
    round_index: int,
) -> str:
    """Build the prompt for ``name``'s position in round ``round_index`` (0-based).

    Peer positions from the previous round are included from round 2 onward so
    each lens can see and revise against the others (the adversarial step).
    """
    header = (
        f"[lens:{name}] Round {round_index + 1}. Score this proposal on the "
        f"'{name}' lens from 0.0 (poor) to 1.0 (excellent). Veto only for a hard "
        f"blocker."
    )
    body = (
        f"Proposal: {proposal.title}\n"
        f"Problem: {proposal.problem}\n"
        f"Proposed change: {proposal.proposed_change}"
    )
    if not peers:
        return f"{header}\n{body}"
    peer_lines = "\n".join(
        f"- {peer}: {pos.score:.2f} {pos.rationale}".rstrip()
        for peer, pos in sorted(peers.items())
    )
    return f"{header}\n{body}\nPeer positions from the previous round:\n{peer_lines}"


def _parse_position(result: object, name: str, fallback: CriticScore) -> CriticScore:
    """Convert a model result into this lens's :class:`CriticScore`.

    A structured :class:`LensPosition` is adopted; a deterministic echo or any
    other shape falls back to the lens's critic score so the line stays green
    offline.
    """
    if isinstance(result, LensPosition):
        return CriticScore(
            critic=name,
            score=result.score,
            veto=result.veto,
            rationale=result.rationale,
        )
    if isinstance(result, str) and not result.startswith(ECHO_PREFIX):
        # Free-text model answer with no structured position: keep the
        # deterministic score but carry the prose for the audit log.
        return fallback.model_copy(update={"rationale": result})
    return fallback


async def _lens_position(
    name: str,
    proposal: Proposal,
    run: Run,
    services: Services,
    peers: dict[str, CriticScore],
    round_index: int,
) -> CriticScore:
    """Ask one lens for its position, falling back to its deterministic critic."""
    fallback = await ALL_CRITICS[name](proposal, run, services)
    persona = _PERSONAS.get(name, _DEFAULT_PERSONA)
    prompt = _lens_prompt(name, proposal, peers, round_index)
    result = await services.model.complete(system=persona, prompt=prompt, schema=LensPosition)
    return _parse_position(result, name, fallback)


async def deliberate(proposal: Proposal, run: Run, services: Services) -> list[CriticScore]:
    """Run the deliberation council and return one final position per enabled lens.

    Each enabled lens states a position; over ``deliberation.rounds`` rounds it
    re-states after seeing the others' previous-round positions (see-and-revise).
    Only lenses whose ``critic.<name>`` flag is enabled for the proposal's product
    participate. Offline (no model handler) every position is the lens's
    deterministic critic score and is stable across rounds.
    """
    product = proposal.product
    enabled = [
        name for name in LENS_NAMES if critic_enabled(services.config, name, product=product)
    ]
    rounds = deliberation_rounds(services.config, product=product)

    positions: dict[str, CriticScore] = {}
    for round_index in range(rounds):
        revised: dict[str, CriticScore] = {}
        for name in enabled:
            peers = {peer: pos for peer, pos in positions.items() if peer != name}
            revised[name] = await _lens_position(
                name, proposal, run, services, peers, round_index
            )
        positions = revised
    return [positions[name] for name in enabled]


__all__ = ["GATE_NAMES", "LENS_NAMES", "LensPosition", "deliberate"]
