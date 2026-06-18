"""AppConfigStore — Azure App Configuration adapter, exercised offline."""

import sys

import pytest

from dsf.config.azure_store import AppConfigStore
from dsf_testing.azure_doubles import InMemoryConfigGateway


def _store(seed=None):
    return AppConfigStore(InMemoryConfigGateway(seed))


def test_get_value_parses_json():
    store = _store({("threshold", None): "0.7"})
    assert store.get_value("threshold") == 0.7


def test_get_value_missing_returns_default():
    assert _store().get_value("nope", default="d") == "d"


def test_is_enabled_reads_resolved_key():
    store = _store({("critics.grounding.enabled", None): "true"})
    assert store.is_enabled("critic.grounding") is True


def test_is_enabled_unknown_flag_is_false():
    assert _store().is_enabled("mystery") is False


def test_is_enabled_product_override_takes_precedence():
    store = _store(
        {
            ("agents.SENTRY.enabled", None): "false",
            ("agents.SENTRY.enabled", "acme"): "true",
        }
    )
    assert store.is_enabled("agent.SENTRY") is False
    assert store.is_enabled("agent.SENTRY", product="acme") is True


def test_set_flag_writes_resolved_key_and_label():
    gw = InMemoryConfigGateway()
    store = AppConfigStore(gw)
    store.set_flag("critic.security", True, product="acme")
    assert gw.get("critics.security.enabled", "acme") == "true"


def test_set_flag_unknown_raises():
    with pytest.raises(ValueError):
        _store().set_flag("bogus", True)


def test_snapshot_nests_unlabelled_and_lists_overrides():
    store = _store(
        {
            ("dry_run", None): "true",
            ("critics.security.enabled", "acme"): "false",
        }
    )
    snap = store.snapshot()
    assert snap["dry_run"] is True
    assert snap["_overrides"] == {"critics.security.enabled@acme": False}


def test_module_import_is_sdk_free():
    assert "azure.appconfiguration" not in sys.modules
