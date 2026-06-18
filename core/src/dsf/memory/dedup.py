"""Deduplication against the retrieval tier of the memory store."""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from dsf.ports import MemoryStore

#: Default similarity threshold above which two texts count as duplicates.
DEFAULT_DUP_THRESHOLD = 0.8


async def is_duplicate(
    text: str,
    store: MemoryStore,
    kind: str,
    threshold: float = DEFAULT_DUP_THRESHOLD,
) -> bool:
    """Return True if ``text`` is a near-duplicate of an existing ``kind`` record.

    Queries ``store.query_similar`` and compares the top hit's ``similarity``
    against ``threshold``.
    """
    hits = await store.query_similar(text, kind, k=1)
    if not hits:
        return False
    top = hits[0].get("similarity", 0.0)
    return float(top) >= threshold


__all__ = ["DEFAULT_DUP_THRESHOLD", "is_duplicate"]
