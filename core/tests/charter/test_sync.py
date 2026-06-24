from __future__ import annotations

from dsf.charter.markdown import git_blob_sha, render_charter
from dsf.charter.sync import CHARTER_PATH, sync_charter, sync_charter_text
from dsf.contracts.charter import Charter
from dsf.contracts.enums import CharterStatus
from dsf_testing.charter import InMemoryCharterStore
from dsf_testing.github import RecordingRepoClient


def _charter_md(vision: str = "V") -> str:
    return render_charter(
        Charter(
            product="alpha",
            vision=vision,
            target_users="U",
            goals=["g"],
            success_metrics=["m"],
        )
    )


async def test_sync_parses_and_stores_ok():
    client = RecordingRepoClient({CHARTER_PATH: (_charter_md(), "blobsha")})
    store = InMemoryCharterStore()
    stored = await sync_charter(store, client, product="alpha", repo="org/alpha")
    assert stored.status == CharterStatus.OK
    assert stored.charter is not None
    assert stored.charter.source_sha == "blobsha" and stored.charter.source_ref == "main"
    assert (await store.get_charter("alpha")).status == CharterStatus.OK


async def test_sync_missing_file_records_missing():
    store = InMemoryCharterStore()
    stored = await sync_charter(store, RecordingRepoClient({}), product="alpha", repo="org/alpha")
    assert stored.status == CharterStatus.MISSING and stored.charter is None


async def test_sync_missing_keeps_last_known_good():
    store = InMemoryCharterStore()
    ok = await sync_charter(
        store,
        RecordingRepoClient({CHARTER_PATH: (_charter_md(), "sha1")}),
        product="alpha",
        repo="org/alpha",
    )
    assert ok.status == CharterStatus.OK and ok.charter is not None

    result = await sync_charter(
        store,
        RecordingRepoClient({}),
        product="alpha",
        repo="org/alpha",
    )

    assert result.status == CharterStatus.MISSING
    assert result.charter is not None and result.charter.vision == "V"
    assert result.last_error is not None
    persisted = await store.get_charter("alpha")
    assert persisted is not None
    assert persisted.status == CharterStatus.MISSING
    assert persisted.charter is not None and persisted.charter.vision == "V"
    assert persisted.last_error == result.last_error


async def test_sync_invalid_charter_records_invalid():
    client = RecordingRepoClient({CHARTER_PATH: ("garbage, no marker", "s")})
    stored = await sync_charter(InMemoryCharterStore(), client, product="alpha", repo="org/alpha")
    assert stored.status == CharterStatus.INVALID and stored.last_error


async def test_sync_without_app_records_missing():
    stored = await sync_charter(InMemoryCharterStore(), None, product="alpha", repo="org/alpha")
    assert stored.status == CharterStatus.MISSING
    assert stored.last_error is not None and "App" in stored.last_error


async def test_sync_idempotent_on_unchanged_blob_sha():
    client = RecordingRepoClient({CHARTER_PATH: (_charter_md(), "sha1")})
    store = InMemoryCharterStore()
    first = await sync_charter(store, client, product="alpha", repo="org/alpha")
    second = await sync_charter(store, client, product="alpha", repo="org/alpha")
    assert first.status == CharterStatus.OK
    # Unchanged blob SHA -> no re-parse, no rewrite: the same stored object is returned.
    assert second.last_synced_at == first.last_synced_at


async def test_sync_changed_content_resyncs():
    store = InMemoryCharterStore()
    text_a = _charter_md(vision="V")
    text_b = _charter_md(vision="V2")
    sha_a = git_blob_sha(text_a.encode())
    sha_b = git_blob_sha(text_b.encode())
    assert sha_b != sha_a

    first = await sync_charter(
        store,
        RecordingRepoClient({CHARTER_PATH: (text_a, sha_a)}),
        product="alpha",
        repo="org/alpha",
    )
    assert first.status == CharterStatus.OK

    result = await sync_charter(
        store,
        RecordingRepoClient({CHARTER_PATH: (text_b, sha_b)}),
        product="alpha",
        repo="org/alpha",
    )

    assert result.status == CharterStatus.OK
    assert result.charter is not None
    assert result.charter.source_sha == sha_b
    assert result.charter.vision == "V2"
    persisted = await store.get_charter("alpha")
    assert persisted is not None and persisted.charter is not None
    assert persisted.status == CharterStatus.OK
    assert persisted.charter.source_sha == sha_b
    assert persisted.charter.vision == "V2"


async def test_sync_invalid_keeps_last_known_good():
    store = InMemoryCharterStore()
    ok = await sync_charter(
        store,
        RecordingRepoClient({CHARTER_PATH: (_charter_md(), "sha1")}),
        product="alpha",
        repo="org/alpha",
    )
    assert ok.status == CharterStatus.OK and ok.charter is not None
    invalid = await sync_charter(
        store,
        RecordingRepoClient({CHARTER_PATH: ("garbage, no marker", "sha2")}),
        product="alpha",
        repo="org/alpha",
    )
    assert invalid.status == CharterStatus.INVALID
    # Last-known-good content is preserved while status flips to INVALID.
    assert invalid.charter is not None and invalid.charter.vision == "V"


async def test_sync_charter_text_stores_with_source():
    store = InMemoryCharterStore()
    stored = await sync_charter_text(
        store,
        product="alpha",
        text=_charter_md(),
        source_sha="localsha",
        source_ref="file:.dsf/charter.md",
    )
    assert stored.status == CharterStatus.OK
    assert stored.charter.source_sha == "localsha"
    assert stored.charter.source_ref == "file:.dsf/charter.md"
