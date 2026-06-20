"""Deliberation council tests."""

from __future__ import annotations

from dsf.council.critics import ALL_CRITICS
from dsf.council.deliberation import (
    GATE_NAMES,
    LENS_NAMES,
    LensPosition,
    deliberate,
)
from dsf_testing import build_test_services, make_evidence, make_proposal, make_run


def test_lens_and_gate_partition():
    # Lenses are the debated dimensions; gates are the deterministic checks.
    assert set(LENS_NAMES) == {"value", "cost", "feasibility", "security", "strategic_fit"}
    assert set(GATE_NAMES) == {"grounding", "duplication"}
    # Every lens and gate is a real critic.
    assert set(LENS_NAMES) | set(GATE_NAMES) == set(ALL_CRITICS)


async def test_offline_lens_scores_match_the_deterministic_critics():
    services = build_test_services()
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
    services = build_test_services()
    services.config.set_flag("critic.cost", False)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    assert all(s.critic != "cost" for s in positions)
    assert len(positions) == len(LENS_NAMES) - 1


async def test_security_lens_vetoes_offline():
    services = build_test_services()
    run = make_run([make_evidence("auth issue")])
    prop = make_proposal(
        run, proposed_change="store plaintext password to make login simpler"
    )
    positions = await deliberate(prop, run, services)
    security = next(s for s in positions if s.critic == "security")
    assert security.veto is True


async def test_registered_lens_handler_overrides_the_fallback():
    services = build_test_services()
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


async def test_lens_falls_back_to_critic_when_the_model_raises():
    # A real model (azure mode) can raise: a validation error on an out-of-bounds
    # score, a strict-schema rejection, or a network error. The lens must degrade
    # to its deterministic critic, not crash the whole council run.
    services = build_test_services()

    def boom(system: str, prompt: str) -> LensPosition:
        raise ValueError("model returned an out-of-bounds score")

    services.model.register("[lens:value]", boom)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    value = next(s for s in positions if s.critic == "value")
    expected = await ALL_CRITICS["value"](prop, run, services)
    assert value.score == expected.score
    assert value.veto == expected.veto
    assert "model unavailable" in value.rationale


def _services_with_rounds(rounds: int):
    """Build local services whose config seeds a specific deliberation-round count.

    ``deliberation_rounds`` reads ``default_deliberation_rounds`` via ``get_value``
    (not the boolean override path), so seed the store directly. ``Services`` is a
    mutable dataclass, so the config can be swapped after build.
    """
    from dsf.config.store import load_defaults
    from dsf_testing import InMemoryConfigStore

    services = build_test_services()
    services.config = InMemoryConfigStore(
        {**load_defaults(), "default_deliberation_rounds": rounds}
    )
    return services


async def test_runs_one_model_call_per_lens_per_round():
    # Default rounds is 2 (config/defaults.json), so no seeding needed.
    services = build_test_services()
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    await deliberate(prop, run, services)
    # Five lenses x two rounds = ten model calls.
    lens_calls = [c for c in services.model.calls if "[lens:" in c[1]]
    assert len(lens_calls) == len(LENS_NAMES) * 2


async def test_second_round_sees_peer_positions_and_revises():
    services = build_test_services()  # default 2 rounds

    # The value lens scores 0.2 in round 1 (no peers in prompt) and 0.9 once it
    # sees peer positions in round 2. This proves peers are fed forward and the
    # final position is the revised one.
    def value_handler(system: str, prompt: str) -> LensPosition:
        if "Peer positions" in prompt:
            return LensPosition(score=0.9, veto=False, rationale="revised up after debate")
        return LensPosition(score=0.2, veto=False, rationale="initial")

    services.model.register("[lens:value]", value_handler)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    positions = await deliberate(prop, run, services)
    value = next(s for s in positions if s.critic == "value")
    assert value.score == 0.9
    assert "revised" in value.rationale


async def test_offline_is_stable_across_rounds():
    # With no handlers, more rounds must not change the deterministic outcome.
    one = _services_with_rounds(1)
    two = _services_with_rounds(2)
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, proposed_change="Add a small cache.")

    p1 = {s.critic: s.score for s in await deliberate(prop, run, one)}
    p2 = {s.critic: s.score for s in await deliberate(prop, run, two)}
    assert p1 == p2
