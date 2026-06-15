"""Deterministic in-memory MemoryStore using dicts + token-overlap similarity."""

from __future__ import annotations

from typing import Any


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


class FakeMemoryStore:
    """In-memory memory store.

    * working tier: a plain dict (TTL is accepted but not expired here).
    * records: list of dicts; ``query_similar`` ranks by naive token overlap.
    * lessons: list of dicts keyed by ``product``.
    """

    def __init__(self) -> None:
        self._working: dict[str, Any] = {}
        self._records: list[dict] = []
        self._lessons: list[dict] = []

    async def put_working(self, key: str, value: Any, ttl: float | None = None) -> None:
        """Store a working-tier value (ttl accepted, not enforced in fake)."""
        self._working[key] = value

    async def get_working(self, key: str) -> Any | None:
        """Read a working-tier value."""
        return self._working.get(key)

    async def put_record(self, record: dict) -> None:
        """Persist a long-term record."""
        self._records.append(dict(record))

    async def query_similar(self, text: str, kind: str, k: int = 5) -> list[dict]:
        """Rank stored records of ``kind`` by token-overlap with ``text``."""
        scored: list[tuple[float, dict]] = []
        for rec in self._records:
            if rec.get("kind") != kind:
                continue
            sim = _overlap(text, str(rec.get("text", "")))
            scored.append((sim, rec))
        scored.sort(key=lambda pair: pair[0], reverse=True)
        return [dict(rec, similarity=sim) for sim, rec in scored[:k]]

    async def put_lesson(self, lesson: dict) -> None:
        """Persist a lesson."""
        self._lessons.append(dict(lesson))

    async def get_lessons(self, product: str, k: int = 5) -> list[dict]:
        """Retrieve lessons for ``product`` (most recent first)."""
        matches = [dict(le) for le in self._lessons if le.get("product") == product]
        return matches[-k:][::-1]
