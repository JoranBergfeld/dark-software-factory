"""The ergonomic :class:`Memory` wrapper plus shared similarity helpers.

``Memory`` is a thin layer over the :class:`~dsf.ports.MemoryStore` port that
adds a couple of conveniences (record serialization, named tier helpers)
without hiding the underlying async semantics. The token-overlap and cosine
similarity helpers are shared with the real Cosmos-backed store in
``azure_store``.
"""

from __future__ import annotations

import math
from typing import TYPE_CHECKING, Any

from pydantic import BaseModel

if TYPE_CHECKING:
    from dsf.ports import MemoryStore


class Memory:
    """Thin ergonomic wrapper over a :class:`~dsf.ports.MemoryStore`."""

    def __init__(self, store: MemoryStore) -> None:
        self._store = store

    @property
    def store(self) -> MemoryStore:
        """The underlying port (for code that wants direct access)."""
        return self._store

    # -- working (short-term / TTL) tier -------------------------------------

    async def remember_working(self, key: str, value: Any, ttl: float | None = None) -> None:
        """Write ``value`` to the working tier under ``key`` (optional TTL)."""
        await self._store.put_working(key, value, ttl)

    async def recall_working(self, key: str) -> Any | None:
        """Read ``key`` from the working tier (``None`` if missing/expired)."""
        return await self._store.get_working(key)

    # -- long-term tier ------------------------------------------------------

    async def record(self, obj: BaseModel | dict) -> None:
        """Persist a durable long-term record.

        Accepts a pydantic model (serialized to a JSON-safe dict) or a dict.
        """
        payload = obj.model_dump(mode="json") if isinstance(obj, BaseModel) else dict(obj)
        await self._store.put_record(payload)

    async def similar(self, text: str, kind: str, k: int = 5) -> list[dict]:
        """Retrieve up to ``k`` records of ``kind`` similar to ``text``."""
        return await self._store.query_similar(text, kind, k)

    # -- lessons (consolidated learning) -------------------------------------

    async def remember_lesson(self, lesson: dict) -> None:
        """Persist a product-scoped lesson."""
        await self._store.put_lesson(lesson)

    async def lessons_for(self, product: str, k: int = 5) -> list[dict]:
        """Retrieve up to ``k`` lessons for ``product``."""
        return await self._store.get_lessons(product, k)


def _tokens(text: str) -> set[str]:
    """Lowercase alphanumeric token set."""
    return {t for t in "".join(c if c.isalnum() else " " for c in text.lower()).split() if t}


def _overlap(a: str, b: str) -> float:
    """Jaccard-style token overlap similarity in [0, 1]."""
    ta, tb = _tokens(a), _tokens(b)
    if not ta or not tb:
        return 0.0
    inter = ta & tb
    union = ta | tb
    return len(inter) / len(union)


def _cosine(a: list[float] | None, b: list[float] | None) -> float:
    """Cosine similarity of two equal-length vectors; 0.0 if either is empty."""
    if not a or not b:
        return 0.0
    dot = sum(x * y for x, y in zip(a, b, strict=True))
    na = math.sqrt(sum(x * x for x in a))
    nb = math.sqrt(sum(y * y for y in b))
    if na == 0.0 or nb == 0.0:
        return 0.0
    return dot / (na * nb)


__all__ = ["Memory"]
