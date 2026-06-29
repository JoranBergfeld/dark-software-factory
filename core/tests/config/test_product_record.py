from __future__ import annotations

import pytest

from dsf.config.flags import product_record
from dsf.config.registry import Product
from dsf_testing import config_with_product_record
from dsf_testing.config import InMemoryConfigStore


def test_product_record_reads_unlabelled_keys():
    cfg = config_with_product_record(
        "demo",
        github_repo="acme/demo",
        label_taxonomy={"type": ["bug"]},
        sentry_projects=["proj-demo"],
        grafana_dashboards=["dash-1"],
        foundryiq_scope="kb-demo",
        azure_monitor_scope="appinsights-demo",
        confidence_threshold=0.8,
    )
    rec = product_record(cfg, "demo")
    assert isinstance(rec, Product)
    assert rec.key == "demo"
    assert rec.github_repo == "acme/demo"
    assert rec.label_taxonomy == {"type": ["bug"]}
    assert rec.sentry_projects == ["proj-demo"]
    assert rec.grafana_dashboards == ["dash-1"]
    assert rec.foundryiq_scope == "kb-demo"
    assert rec.azure_monitor_scope == "appinsights-demo"
    assert rec.confidence_threshold == 0.8


def test_product_record_threshold_falls_back_to_default():
    cfg = config_with_product_record("demo", github_repo="acme/demo")
    assert product_record(cfg, "demo").confidence_threshold == 0.6  # default_threshold


def test_product_record_fails_loud_when_missing():
    cfg = InMemoryConfigStore.from_defaults()  # no product.* keys
    with pytest.raises(ValueError, match="no product record"):
        product_record(cfg, "demo")
