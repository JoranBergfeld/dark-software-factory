"""In-memory MemoryStore double for tests.

``InMemoryMemoryStore`` is the deterministic, offline
:class:`~dsf.ports.MemoryStore` implementation (dict-backed). ``query_similar``
ranks by embedding cosine similarity when an :class:`~dsf.ports.EmbeddingClient`
is injected, and falls back to token-overlap when none is configured (offline).
"""

from __future__ import annotations

import math
import time
from typing import TYPE_CHECKING, Any

if TYPE_CHECKING:
    from dsf.ports import EmbeddingClient


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


class InMemoryMemoryStore:
    """In-memory memory store.

    * working tier: TTL-aware dict; entries expire after their ``ttl`` seconds.
    * records: list of dicts; TTL-aware and bounded by ``max_records`` (oldest
      evicted first). ``query_similar`` filters expired entries before scoring.
    * lessons: list of dicts keyed by ``product``.
    """

    def __init__(
        self,
        max_records: int = 1000,
        embedder: EmbeddingClient | None = None,
    ) -> None:
        # working: key -> (value, ttl_seconds | None, inserted_at)
        self._working: dict[str, tuple[Any, float | None, float]] = {}
        self._records: list[dict] = []
        self._lessons: list[dict] = []
        self._max_records = max_records
        # When present, record texts are embedded on write and query_similar
        # ranks by cosine similarity; otherwise it falls back to token overlap.
        self._embedder = embedder

    async def put_working(self, key: str, value: Any, ttl: float | None = None) -> None:
        """Store a working-tier value, optionally expiring after ``ttl`` seconds."""
        self._working[key] = (value, ttl, time.monotonic())

    async def get_working(self, key: str) -> Any | None:
        """Read a working-tier value; returns None if missing or expired."""
        entry = self._working.get(key)
        if entry is None:
            return None
        value, ttl, inserted_at = entry
        if ttl is not None and (time.monotonic() - inserted_at) >= ttl:
            del self._working[key]
            return None
        return value

    async def put_record(self, record: dict, ttl: float | None = None) -> None:
        """Persist a record, optionally expiring after ``ttl`` seconds.

        When the store exceeds ``max_records``, the oldest entries are evicted
        to keep memory growth bounded.
        """
        entry = dict(record)
        entry["_inserted_at"] = time.monotonic()
        if ttl is not None:
            entry["_ttl"] = ttl
        if self._embedder is not None:
            vectors = await self._embedder.embed([str(record.get("text", ""))])
            entry["_vector"] = vectors[0] if vectors else None
        self._records.append(entry)
        # Evict oldest entries if over the cap.
        if len(self._records) > self._max_records:
            self._records = self._records[-self._max_records :]

    async def query_similar(self, text: str, kind: str, k: int = 5) -> list[dict]:
        """Rank non-expired records of ``kind`` similar to ``text``.

        Uses embedding cosine similarity when an embedder is configured;
        otherwise falls back to token-overlap.
        """
        now = time.monotonic()
        candidates: list[dict] = []
        for rec in self._records:
            if rec.get("kind") != kind:
                continue
            inserted_at = rec.get("_inserted_at", 0.0)
            rec_ttl = rec.get("_ttl")
            if rec_ttl is not None and (now - inserted_at) >= rec_ttl:
                continue  # expired
            candidates.append(rec)

        if self._embedder is not None:
            qvecs = await self._embedder.embed([text])
            qv = qvecs[0] if qvecs else None
            scored = [(_cosine(qv, rec.get("_vector")), rec) for rec in candidates]
        else:
            scored = [
                (_overlap(text, str(rec.get("text", ""))), rec) for rec in candidates
            ]

        scored.sort(key=lambda pair: pair[0], reverse=True)
        return [
            {rk: rv for rk, rv in rec.items() if not rk.startswith("_")} | {"similarity": sim}
            for sim, rec in scored[:k]
        ]

    async def put_lesson(self, lesson: dict) -> None:
        """Persist a lesson."""
        self._lessons.append(dict(lesson))

    async def get_lessons(self, product: str, k: int = 5) -> list[dict]:
        """Retrieve lessons for ``product`` (most recent first)."""
        matches = [dict(le) for le in self._lessons if le.get("product") == product]
        return matches[-k:][::-1]


__all__ = ["InMemoryMemoryStore"]
