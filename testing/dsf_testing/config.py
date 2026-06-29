"""In-memory ConfigStore double for tests, seeded from config/defaults.json.

The shared ``load_defaults`` / ``resolve_flag_key`` helpers stay in
``dsf.config.store`` (they are also used by the real App Configuration adapter),
so this double imports them rather than duplicating them.
"""

from __future__ import annotations

import copy
from typing import Any

from dsf.config.store import load_defaults, resolve_flag_key


class InMemoryConfigStore:
    """In-memory feature-flag/config store seeded from a dict.

    Flag namespacing convention used by the typed accessors in
    ``dsf.config`` and exercised here:

    * ``critic.<name>`` -> reads ``critics.<name>.enabled``
    * ``agent.<KIND>``  -> reads ``agents.<KIND>.enabled``
    * ``trigger.<KIND>.paused`` -> reads ``triggers.<KIND>.paused``
    * ``dry_run`` -> top-level ``dry_run`` boolean
    """

    def __init__(self, seed: dict | None = None) -> None:
        self._data: dict[str, Any] = copy.deepcopy(seed if seed is not None else load_defaults())
        # Per-product flag overrides: {(flag, product): bool}.
        self._overrides: dict[tuple[str, str | None], bool] = {}

    @classmethod
    def from_defaults(cls) -> InMemoryConfigStore:
        """Build a store seeded from ``config/defaults.json``."""
        return cls(load_defaults())

    def is_enabled(self, flag: str, product: str | None = None) -> bool:
        """Resolve an enable/pause flag, honoring per-product overrides."""
        if (flag, product) in self._overrides:
            return self._overrides[(flag, product)]
        if (flag, None) in self._overrides:
            return self._overrides[(flag, None)]

        key = resolve_flag_key(flag)
        if key is None:
            return False
        return bool(self.get_value(key, False))

    def get_value(self, key: str, default: Any = None) -> Any:
        """Read a config value by dotted key path."""
        node: Any = self._data
        for part in key.split("."):
            if not isinstance(node, dict) or part not in node:
                return default
            node = node[part]
        return node

    def set_flag(self, flag: str, value: bool, product: str | None = None) -> None:
        """Set a flag override (optionally per-product)."""
        self._overrides[(flag, product)] = bool(value)

    def snapshot(self) -> dict:
        """Return a snapshot of seed data plus active overrides."""
        snap = copy.deepcopy(self._data)
        snap["_overrides"] = {
            f"{flag}@{product or '*'}": value
            for (flag, product), value in self._overrides.items()
        }
        return snap


def config_with_product_record(
    product: str,
    *,
    github_repo: str,
    label_taxonomy: dict[str, list[str]] | None = None,
    sentry_projects: list[str] | None = None,
    grafana_dashboards: list[str] | None = None,
    foundryiq_scope: str = "",
    azure_monitor_scope: str = "",
    confidence_threshold: float | None = None,
    seed: dict | None = None,
) -> InMemoryConfigStore:
    """Seed an InMemoryConfigStore with a product record (unlabelled product.* keys).

    Mirrors what the provisioner seeds into the per-product App Configuration so
    runtime tests can exercise ``product_record`` without Azure.
    """
    data = copy.deepcopy(seed) if seed is not None else load_defaults()
    data["product"] = {
        "github_repo": github_repo,
        "label_taxonomy": label_taxonomy or {},
        "foundryiq_scope": foundryiq_scope,
        "sentry_projects": sentry_projects or [],
        "grafana_dashboards": grafana_dashboards or [],
        "azure_monitor_scope": azure_monitor_scope,
    }
    if confidence_threshold is not None:
        data.setdefault("threshold", {})[product] = confidence_threshold
    return InMemoryConfigStore(data)


__all__ = ["InMemoryConfigStore", "config_with_product_record"]
