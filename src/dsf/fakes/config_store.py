"""Deterministic in-memory ConfigStore seeded from config/defaults.json."""

from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any


def _repo_root() -> Path:
    """Locate repo root (where ``config/defaults.json`` lives)."""
    # src/dsf/fakes/config_store.py -> repo root is three parents up from src/dsf.
    here = Path(__file__).resolve()
    return here.parents[3]


def load_defaults() -> dict:
    """Load the seed config from ``config/defaults.json`` at the repo root."""
    path = _repo_root() / "config" / "defaults.json"
    return json.loads(path.read_text(encoding="utf-8"))


class FakeConfigStore:
    """In-memory feature-flag/config store seeded from a dict.

    Flag namespacing convention used by the typed accessors in
    ``dsf.config`` (later phases) and exercised here:

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
    def from_defaults(cls) -> FakeConfigStore:
        """Build a store seeded from ``config/defaults.json``."""
        return cls(load_defaults())

    def is_enabled(self, flag: str, product: str | None = None) -> bool:
        """Resolve an enable/pause flag, honoring per-product overrides."""
        if (flag, product) in self._overrides:
            return self._overrides[(flag, product)]
        if (flag, None) in self._overrides:
            return self._overrides[(flag, None)]

        if flag == "dry_run":
            return bool(self._data.get("dry_run", False))
        if flag.startswith("critic."):
            name = flag.split(".", 1)[1]
            return bool(self._data.get("critics", {}).get(name, {}).get("enabled", False))
        if flag.startswith("agent."):
            name = flag.split(".", 1)[1]
            return bool(self._data.get("agents", {}).get(name, {}).get("enabled", False))
        if flag.startswith("trigger.") and flag.endswith(".paused"):
            name = flag.split(".")[1]
            return bool(self._data.get("triggers", {}).get(name, {}).get("paused", False))
        return False

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
