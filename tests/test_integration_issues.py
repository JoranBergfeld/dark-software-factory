"""Integration tests for issues #2, #3, #4, #5 — council/learning loop correctness."""

from __future__ import annotations

import json
from pathlib import Path

from dsf.config.flags import weights
from dsf.config.store import InMemoryConfigStore
from dsf.container import build_services
from dsf.contracts.enums import Verdict
from dsf.contracts.models import CouncilVerdict, CriticScore
from dsf.council.critics import strategic_fit
from dsf.learning.calibration import proposed_weight_update
from dsf.learning.feedback_watcher import PrOutcome, record_outcome
from dsf.memory.consolidation import consolidate_run
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations import s5_council
from tests.council.conftest import make_evidence, make_proposal, make_run

# ---------------------------------------------------------------------------
# Issue #2 — Learning loop: consolidate_run writes text; strategic_fit reads it
# ---------------------------------------------------------------------------


async def test_consolidate_run_lesson_text_boosts_strategic_fit():
    """consolidate_run -> get_lessons -> strategic_fit boost — no hand-fed text key."""
    services = build_services("local")
    run = make_run([make_evidence("feature ask")])
    prop = make_proposal(run, product="alpha")

    # Baseline: no lessons yet
    base = await strategic_fit.evaluate(prop, run, services)
    assert base.score == strategic_fit.DEFAULT_SCORE

    # Write a lesson via the real consolidation path with strategic terms in rationale
    verdict = CouncilVerdict(
        proposal_id=prop.id,
        verdict=Verdict.ACCEPT,
        weighted_score=0.8,
        threshold=0.6,
        rationale="aligned with roadmap and strategic priority",
    )
    await consolidate_run(run, verdict, services.memory)

    # strategic_fit reads lessons["text"] and finds supportive terms
    boosted = await strategic_fit.evaluate(prop, run, services)
    assert boosted.score > base.score, (
        f"Expected boost above {base.score}, got {boosted.score}. "
        "Lesson text field was not written or not read."
    )


async def test_pr_outcome_lesson_text_boosts_strategic_fit():
    """outcome_to_lesson writes text; strategic_fit reads it (PR path)."""
    from dsf.learning.lessons import outcome_to_lesson

    services = build_services("local")
    run = make_run([make_evidence("pr feature")])
    prop = make_proposal(run, product="alpha")

    base = await strategic_fit.evaluate(prop, run, services)

    outcome = PrOutcome(
        id="pr-1",
        product="alpha",
        proposal_title="Add roadmap-aligned feature",
        verdict="approved",
        rationale="Users approved: aligned with strategic roadmap priority.",
    )
    lesson = outcome_to_lesson(outcome)
    await services.memory.put_lesson(dict(lesson))

    boosted = await strategic_fit.evaluate(prop, run, services)
    assert boosted.score > base.score


# ---------------------------------------------------------------------------
# Issue #3 — Duplication: S5 writes proposal record; second run gets vetoed
# ---------------------------------------------------------------------------


async def test_s5_writes_proposal_record_and_second_run_is_deduped():
    """S5 on ACCEPT writes kind=proposal; second identical proposal is vetoed."""
    services = build_services("local")
    bb = Blackboard(services.memory)

    # --- First run ---
    run1 = make_run([make_evidence("error spike")])
    prop1 = make_proposal(run1)  # title="Improve alpha latency", problem="alpha p99 ..."
    await bb.save_proposals(run1.id, [prop1])
    result1 = await s5_council.run(run1, services)
    assert result1.proposals, "First proposal should be accepted by council"

    # --- Second run with identical title/problem ---
    run2 = make_run([make_evidence("error spike")])
    prop2 = make_proposal(run2)  # same defaults: same title + problem
    await bb.save_proposals(run2.id, [prop2])
    result2 = await s5_council.run(run2, services)
    assert result2.proposals == [], (
        "Second identical proposal should be killed by duplication critic"
    )


# ---------------------------------------------------------------------------
# Issue #4 — Calibration: S5 stores scores, record_outcome joins them, weights move
# ---------------------------------------------------------------------------


async def test_calibration_weights_move_after_pr_outcomes_with_joined_scores():
    """S5 stores critic_scores; record_outcome joins them; predictive critic weight rises."""
    services = build_services("local")

    # Simulate S5 persisting per-critic scores for two proposals
    await services.memory.put_working(
        "critic_scores:proposal-good",
        {"grounding": 0.9, "cost": 0.5},
    )
    await services.memory.put_working(
        "critic_scores:proposal-bad",
        {"grounding": 0.1, "cost": 0.5},
    )

    # Simulate PR outcomes referencing those proposals
    outcome_approved = PrOutcome(
        id="pr-good",
        product="alpha",
        proposal_id="proposal-good",
        proposal_title="Good proposal",
        verdict="approved",
    )
    outcome_rejected = PrOutcome(
        id="pr-bad",
        product="alpha",
        proposal_id="proposal-bad",
        proposal_title="Bad proposal",
        verdict="rejected",
    )
    await record_outcome(outcome_approved, services)
    await record_outcome(outcome_rejected, services)

    # Calibration: grounding is predictive (high=approved, low=rejected), cost is flat
    proposed = await proposed_weight_update(services, ["grounding", "cost"])
    assert proposed["grounding"] > proposed["cost"], (
        "grounding is predictive and should outweigh flat cost critic"
    )


# ---------------------------------------------------------------------------
# Issue #5 — Seeded weight in defaults.json measurably changes the verdict score
# ---------------------------------------------------------------------------


def test_seeded_weight_from_config_changes_weighted_score():
    """A non-default weight seeded in config changes the council verdict score."""
    scores = [
        CriticScore(critic="grounding", score=0.9, veto=False, rationale=""),
        CriticScore(critic="value", score=0.3, veto=False, rationale=""),
    ]

    # Equal weights: (1*0.9 + 1*0.3) / 2 = 0.6
    cfg_equal = InMemoryConfigStore({"weight": {"grounding": 1.0, "value": 1.0}})
    w_equal = weights(cfg_equal, ["grounding", "value"])
    v_equal = CouncilVerdict.from_scores("p1", scores, 0.9, w_equal)

    # Boosted grounding: (2*0.9 + 1*0.3) / 3 = 0.7
    cfg_boosted = InMemoryConfigStore({"weight": {"grounding": 2.0, "value": 1.0}})
    w_boosted = weights(cfg_boosted, ["grounding", "value"])
    v_boosted = CouncilVerdict.from_scores("p1", scores, 0.9, w_boosted)

    assert v_boosted.weighted_score > v_equal.weighted_score, (
        f"Boosted grounding weight should increase score: "
        f"{v_boosted.weighted_score} > {v_equal.weighted_score}"
    )


def test_defaults_json_weight_block_is_readable():
    """defaults.json has a top-level weight block; InMemoryConfigStore reads it correctly."""
    cfg = InMemoryConfigStore.from_defaults()
    result = weights(cfg, ["grounding", "value", "strategic_fit", "cost"])
    for name, w in result.items():
        assert w == 1.0, f"Expected weight 1.0 for {name}, got {w}"

    defaults_path = Path(__file__).parent.parent / "config" / "defaults.json"
    data = json.loads(defaults_path.read_text())
    assert "weight" in data, "defaults.json must have a top-level 'weight' block"
    assert "grounding" in data["weight"], "weight block must include 'grounding'"
