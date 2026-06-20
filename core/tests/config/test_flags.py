"""Tests for typed flag accessors (plan Task 1.2)."""

from __future__ import annotations

from dsf.config.flags import (
    DEFAULT_DELIBERATION_ROUNDS,
    DEFAULT_THRESHOLD,
    agent_enabled,
    consensus_bar,
    critic_enabled,
    deliberation_rounds,
    jury_roster,
    maturity_level,
    threshold,
    triggers_paused,
    weights,
)
from dsf.contracts.enums import SourceKind, TriggerKind
from dsf_testing import InMemoryConfigStore


def test_critic_enabled_respects_disabled_flag():
    cfg = InMemoryConfigStore.from_defaults()
    assert critic_enabled(cfg, "duplication") is True
    cfg.set_flag("critic.duplication", False)
    assert critic_enabled(cfg, "duplication") is False
    # Other critics unaffected.
    assert critic_enabled(cfg, "grounding") is True


def test_critic_enabled_per_product_override():
    cfg = InMemoryConfigStore.from_defaults()
    cfg.set_flag("critic.value", False, product="microbi")
    assert critic_enabled(cfg, "value", product="microbi") is False
    assert critic_enabled(cfg, "value", product="other-product") is True


def test_agent_enabled_accepts_enum_and_str():
    cfg = InMemoryConfigStore.from_defaults()
    assert agent_enabled(cfg, SourceKind.SENTRY) is True
    assert agent_enabled(cfg, "SENTRY") is True
    cfg.set_flag("agent.SENTRY", False)
    assert agent_enabled(cfg, SourceKind.SENTRY) is False


def test_triggers_paused():
    cfg = InMemoryConfigStore.from_defaults()
    assert triggers_paused(cfg, TriggerKind.SIGNAL) is False
    cfg.set_flag("trigger.SIGNAL.paused", True)
    assert triggers_paused(cfg, "SIGNAL") is True


def test_threshold_falls_back_to_default():
    cfg = InMemoryConfigStore.from_defaults()
    # No threshold.<product> seeded -> falls back to default_threshold (0.6).
    assert threshold(cfg, product="microbi") == DEFAULT_THRESHOLD
    assert threshold(cfg) == DEFAULT_THRESHOLD


def test_threshold_per_product_value():
    cfg = InMemoryConfigStore(
        {"default_threshold": 0.6, "threshold": {"microbi": 0.85}}
    )
    assert threshold(cfg, product="microbi") == 0.85
    assert threshold(cfg, product="other") == 0.6


def test_weights_returns_dict_for_given_critics():
    cfg = InMemoryConfigStore.from_defaults()
    critics = ["grounding", "value", "duplication"]
    result = weights(cfg, critics)
    assert result == {"grounding": 1.0, "value": 1.0, "duplication": 1.0}


def test_weights_reads_overridden_value():
    cfg = InMemoryConfigStore({"weight": {"value": 2.5}})
    result = weights(cfg, ["value", "cost"])
    assert result == {"value": 2.5, "cost": 1.0}


def test_maturity_defaults_to_supervised():
    cfg = InMemoryConfigStore.from_defaults()
    assert maturity_level(cfg) == "supervised"


def test_maturity_per_product_override():
    cfg = InMemoryConfigStore(
        {"default_maturity": "supervised", "maturity": {"acme": "autonomous"}}
    )
    assert maturity_level(cfg, product="acme") == "autonomous"
    assert maturity_level(cfg, product="other") == "supervised"


def test_consensus_bar_default():
    cfg = InMemoryConfigStore.from_defaults()
    assert consensus_bar(cfg) == 0.67


def test_consensus_bar_per_product_override():
    cfg = InMemoryConfigStore(
        {"default_consensus_bar": 0.67, "consensus_bar": {"acme": 0.9}}
    )
    assert consensus_bar(cfg, product="acme") == 0.9
    assert consensus_bar(cfg, product="other") == 0.67


def test_jury_roster_default():
    cfg = InMemoryConfigStore.from_defaults()
    assert jury_roster(cfg) == ["pragmatist", "skeptic", "user_advocate"]


def test_deliberation_rounds_defaults_to_two():
    cfg = InMemoryConfigStore.from_defaults()
    assert deliberation_rounds(cfg) == 2


def test_deliberation_rounds_hard_fallback_when_unset():
    cfg = InMemoryConfigStore({})
    assert deliberation_rounds(cfg) == DEFAULT_DELIBERATION_ROUNDS


def test_deliberation_rounds_per_product_override():
    cfg = InMemoryConfigStore(
        {"default_deliberation_rounds": 2, "deliberation_rounds": {"alpha": 1}}
    )
    assert deliberation_rounds(cfg) == 2
    assert deliberation_rounds(cfg, product="alpha") == 1


def test_deliberation_rounds_is_floored_at_one():
    cfg = InMemoryConfigStore({"default_deliberation_rounds": 0})
    assert deliberation_rounds(cfg) == 1
