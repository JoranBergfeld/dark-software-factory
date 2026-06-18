"""InMemoryConfigStore — seeded defaults + protocol conformance."""

from __future__ import annotations

from dsf.config.store import InMemoryConfigStore, resolve_flag_key
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
    assert cfg.get_value("critics.value.weight") == 1.0
