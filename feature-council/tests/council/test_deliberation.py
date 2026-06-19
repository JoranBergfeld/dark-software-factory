"""Deliberation council tests."""

from __future__ import annotations

from dsf.container import build_services
from dsf.council.critics import ALL_CRITICS
from dsf.council.deliberation import (
    GATE_NAMES,
    LENS_NAMES,
    LensPosition,
    deliberate,
)
from dsf_testing import make_evidence, make_proposal, make_run


def test_lens_and_gate_partition():
    # Lenses are the debated dimensions; gates are the deterministic checks.
    assert set(LENS_NAMES) == {"value", "cost", "feasibility", "security", "strategic_fit"}
    assert set(GATE_NAMES) == {"grounding", "duplication"}
    # Every lens and gate is a real critic.
    assert set(LENS_NAMES) | set(GATE_NAMES) == set(ALL_CRITICS)


async def test_offline_lens_scores_match_the_deterministic_critics():
    services = build_services("local")
    run = make_run(
        [
            make_evidence("CRITICAL outage", confidence=0.9),
            make_evidence("high severity failure", confidence=0.9),
        ]
    )
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    by_name = {s.critic: s for s in positions}

    # Exactly the enabled lenses, no gates.
    assert set(by_name) == set(LENS_NAMES)

    # Each lens position equals the deterministic critic score offline.
    for name in LENS_NAMES:
        expected = await ALL_CRITICS[name](prop, run, services)
        assert by_name[name].score == expected.score
        assert by_name[name].veto == expected.veto


async def test_disabled_lens_is_excluded():
    services = build_services("local")
    services.config.set_flag("critic.cost", False)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    assert all(s.critic != "cost" for s in positions)
    assert len(positions) == len(LENS_NAMES) - 1


async def test_security_lens_vetoes_offline():
    services = build_services("local")
    run = make_run([make_evidence("auth issue")])
    prop = make_proposal(
        run, proposed_change="store plaintext password to make login simpler"
    )
    positions = await deliberate(prop, run, services)
    security = next(s for s in positions if s.critic == "security")
    assert security.veto is True


async def test_registered_lens_handler_overrides_the_fallback():
    services = build_services("local")
    # A scripted value lens returns a structured position; the parser must use it
    # instead of the deterministic critic fallback.
    services.model.register(
        "[lens:value]",
        lambda system, prompt: LensPosition(score=0.123, veto=False, rationale="scripted"),
    )
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    value = next(s for s in positions if s.critic == "value")
    assert value.score == 0.123
    assert value.rationale == "scripted"
