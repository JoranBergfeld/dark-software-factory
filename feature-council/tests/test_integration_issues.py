"""Integration tests for issues #2, #3, #4, #5 — council/learning loop correctness."""

from __future__ import annotations

import json
from pathlib import Path

from dsf.config.flags import weights
from dsf.contracts.enums import Verdict
from dsf.contracts.models import CouncilVerdict, CriticScore
from dsf.learning.calibration import proposed_weight_update
from dsf.learning.feedback_watcher import PrOutcome, record_outcome
from dsf.memory.consolidation import consolidate_run
from dsf.memory.dedup import FILED_ISSUE_KIND, dedup_key
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.stations import s5_council
from dsf_testing import (
    InMemoryConfigStore,
    build_test_services,
    make_evidence,
    make_proposal,
    make_run,
)

# ---------------------------------------------------------------------------
# Issue #2 — Learning loop: consolidate_run writes text; strategic_fit reads it
# ---------------------------------------------------------------------------


async def test_consolidate_run_writes_retrievable_lesson_text():
    """Issue #2: consolidate_run writes a lesson with a retrievable ``text`` field.

    The charter spec drops strategic_fit's lesson keyword-count; the durable
    learning contract is that the lesson text is written and retrievable.
    """
    services = build_test_services()
    run = make_run([make_evidence("feature ask")])
    prop = make_proposal(run, product="alpha")

    verdict = CouncilVerdict(
        proposal_id=prop.id,
        verdict=Verdict.ACCEPT,
        weighted_score=0.8,
        threshold=0.6,
        rationale="aligned with roadmap and strategic priority",
    )
    await consolidate_run(run, verdict, services.memory)

    lessons = await services.memory.get_lessons("alpha")
    assert lessons
    assert any("strategic priority" in str(le.get("text", "")) for le in lessons)


async def test_pr_outcome_writes_retrievable_lesson_text():
    """Issue #2 (PR path): outcome_to_lesson writes a retrievable ``text`` field."""
    from dsf.learning.lessons import outcome_to_lesson

    services = build_test_services()
    outcome = PrOutcome(
        id="pr-1",
        product="alpha",
        proposal_title="Add roadmap-aligned feature",
        verdict="approved",
        rationale="Users approved: aligned with strategic roadmap priority.",
    )
    lesson = outcome_to_lesson(outcome)
    await services.memory.put_lesson(dict(lesson))

    lessons = await services.memory.get_lessons("alpha")
    assert lessons
    assert any("roadmap" in str(le.get("text", "")) for le in lessons)


# ---------------------------------------------------------------------------
# Issue #3 — Duplication: S5 writes proposal record; second run gets vetoed
# ---------------------------------------------------------------------------


async def test_proposal_matching_filed_issue_is_deduped():
    """A proposal matching an already-filed issue is vetoed by the duplication critic.

    Dedup is keyed on the filed-issue corpus (written by S7), so this seeds a
    filed-issue record as a prior run would, then runs S5.
    """
    services = build_test_services()
    bb = Blackboard(services.memory)

    run = make_run([make_evidence("error spike")])
    prop = make_proposal(run)  # default title + problem
    # Seed the filed-issue corpus exactly as S7 would for a prior filing.
    await services.memory.put_record(
        {"kind": FILED_ISSUE_KIND, "text": dedup_key(prop.title, prop.problem)}
    )
    await bb.save_proposals(run.id, [prop])

    result = await s5_council.run(run, services)

    assert result.proposals == [], (
        "Proposal matching an already-filed issue should be killed by duplication"
    )


# ---------------------------------------------------------------------------
# Issue #4 — Calibration: S5 stores scores, record_outcome joins them, weights move
# ---------------------------------------------------------------------------


async def test_calibration_weights_move_after_pr_outcomes_with_joined_scores():
    """S5 stores critic_scores; record_outcome joins them; predictive critic weight rises."""
    services = build_test_services()

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

    defaults_path = Path(__file__).resolve().parents[2] / "config" / "defaults.json"
    data = json.loads(defaults_path.read_text())
    assert "weight" in data, "defaults.json must have a top-level 'weight' block"
    assert "grounding" in data["weight"], "weight block must include 'grounding'"
