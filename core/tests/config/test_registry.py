"""Tests for the product registry loader + routing (plan Task 1.2)."""

from __future__ import annotations

from dsf.config.registry import (
    _MIN_KEY_LEN,
    Product,
    load_registry,
    register_product,
    route_product,
    unregister_product,
)


def test_load_registry_seeds_microbi():
    registry = load_registry()
    assert set(registry) >= {"microbi"}
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
    product = route_product(["MicroBI"], registry)
    assert product is not None
    assert product.key == "microbi"


def test_route_product_unknown_hint_returns_none():
    registry = load_registry()
    assert route_product(["totally-unrelated"], registry) is None
    assert route_product([], registry) is None
    assert route_product(["", "   "], registry) is None


# ---------------------------------------------------------------------------
# New tests for issue #18: tighter word-boundary matching
# ---------------------------------------------------------------------------

def _mini_registry(*products: Product) -> dict[str, Product]:
    return {p.key: p for p in products}


def test_short_key_below_min_length_not_used_for_word_boundary():
    """A key shorter than _MIN_KEY_LEN must not match via word-boundary fallback."""
    assert len("api") < _MIN_KEY_LEN, "test assumes 'api' is below the minimum"
    short = Product(key="api", github_repo="example/api")
    registry = _mini_registry(short)
    # "api" appears as a token inside the hint but should be skipped (key too short).
    result = route_product(["rapid-api-gateway-logs"], registry)
    assert result is None


def test_word_boundary_prevents_partial_key_match():
    """Key must not match when it is a strict substring of a token in the hint."""
    p = Product(key="microbi", github_repo="joranbergfeld/microbi")
    registry = _mini_registry(p)
    # "microbi" is not a whole token in "microbial" — word boundary blocks it.
    assert route_product(["microbial-research-project"], registry) is None
    assert route_product(["microbiology-lab-report"], registry) is None


def test_exact_match_works_for_hyphenated_key():
    """Exact match (after normalisation) works for hyphenated product keys."""
    p = Product(key="metrics-dash", github_repo="example/metrics-dash")
    registry = _mini_registry(p)
    product = route_product(["metrics-dash"], registry)
    assert product is not None
    assert product.key == "metrics-dash"


def test_word_boundary_match_works_for_key_embedded_in_longer_hint():
    """Key that appears as a whole token inside a hint is routed correctly."""
    registry = load_registry()
    product = route_product(["alert: microbi latency > 500ms"], registry)
    assert product is not None
    assert product.key == "microbi"


def test_ambiguous_multi_match_returns_longest_key():
    """When two keys both match a hint the longest key wins."""
    shorter = Product(key="node", github_repo="example/node")
    longer = Product(key="nodegroup", github_repo="example/nodegroup")
    registry = _mini_registry(shorter, longer)
    # Both "node" and "nodegroup" are whole tokens in the hint.
    result = route_product(["node nodegroup performance degraded"], registry)
    assert result is not None
    assert result.key == "nodegroup"


def test_first_hint_that_matches_is_returned():
    """route_product stops at the first hint list entry that matches."""
    registry = load_registry()
    product = route_product(["no-match-here", "microbi-api error spike"], registry)
    assert product is not None
    assert product.key == "microbi"


def test_route_product_exact_match_not_gated_by_min_key_length():
    """Exact match always works even for keys shorter than _MIN_KEY_LEN."""
    short = Product(key="api", github_repo="example/api")
    registry = _mini_registry(short)
    # Hint IS the exact key — should match regardless of length.
    result = route_product(["api"], registry)
    assert result is not None
    assert result.key == "api"


# ---------------------------------------------------------------------------
# Tests for issue #34: register_product upserts into config/products.json
# ---------------------------------------------------------------------------

def test_register_product_creates_new_registry(tmp_path):
    """Registering into a non-existent file creates the canonical shape."""
    path = tmp_path / "config" / "products.json"
    written = register_product(
        Product(key="acme", github_repo="acme/acme", confidence_threshold=0.7),
        path=path,
    )
    assert written == path
    registry = load_registry(path)
    assert registry["acme"].github_repo == "acme/acme"
    assert registry["acme"].confidence_threshold == 0.7


def test_register_product_idempotent_update(tmp_path):
    """Re-registering the same key updates in place — no duplicate entries."""
    path = tmp_path / "config" / "products.json"
    register_product(Product(key="acme", github_repo="acme/old"), path=path)
    register_product(Product(key="acme", github_repo="acme/new"), path=path)
    registry = load_registry(path)
    assert list(registry) == ["acme"]
    assert registry["acme"].github_repo == "acme/new"


def test_register_product_appends_without_clobbering(tmp_path):
    """A new key is appended, leaving existing entries untouched and ordered."""
    path = tmp_path / "config" / "products.json"
    register_product(Product(key="alpha", github_repo="o/alpha"), path=path)
    register_product(Product(key="beta", github_repo="o/beta"), path=path)
    registry = load_registry(path)
    assert list(registry) == ["alpha", "beta"]
    assert registry["alpha"].github_repo == "o/alpha"


def test_unregister_product_removes_entry(tmp_path):
    path = tmp_path / "config" / "products.json"
    register_product(Product(key="alpha", github_repo="o/alpha"), path=path)
    register_product(Product(key="beta", github_repo="o/beta"), path=path)

    unregister_product("alpha", path=path)
    registry = load_registry(path)
    assert list(registry) == ["beta"]


def test_unregister_product_missing_file_is_noop(tmp_path):
    path = tmp_path / "config" / "products.json"
    written = unregister_product("alpha", path=path)
    assert written == path
    assert not path.exists()


def test_product_has_azure_monitor_scope_default():
    from dsf.config.registry import Product

    p = Product(key="microbi", github_repo="example/microbi")
    assert p.azure_monitor_scope == ""

    scoped = Product(
        key="microbi",
        github_repo="example/microbi",
        azure_monitor_scope="app-123",
    )
    assert scoped.azure_monitor_scope == "app-123"
