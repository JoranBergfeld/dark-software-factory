"""Decision engine tests (plan Task 3.9)."""

from __future__ import annotations

from dsf.container import build_services
from dsf.contracts.enums import Verdict
from dsf.council.decision import decide
from tests.council.conftest import make_evidence, make_proposal, make_run


async def test_veto_yields_kill():
    services = build_services("local")
    run = make_run([make_evidence("auth issue")])
    # Security veto via flagged content.
    prop = make_proposal(
        run, proposed_change="store plaintext password to make login simpler"
    )
    verdict = await decide(prop, run, services)
    assert verdict.verdict == Verdict.KILL
    assert "security" in verdict.rationale.lower()


async def test_all_pass_high_scores_yields_accept():
    services = build_services("local")
    run = make_run(
        [
            make_evidence("CRITICAL outage", confidence=0.9),
            make_evidence("high severity failure", confidence=0.9),
            make_evidence("more failures", confidence=0.9),
        ]
    )
    prop = make_proposal(run, proposed_change="Add a small cache.")
    verdict = await decide(prop, run, services)
    assert verdict.verdict == Verdict.ACCEPT
    assert verdict.weighted_score >= verdict.threshold


async def test_disabling_a_critic_excludes_it_and_still_decides():
    services = build_services("local")
    run = make_run([make_evidence("auth issue")])
    prop = make_proposal(
        run, proposed_change="store plaintext password to make login simpler"
    )

    # With security enabled -> vetoed KILL.
    base = await decide(prop, run, services)
    assert base.verdict == Verdict.KILL
    assert any(s.critic == "security" for s in base.scores)

    # Disable security -> excluded from the council; no veto from it now.
    services.config.set_flag("critic.security", False)
    after = await decide(prop, run, services)
    assert all(s.critic != "security" for s in after.scores)
    assert len(after.scores) == len(base.scores) - 1
    # decide still produces a valid verdict.
    assert after.verdict in (Verdict.ACCEPT, Verdict.KILL)


async def test_below_threshold_yields_kill():
    services = build_services("local")
    # A thin, costly, low-value (but non-vetoed) proposal should fall below the
    # default 0.6 threshold and be killed without any veto.
    run = make_run([make_evidence("cosmetic note", confidence=0.1)])
    prop = make_proposal(
        run,
        proposed_change=(
            "Migrate everything and rewrite from scratch, overhaul and rearchitect "
            "the integration, refactor the schema and infrastructure across multiple modules."
        ),
        product="alpha",
    )
    verdict = await decide(prop, run, services)
    assert verdict.verdict == Verdict.KILL
    # KILL is by threshold, not by veto.
    assert not any(s.veto for s in verdict.scores)
