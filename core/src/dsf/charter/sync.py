"""Deterministic charter sync: parse `.dsf/charter.md` and store it.

Two entry points, both pull-only and idempotent on the git blob SHA:

* :func:`sync_charter_text` — parse already-read text (used by ``dsf charter
  sync`` from a local working copy);
* :func:`sync_charter` — read the file from a repo ref over the GitHub App, then
  delegate to :func:`sync_charter_text` (used by ``dsf charter sync --ref`` and
  the runtime sweep).

Both record a :class:`StoredCharter` with status OK / MISSING / INVALID and never
raise on a missing/bad file — the failure is captured as state, and the last
known-good ``charter`` content is preserved.
"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import TYPE_CHECKING

from dsf.charter.markdown import CharterParseError, parse_charter
from dsf.contracts.charter import StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf.ports import CharterStore

if TYPE_CHECKING:
    from dsf.github_app_client import GitHubAppClient

#: Canonical path of the human-owned charter file in a product repo.
CHARTER_PATH = ".dsf/charter.md"


async def sync_charter_text(
    store: CharterStore,
    *,
    product: str,
    text: str,
    source_sha: str,
    source_ref: str,
) -> StoredCharter:
    """Parse charter ``text`` and store it; idempotent on ``source_sha``.

    On a parse failure the last-known-good ``charter`` content is preserved while
    ``status`` flips to INVALID. Never raises on bad content.
    """
    prior = await store.get_charter(product)
    if (
        prior is not None
        and prior.status == CharterStatus.OK
        and prior.charter is not None
        and prior.charter.source_sha == source_sha
    ):
        return prior  # idempotent: unchanged blob SHA since the last good sync

    last_good = prior.charter if prior is not None else None
    try:
        charter = parse_charter(text, product=product)
    except CharterParseError as exc:
        stored = StoredCharter(
            product=product, charter=last_good, status=CharterStatus.INVALID, last_error=str(exc)
        )
        await store.put_charter(stored)
        return stored

    charter = charter.model_copy(update={"source_sha": source_sha, "source_ref": source_ref})
    stored = StoredCharter(
        product=product, charter=charter, status=CharterStatus.OK, last_synced_at=datetime.now(UTC)
    )
    await store.put_charter(stored)
    return stored


async def sync_charter(
    store: CharterStore,
    repo_client: GitHubAppClient | None,
    *,
    product: str,
    repo: str,
    ref: str = "main",
) -> StoredCharter:
    """Read ``product``'s charter from ``repo`` over the App and store it.

    A missing App or missing file records MISSING (keeping the last known-good
    content); otherwise it delegates to :func:`sync_charter_text`.
    """
    if repo_client is None:
        prior = await store.get_charter(product)
        stored = StoredCharter(
            product=product,
            charter=prior.charter if prior is not None else None,
            status=CharterStatus.MISSING,
            last_error="no GitHub App configured to read the charter file",
        )
        await store.put_charter(stored)
        return stored

    file = await repo_client.read_file(repo, CHARTER_PATH, ref=ref)
    if file is None:
        prior = await store.get_charter(product)
        stored = StoredCharter(
            product=product,
            charter=prior.charter if prior is not None else None,
            status=CharterStatus.MISSING,
            last_error=f"{CHARTER_PATH} not found on {ref}",
        )
        await store.put_charter(stored)
        return stored

    return await sync_charter_text(
        store, product=product, text=file.text, source_sha=file.sha, source_ref=file.ref
    )


__all__ = ["CHARTER_PATH", "sync_charter", "sync_charter_text"]
