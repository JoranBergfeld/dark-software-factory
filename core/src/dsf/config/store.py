"""Deterministic in-memory ConfigStore seeded from config/defaults.json."""

from __future__ import annotations

import copy
import json
from pathlib import Path
from typing import Any


def _repo_root() -> Path:
    """Locate repo root (where ``config/defaults.json`` lives)."""
    # core/src/dsf/config/store.py -> repo root is four parents up.
    here = Path(__file__).resolve()
    return here.parents[4]


def load_defaults() -> dict:
    """Load the seed config from ``config/defaults.json`` at the repo root."""
    path = _repo_root() / "config" / "defaults.json"
    return json.loads(path.read_text(encoding="utf-8"))


def resolve_flag_key(flag: str) -> str | None:
    """Map a namespaced flag token to its underlying dotted boolean key.

    Returns ``None`` for unknown flags (``is_enabled`` then reports ``False``).
    Shared by :class:`InMemoryConfigStore` and the App Configuration adapter so
    the two cannot drift.
    """
    if flag == "dry_run":
        return "dry_run"
    if flag.startswith("critic."):
        return f"critics.{flag.split('.', 1)[1]}.enabled"
    if flag.startswith("agent."):
        return f"agents.{flag.split('.', 1)[1]}.enabled"
    if flag.startswith("trigger.") and flag.endswith(".paused"):
        return f"triggers.{flag.split('.')[1]}.paused"
    if flag == "charter.amendment.enabled":
        return "charter.amendment.enabled"
    return None


class InMemoryConfigStore:
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
