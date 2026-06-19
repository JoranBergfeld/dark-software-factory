"""Decision engine tests (plan Task 3.9)."""

from __future__ import annotations

from dsf.config.flags import weights
from dsf.config.store import InMemoryConfigStore
from dsf.container import build_services
from dsf.contracts.enums import Verdict
from dsf.contracts.models import CouncilVerdict, CriticScore
from dsf.council.decision import decide
from dsf_testing import make_evidence, make_proposal, make_run


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


def test_seeded_weight_shifts_council_weighted_score():
    """A weight seeded under the canonical ``weight.<critic>`` block flows
    through ``weights()`` into the verdict, shifting the weighted score toward
    the up-weighted critic (#5). This is the single location the Control Center
    tweaks at runtime, so 'governing on the go' moves real decisions."""
    scores = [
        CriticScore(critic="value", score=1.0),
        CriticScore(critic="cost", score=0.0),
    ]

    # Equal default weights -> plain mean.
    default_weights = weights(InMemoryConfigStore.from_defaults(), ["value", "cost"])
    base = CouncilVerdict.from_scores("p", scores, 0.6, default_weights)
    assert base.weighted_score == 0.5

    # Up-weight 'value' 3:1 via the canonical top-level block only.
    seeded_weights = weights(InMemoryConfigStore({"weight": {"value": 3.0}}), ["value", "cost"])
    up = CouncilVerdict.from_scores("p", scores, 0.6, seeded_weights)
    assert up.weighted_score == 0.75
    assert up.weighted_score > base.weighted_score


async def test_jury_dissent_escalates_under_supervised():
    services = build_services("local")
    services.model.register("[jury:skeptic]", lambda system, prompt: "NO-GO")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    verdict = await decide(prop, run, services)
    # ACCEPT recommendation, but a 2-1 jury under supervised maturity escalates.
    assert verdict.verdict == Verdict.ESCALATE
    assert verdict.jury is not None
    assert len(verdict.jury.votes) == 3


async def test_unanimous_jury_against_kills_a_strong_recommendation():
    services = build_services("local")
    for persona in ("pragmatist", "skeptic", "user_advocate"):
        services.model.register(f"[jury:{persona}]", lambda system, prompt: "NO-GO")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    verdict = await decide(prop, run, services)
    assert verdict.verdict == Verdict.KILL


async def test_accept_path_populates_the_jury():
    services = build_services("local")
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")
    verdict = await decide(prop, run, services)
    assert verdict.verdict == Verdict.ACCEPT
    assert verdict.jury is not None
    assert len(verdict.jury.votes) == 3
    assert all(v.go for v in verdict.jury.votes)
