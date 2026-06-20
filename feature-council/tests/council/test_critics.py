"""Per-critic unit tests (plan Tasks 3.2-3.8)."""

from __future__ import annotations

from dsf.council.critics import (
    ALL_CRITICS,
    cost,
    duplication,
    feasibility,
    grounding,
    security,
    strategic_fit,
    value,
)
from dsf_testing import build_test_services, make_evidence, make_proposal, make_run


def test_all_critics_registry_has_seven():
    assert set(ALL_CRITICS) == {
        "grounding",
        "value",
        "duplication",
        "feasibility",
        "strategic_fit",
        "cost",
        "security",
    }


async def test_grounding_vetoes_empty_evidence_ids():
    services = build_test_services()
    run = make_run([make_evidence("error spike")])
    prop = make_proposal(run, evidence_ids=[])
    score = await grounding.evaluate(prop, run, services)
    assert score.veto is True
    assert score.score == 0.0
    assert score.critic == "grounding"


async def test_grounding_vetoes_unknown_evidence_id():
    services = build_test_services()
    run = make_run([make_evidence("error spike")])
    prop = make_proposal(run, evidence_ids=["does-not-exist"])
    score = await grounding.evaluate(prop, run, services)
    assert score.veto is True


async def test_grounding_passes_grounded_proposal():
    services = build_test_services()
    run = make_run([make_evidence("error spike")])
    prop = make_proposal(run)
    score = await grounding.evaluate(prop, run, services)
    assert score.veto is False
    assert score.score == 1.0


async def test_value_scales_with_evidence_and_severity():
    services = build_test_services()
    few = make_run([make_evidence("minor glitch")])
    many = make_run(
        [
            make_evidence("CRITICAL outage", confidence=0.9),
            make_evidence("high severity failure"),
            make_evidence("more failures"),
        ]
    )
    low = await value.evaluate(make_proposal(few), few, services)
    high = await value.evaluate(make_proposal(many), many, services)
    assert high.score > low.score
    assert low.veto is False and high.veto is False


async def test_duplication_vetoes_when_match_in_memory():
    services = build_test_services()
    run = make_run([make_evidence("error spike")])
    prop = make_proposal(run, title="Improve alpha latency", problem="alpha p99 latency elevated")
    # Pre-put a matching *filed issue* record: the critic dedups against the
    # filed-issue corpus (the same one S7 writes), keyed on title + problem.
    await services.memory.put_record(
        {"kind": "issue", "text": "Improve alpha latency alpha p99 latency elevated"}
    )
    score = await duplication.evaluate(prop, run, services)
    assert score.veto is True
    assert score.critic == "duplication"


async def test_duplication_passes_when_no_match():
    services = build_test_services()
    run = make_run([make_evidence("error spike")])
    prop = make_proposal(run)
    score = await duplication.evaluate(prop, run, services)
    assert score.veto is False
    assert score.score == 1.0


async def test_feasibility_penalizes_risky_scope():
    services = build_test_services()
    run = make_run([make_evidence("error spike")])
    small = make_proposal(run, proposed_change="Add a config flag.")
    big = make_proposal(
        run,
        proposed_change="Rewrite the entire service and migrate everything from scratch.",
    )
    s_small = await feasibility.evaluate(small, run, services)
    s_big = await feasibility.evaluate(big, run, services)
    assert s_big.score < s_small.score
    assert s_big.veto is False


async def test_strategic_fit_default_and_supportive():
    from dsf.contracts.enums import Verdict
    from dsf.contracts.models import CouncilVerdict
    from dsf.memory.consolidation import consolidate_run

    services = build_test_services()
    run = make_run([make_evidence("feature ask")])
    prop = make_proposal(run, product="alpha")
    base = await strategic_fit.evaluate(prop, run, services)
    assert base.veto is False
    assert base.score == strategic_fit.DEFAULT_SCORE

    # Use the real writer path (consolidate_run) so the lesson carries a "text" field.
    verdict = CouncilVerdict(
        proposal_id=prop.id,
        verdict=Verdict.ACCEPT,
        weighted_score=0.8,
        threshold=0.6,
        rationale="aligned with roadmap and strategic priority",
    )
    await consolidate_run(run, verdict, services.memory)
    boosted = await strategic_fit.evaluate(prop, run, services)
    assert boosted.score > base.score


async def test_cost_inverse_to_effort():
    services = build_test_services()
    run = make_run([make_evidence("error spike")])
    cheap = make_proposal(run, proposed_change="Tweak a constant.")
    pricey = make_proposal(
        run,
        proposed_change=(
            "Migrate the schema, refactor the integration, redesign infrastructure "
            + "and rewrite multiple modules " * 6
        ),
    )
    s_cheap = await cost.evaluate(cheap, run, services)
    s_pricey = await cost.evaluate(pricey, run, services)
    assert s_cheap.score > s_pricey.score
    assert s_cheap.veto is False


async def test_security_vetoes_flagged_content():
    services = build_test_services()
    run = make_run([make_evidence("auth issue")])
    bad = make_proposal(
        run, proposed_change="To fix login we will store plaintext password in the db."
    )
    score = await security.evaluate(bad, run, services)
    assert score.veto is True
    assert score.critic == "security"


async def test_security_passes_clean_content():
    services = build_test_services()
    run = make_run([make_evidence("auth issue")])
    score = await security.evaluate(make_proposal(run), run, services)
    assert score.veto is False
    assert score.score == 1.0
