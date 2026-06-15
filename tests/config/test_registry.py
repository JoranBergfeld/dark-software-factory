"""Tests for the product registry loader + routing (plan Task 1.2)."""

from __future__ import annotations

from dsf.config.registry import Product, load_registry, route_product


def test_load_registry_seeds_two_products():
    registry = load_registry()
    assert set(registry) >= {"microbi", "homelab-dash"}
    microbi = registry["microbi"]
    assert isinstance(microbi, Product)
    assert microbi.github_repo
    assert set(microbi.label_taxonomy) >= {"type", "area", "severity"}
    assert microbi.confidence_threshold == 0.6
    assert microbi.sentry_projects
    assert microbi.grafana_dashboards


def test_route_product_matches_hint_to_product():
    registry = load_registry()
    product = route_product(["microbi-api error spike"], registry)
    assert product is not None
    assert product.key == "microbi"


def test_route_product_case_insensitive_key_match():
    registry = load_registry()
    product = route_product(["HomeLab-Dash"], registry)
    assert product is not None
    assert product.key == "homelab-dash"


def test_route_product_unknown_hint_returns_none():
    registry = load_registry()
    assert route_product(["totally-unrelated"], registry) is None
    assert route_product([], registry) is None
    assert route_product(["", "   "], registry) is None
