"""Tests for instance spec models and defaults."""

from __future__ import annotations

from dsf.instance.spec import InstanceSpec, default_label_taxonomy


def test_default_label_taxonomy_shape():
    tax = default_label_taxonomy()
    assert set(tax) == {"type", "area", "severity"}
    assert "feature" in tax["type"]
    assert "sev-critical" in tax["severity"]


def test_instance_spec_defaults():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.visibility == "private"
    assert spec.runtime_target == "homelab"
    assert spec.confidence_threshold == 0.6
    assert spec.label_taxonomy == default_label_taxonomy()


def test_instance_spec_derivations():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.resolved_repo() == "demo"
    assert spec.github_repo() == "acme/demo"
    assert spec.resource_group() == "rg-dsf-demo"


def test_instance_spec_explicit_repo_override():
    spec = InstanceSpec(product="demo", owner="acme", repo="demo-app")
    assert spec.resolved_repo() == "demo-app"
    assert spec.github_repo() == "acme/demo-app"
