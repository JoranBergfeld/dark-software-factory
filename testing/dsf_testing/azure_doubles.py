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

    def delete(self, key: str, label: str | None) -> None:
        self._d.pop((key, label), None)


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


class RecordingChatGateway:
    """ChatGateway double: records calls, returns a canned ``response`` string."""

    def __init__(self, response: str = "") -> None:
        self.response = response
        self.calls: list[dict] = []

    async def complete(self, system: str, prompt: str, json_schema: dict | None) -> str:
        self.calls.append(
            {"system": system, "prompt": prompt, "json_schema": json_schema}
        )
        return self.response


class RecordingEmbeddingsGateway:
    """EmbeddingsGateway double: records calls, returns canned vectors.

    ``vectors`` may be a fixed list returned verbatim for every call, or a
    ``{text: vector}`` map resolved per input text (missing texts -> a zero
    vector of ``dim`` length) so tests can model semantic closeness directly.
    """

    def __init__(
        self,
        vectors: list[list[float]] | dict[str, list[float]] | None = None,
        *,
        dim: int = 0,
    ) -> None:
        self.vectors = vectors if vectors is not None else []
        self.dim = dim
        self.calls: list[dict] = []

    async def embed(self, texts: list[str]) -> list[list[float]]:
        self.calls.append({"texts": list(texts)})
        if isinstance(self.vectors, dict):
            return [self.vectors.get(t, [0.0] * self.dim) for t in texts]
        return list(self.vectors)
