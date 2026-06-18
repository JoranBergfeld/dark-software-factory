"""InMemoryConfigStore — seeded defaults + protocol conformance."""

from __future__ import annotations

from dsf.config.store import InMemoryConfigStore, load_defaults, resolve_flag_key
from dsf.ports import ConfigStore


def test_resolve_flag_key_mapping():
    assert resolve_flag_key("dry_run") == "dry_run"
    assert resolve_flag_key("critic.grounding") == "critics.grounding.enabled"
    assert resolve_flag_key("agent.SENTRY") == "agents.SENTRY.enabled"
    assert resolve_flag_key("trigger.SENTRY.paused") == "triggers.SENTRY.paused"
    assert resolve_flag_key("nonsense") is None


def test_inmemory_config_satisfies_protocol():
    assert isinstance(InMemoryConfigStore.from_defaults(), ConfigStore)


def test_config_store_seeded_defaults():
    cfg = InMemoryConfigStore.from_defaults()
    assert cfg.is_enabled("dry_run") is True
    assert cfg.is_enabled("critic.grounding") is True
    assert cfg.is_enabled("agent.SENTRY") is True
    assert cfg.is_enabled("trigger.SIGNAL.paused") is False
    assert cfg.get_value("default_threshold") == 0.6
    assert cfg.get_value("weight.value") == 1.0


def test_critic_weights_live_only_in_top_level_block():
    """``critics.<name>`` carries only ``enabled``; weights live under the
    canonical top-level ``weight`` block (the sole location ``weights()`` reads
    and the Control Center tweaks). Guards against re-introducing the dead
    ``critics.<name>.weight`` trap (#5)."""
    data = load_defaults()
    for name, critic_cfg in data["critics"].items():
        assert "weight" not in critic_cfg, (
            f"critics.{name}.weight is dead config (never read); "
            f"use the canonical top-level weight.{name} instead"
        )
    # Every critic has a seed under the canonical block.
    assert set(data["weight"]) >= set(data["critics"])
