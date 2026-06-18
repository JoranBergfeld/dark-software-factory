"""In-memory gateway doubles for offline unit tests of the Azure adapters.

The Azure adapters talk to a narrow *gateway* seam, not the raw Azure SDK.
These dict-backed gateways implement that seam so the adapters run fully
offline -- the same idea as injecting ``_run`` into ``RealGitHubClient``.
"""

from __future__ import annotations


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
