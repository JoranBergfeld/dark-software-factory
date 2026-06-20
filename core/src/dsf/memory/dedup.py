"""Deduplication against the retrieval tier of the memory store."""

from __future__ import annotations

from typing import TYPE_CHECKING

if TYPE_CHECKING:
    from dsf.ports import MemoryStore

#: Default similarity threshold above which two texts count as duplicates.
DEFAULT_DUP_THRESHOLD = 0.8

#: Shared record-kind for the filed-issue dedup corpus. S7 writes filed issues
#: under this kind and the duplication critic queries it, so a proposal is
#: deduplicated against what the factory has actually filed.
FILED_ISSUE_KIND = "issue"


def dedup_key(title: str, problem: str) -> str:
    """Build the canonical dedup text for an issue/proposal (title + problem).

    Deduping on title alone misses reworded-title duplicates of the same
    problem; including the problem makes the key carry the actual content.
    """
    return f"{title} {problem}".strip()


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


__all__ = ["DEFAULT_DUP_THRESHOLD", "FILED_ISSUE_KIND", "dedup_key", "is_duplicate"]
