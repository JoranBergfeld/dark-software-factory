"""Tests for typed flag accessors (plan Task 1.2)."""

from __future__ import annotations

from dsf.config.flags import (
    DEFAULT_THRESHOLD,
    agent_enabled,
    critic_enabled,
    dry_run_global,
    threshold,
    triggers_paused,
    weights,
)
from dsf.contracts.enums import SourceKind, TriggerKind
from dsf.fakes import FakeConfigStore


def test_critic_enabled_respects_disabled_flag():
    cfg = FakeConfigStore.from_defaults()
    assert critic_enabled(cfg, "duplication") is True
    cfg.set_flag("critic.duplication", False)
    assert critic_enabled(cfg, "duplication") is False
    # Other critics unaffected.
    assert critic_enabled(cfg, "grounding") is True


def test_critic_enabled_per_product_override():
    cfg = FakeConfigStore.from_defaults()
    cfg.set_flag("critic.value", False, product="microbi")
    assert critic_enabled(cfg, "value", product="microbi") is False
    assert critic_enabled(cfg, "value", product="homelab-dash") is True


def test_agent_enabled_accepts_enum_and_str():
    cfg = FakeConfigStore.from_defaults()
    assert agent_enabled(cfg, SourceKind.SENTRY) is True
    assert agent_enabled(cfg, "SENTRY") is True
    cfg.set_flag("agent.SENTRY", False)
    assert agent_enabled(cfg, SourceKind.SENTRY) is False


def test_triggers_paused():
    cfg = FakeConfigStore.from_defaults()
    assert triggers_paused(cfg, TriggerKind.SIGNAL) is False
    cfg.set_flag("trigger.SIGNAL.paused", True)
    assert triggers_paused(cfg, "SIGNAL") is True


def test_dry_run_global():
    cfg = FakeConfigStore.from_defaults()
    assert dry_run_global(cfg) is True
    cfg.set_flag("dry_run", False)
    assert dry_run_global(cfg) is False


def test_threshold_falls_back_to_default():
    cfg = FakeConfigStore.from_defaults()
    # No threshold.<product> seeded -> falls back to default_threshold (0.6).
    assert threshold(cfg, product="microbi") == DEFAULT_THRESHOLD
    assert threshold(cfg) == DEFAULT_THRESHOLD


def test_threshold_per_product_value():
    cfg = FakeConfigStore(
        {"default_threshold": 0.6, "threshold": {"microbi": 0.85}}
    )
    assert threshold(cfg, product="microbi") == 0.85
    assert threshold(cfg, product="other") == 0.6


def test_weights_returns_dict_for_given_critics():
    cfg = FakeConfigStore.from_defaults()
    critics = ["grounding", "value", "duplication"]
    result = weights(cfg, critics)
    assert result == {"grounding": 1.0, "value": 1.0, "duplication": 1.0}


def test_weights_reads_overridden_value():
    cfg = FakeConfigStore({"weight": {"value": 2.5}})
    result = weights(cfg, ["value", "cost"])
    assert result == {"value": 2.5, "cost": 1.0}
