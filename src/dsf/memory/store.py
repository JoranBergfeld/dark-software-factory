"""Ergonomic helper wrapping a :class:`~dsf.ports.MemoryStore` port.

``Memory`` is a thin layer over the port that adds a couple of conveniences
(record serialization, named tier helpers) without hiding the underlying
async semantics.
"""

from __future__ import annotations

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


__all__ = ["Memory"]
