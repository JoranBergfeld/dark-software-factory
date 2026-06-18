"""In-memory gateway doubles for offline unit tests of the Azure adapters.

The Azure adapters talk to a narrow *gateway* seam, not the raw Azure SDK.
These dict-backed gateways implement that seam so the adapters run fully
offline -- the same idea as injecting ``_run`` into ``RealGitHubClient``.
"""

from __future__ import annotations

from typing import Any


class InMemoryConfigGateway:
    """Dict-backed ConfigGateway: ``(key, label) -> value`` (JSON strings)."""

    def __init__(self, seed: dict[tuple[str, str | None], str] | None = None) -> None:
        self._d: dict[tuple[str, str | None], str] = dict(seed or {})

    def get(self, key: str, label: str | None) -> str | None:
        return self._d.get((key, label))

    def set(self, key: str, value: str, label: str | None) -> None:
        self._d[(key, label)] = value

    def list(self) -> list[tuple[str, str, str | None]]:
        return [(k, v, lbl) for (k, lbl), v in self._d.items()]


class InMemoryCosmosGateway:
    """Dict-backed CosmosGateway: ``container -> list[item dict]``.

    Implements ``upsert`` (replace-by-``id``) and a single-field equality
    ``query`` -- the only query shape the adapter issues.
    """

    def __init__(self) -> None:
        self.containers: dict[str, list[dict]] = {}

    async def upsert(self, container: str, item: dict) -> None:
        items = self.containers.setdefault(container, [])
        for idx, existing in enumerate(items):
            if existing.get("id") == item.get("id"):
                items[idx] = dict(item)
                return
        items.append(dict(item))

    async def query(self, container: str, field: str, value: Any) -> list[dict]:
        return [
            dict(i) for i in self.containers.get(container, []) if i.get(field) == value
        ]
