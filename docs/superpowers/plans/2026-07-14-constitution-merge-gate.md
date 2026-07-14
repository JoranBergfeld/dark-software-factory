# Constitution Merge Gate Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `dsf charter implement` wait for the constitution to land on `main` before it files and assigns the Copilot bootstrap issue, so the Coding Agent never starts building before its governing constitution exists (ADR 0017).

**Architecture:** Insert a *merge gate* into `_implement_async` between opening the constitution PR and filing the bootstrap issue. The "merged" signal is `main`'s constitution header (`schema_version`/`source_ref`/`source_sha`) matching the charter being implemented — read with the existing App `read_file`, so **no new GitHub API is added**. A pure `is_constitution_current` in `core` decides currency; two `cli` helpers (`_ensure_constitution_pr` reconcile + `_wait_for_constitution_on_main` poll) do the work, mirroring the existing `_watch_and_request_review` loop. `implement` never merges anything itself — the creation-maturity dial already decides who merges (`high` = GitHub auto-merge, `low` = a human). A timeout is resumable (`rc=2`, no issue filed).

**Tech Stack:** Python 3.12, `uv`, pytest (`asyncio_mode=auto`), ruff (E,F,I,UP,B, line length 100), import-linter. Real-only `src/` (ADR 0014); deterministic doubles live in `testing/dsf_testing/`.

**Spec:** `docs/superpowers/specs/2026-07-14-constitution-merge-gate-design.md`

---

## File Structure

| File | Change | Responsibility |
| --- | --- | --- |
| `core/src/dsf/charter/constitution.py` | modify | Add pure `is_constitution_current(existing_text, charter) -> bool` next to `render_constitution` (parser + header format stay together). |
| `core/tests/charter/test_constitution.py` | modify | Unit-test `is_constitution_current`. |
| `testing/dsf_testing/github.py` | modify | Add optional scripted `read_file_sequence` mode to `RecordingRepoClient` so a poll can be driven "not current for N reads, then current" without real sleeps. |
| `cli/src/dsf/cli/charter.py` | modify | Add `_ensure_constitution_pr` (reconcile) + `_wait_for_constitution_on_main` (poll); wire both into `_implement_async`; return resumable `rc=2` on timeout. |
| `cli/tests/cli/test_charter.py` | modify | Unit-test both helpers + the scripted double; update existing `implement` tests so `main` becomes current; add already-current / reuse / stale-revision / timeout / `--no-wait` end-to-end cases. |

**Task order (dependencies):** Task 1 (core currency) → Task 2 (double) → Task 3 (`_ensure_constitution_pr`) → Task 4 (`_wait_for_constitution_on_main`) → Task 5 (wire + end-to-end) → Task 6 (full verification).

**Every commit uses Conventional Commits and appends these trailers** (blank line before them):

```
Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: c1ac6cfe-2001-4376-8b9e-4efc8389dc40
```

---

## Task 1: `core` — `is_constitution_current`

**Files:**
- Modify: `core/src/dsf/charter/constitution.py`
- Test: `core/tests/charter/test_constitution.py`

- [ ] **Step 1: Write the failing tests**

Add these imports and tests to `core/tests/charter/test_constitution.py`. Change the existing top import line
`from dsf.charter.constitution import CONSTITUTION_PATH, render_constitution`
to:

```python
from dsf.charter.constitution import (
    CONSTITUTION_PATH,
    is_constitution_current,
    render_constitution,
)
```

Append at the end of the file:

```python
def test_is_current_true_for_freshly_rendered():
    charter = _charter()
    assert is_constitution_current(render_constitution(charter), charter) is True


def test_is_current_ignores_ratified_footer_date():
    charter = _charter()
    early = render_constitution(charter, today=date(2026, 1, 1))
    later = render_constitution(charter, today=date(2026, 6, 30))
    assert is_constitution_current(early, charter) is True
    assert is_constitution_current(later, charter) is True


def test_is_current_false_on_sha_mismatch():
    on_main = render_constitution(_charter(source_sha="oldsha"))
    assert is_constitution_current(on_main, _charter(source_sha="newsha")) is False


def test_is_current_false_on_schema_mismatch():
    charter = _charter()
    text = render_constitution(charter).replace("schema_version=1", "schema_version=2")
    assert is_constitution_current(text, charter) is False


def test_is_current_false_on_ref_mismatch():
    on_main = render_constitution(_charter(source_ref="main"))
    assert is_constitution_current(on_main, _charter(source_ref="release")) is False


def test_is_current_false_for_none_empty_or_headerless():
    charter = _charter()
    assert is_constitution_current(None, charter) is False
    assert is_constitution_current("", charter) is False
    assert is_constitution_current("# just a doc, no marker", charter) is False


def test_is_current_true_when_ref_and_sha_unknown_roundtrip():
    charter = _charter(source_sha=None, source_ref=None)
    assert is_constitution_current(render_constitution(charter), charter) is True
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest core/tests/charter/test_constitution.py -q`
Expected: FAIL — `ImportError: cannot import name 'is_constitution_current'`.

- [ ] **Step 3: Implement `is_constitution_current`**

In `core/src/dsf/charter/constitution.py`, change the import block at the top from:

```python
from __future__ import annotations

from datetime import UTC, date, datetime

from dsf.contracts.charter import Charter
```

to:

```python
from __future__ import annotations

import re
from datetime import UTC, date, datetime

from dsf.contracts.charter import Charter
```

Add this module-level constant directly below the existing `_NONE_DECLARED = "- (none declared in the charter)"` line:

```python
_MARKER_RE = re.compile(
    r"<!--\s*dsf:constitution\s+"
    r"schema_version=(?P<schema_version>\S+)\s+"
    r"source_ref=(?P<source_ref>\S+)\s+"
    r"source_sha=(?P<source_sha>\S+)\s*-->"
)
```

Add this function directly above the final `__all__` line:

```python
def is_constitution_current(existing_text: str | None, charter: Charter) -> bool:
    """True iff ``existing_text`` already reflects ``charter``'s identity.

    Parses the ``<!-- dsf:constitution schema_version=.. source_ref=.. source_sha=.. -->``
    header that :func:`render_constitution` writes and matches it against the
    charter's schema version, source ref and source sha. Pure and offline.
    Returns ``False`` for ``None``/empty/headerless text so a missing or foreign
    document never reads as current. Compares identity only, so the dated
    ``**Ratified**`` footer never causes a false "stale" reading.
    """
    if not existing_text:
        return False
    marker = _MARKER_RE.search(existing_text)
    if marker is None:
        return False
    return (
        marker.group("schema_version") == str(_SCHEMA_VERSION)
        and marker.group("source_ref") == (charter.source_ref or "unknown")
        and marker.group("source_sha") == (charter.source_sha or "unknown")
    )
```

Change the final line from:

```python
__all__ = ["CONSTITUTION_PATH", "render_constitution"]
```

to:

```python
__all__ = ["CONSTITUTION_PATH", "is_constitution_current", "render_constitution"]
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `uv run pytest core/tests/charter/test_constitution.py -q`
Expected: PASS (all existing + 7 new tests green).

- [ ] **Step 5: Lint**

Run: `uv run ruff check core/src/dsf/charter/constitution.py core/tests/charter/test_constitution.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add core/src/dsf/charter/constitution.py core/tests/charter/test_constitution.py
git commit -F - <<'EOF'
feat: add is_constitution_current currency check

Pure, offline check that parses the constitution's dsf:constitution header
(schema_version/source_ref/source_sha) and matches it against a charter's
identity. Compares identity only so the dated Ratified footer never reads as
stale. Backs the upcoming `dsf charter implement` merge gate; reuses the header
render_constitution already emits, so no new GitHub API is needed.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: c1ac6cfe-2001-4376-8b9e-4efc8389dc40
EOF
```

---

## Task 2: test double — scripted `read_file` sequence

**Files:**
- Modify: `testing/dsf_testing/github.py`
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing test**

In `cli/tests/cli/test_charter.py`, add this import near the other `dsf.charter` imports at the top (after `from dsf.charter.sync import CHARTER_PATH`):

```python
from dsf.charter.constitution import CONSTITUTION_PATH, render_constitution
```

Append this test at the end of the file:

```python
def test_recording_repo_client_scripts_read_file_sequence():
    client = RecordingRepoClient(
        read_file_sequence={CONSTITUTION_PATH: [None, ("first", "s1"), ("last", "s2")]}
    )
    # pops through the sequence in order, then the final entry sticks
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)) is None
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)).text == "first"
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)).text == "last"
    assert asyncio.run(client.read_file("r", CONSTITUTION_PATH)).text == "last"
    # a path without a scripted sequence still falls back to static files
    assert asyncio.run(client.read_file("r", "absent")) is None
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest cli/tests/cli/test_charter.py::test_recording_repo_client_scripts_read_file_sequence -q`
Expected: FAIL — `TypeError: __init__() got an unexpected keyword argument 'read_file_sequence'`.

- [ ] **Step 3: Implement the scripted mode**

In `testing/dsf_testing/github.py`, replace the `RecordingRepoClient.__init__` method:

```python
    def __init__(
        self,
        files: dict[str, tuple[str, str]] | None = None,
        *,
        repositories: list[str] | None = None,
        prs: list[_AmendmentPr] | None = None,
        create_issue_error: Exception | None = None,
    ) -> None:
        self._files = dict(files or {})
        self.repositories = repositories
        self._seed_prs = list(prs or [])
        self.prs: list[dict] = []
        self.issues: list[dict] = []
        self._create_issue_error = create_issue_error
```

with:

```python
    def __init__(
        self,
        files: dict[str, tuple[str, str]] | None = None,
        *,
        repositories: list[str] | None = None,
        prs: list[_AmendmentPr] | None = None,
        create_issue_error: Exception | None = None,
        read_file_sequence: dict[str, list[tuple[str, str] | None]] | None = None,
    ) -> None:
        self._files = dict(files or {})
        self.repositories = repositories
        self._seed_prs = list(prs or [])
        self.prs: list[dict] = []
        self.issues: list[dict] = []
        self._create_issue_error = create_issue_error
        # Optional per-path scripted results: each ``read_file`` for a scripted
        # path returns the next entry (``None`` = absent); the last entry sticks
        # once the sequence is exhausted. Lets a poll be driven deterministically.
        self._read_seq = {
            path: list(seq) for path, seq in (read_file_sequence or {}).items()
        }
```

Then replace the `read_file` method:

```python
    async def read_file(self, repo: str, path: str, ref: str = "main") -> _RepoFile | None:
        if path not in self._files:
            return None
        text, sha = self._files[path]
        return _RepoFile(text=text, sha=sha, ref=ref)
```

with:

```python
    async def read_file(self, repo: str, path: str, ref: str = "main") -> _RepoFile | None:
        seq = self._read_seq.get(path)
        if seq:
            entry = seq[0] if len(seq) == 1 else seq.pop(0)
            if entry is None:
                return None
            text, sha = entry
            return _RepoFile(text=text, sha=sha, ref=ref)
        if path not in self._files:
            return None
        text, sha = self._files[path]
        return _RepoFile(text=text, sha=sha, ref=ref)
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest cli/tests/cli/test_charter.py::test_recording_repo_client_scripts_read_file_sequence -q`
Expected: PASS.

- [ ] **Step 5: Lint**

Run: `uv run ruff check testing/dsf_testing/github.py cli/tests/cli/test_charter.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add testing/dsf_testing/github.py cli/tests/cli/test_charter.py
git commit -F - <<'EOF'
test: add scripted read_file sequence to RecordingRepoClient

Optional read_file_sequence maps a path to successive (text, sha) results (or
None), popping per call with the last entry sticking. Lets a poll loop be
driven "not current for N reads, then current" without real sleeps. Existing
static-files behavior is untouched when no sequence is given.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: c1ac6cfe-2001-4376-8b9e-4efc8389dc40
EOF
```

---

## Task 3: `cli` — `_ensure_constitution_pr` (reconcile)

**Files:**
- Modify: `cli/src/dsf/cli/charter.py`
- Test: `cli/tests/cli/test_charter.py`

Reconcile the constitution PR before waiting: skip if `main` is already current, reuse an open PR for the *same* charter revision, else open a fresh one. The branch is scoped to the charter revision (`charter/constitution-<sha8>-<uuid>`) so a stale-revision PR is never mistakenly reused.

- [ ] **Step 1: Write the failing tests**

Append to `cli/tests/cli/test_charter.py`:

```python
def test_ensure_constitution_pr_skips_when_already_current():
    ch = _ok_charter("blobsha")
    client = RecordingRepoClient({CONSTITUTION_PATH: (render_constitution(ch), "csha")})
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is True and pr_url is None and client.prs == []


def test_ensure_constitution_pr_reuses_open_same_revision_pr():
    from datetime import UTC, datetime

    from dsf_testing.github import SeedPr

    ch = _ok_charter("blobsha")  # sha8 == "blobsha"
    client = RecordingRepoClient(
        prs=[
            SeedPr(
                html_url="https://github.com/org/alpha/pull/7",
                state="open",
                created_at=datetime(2026, 7, 14, tzinfo=UTC),
                head_ref="charter/constitution-blobsha-deadbeef",
            )
        ]
    )
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is False
    assert pr_url == "https://github.com/org/alpha/pull/7"
    assert client.prs == []  # reused; no new PR opened


def test_ensure_constitution_pr_ignores_stale_revision_pr():
    from datetime import UTC, datetime

    from dsf_testing.github import SeedPr

    ch = _ok_charter("blobsha")  # sha8 == "blobsha"
    client = RecordingRepoClient(
        prs=[
            SeedPr(
                html_url="https://github.com/org/alpha/pull/3",
                state="open",
                created_at=datetime(2026, 7, 13, tzinfo=UTC),
                head_ref="charter/constitution-oldsha00-deadbeef",  # different sha8
            )
        ]
    )
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is False
    assert len(client.prs) == 1
    assert client.prs[0]["branch"].startswith("charter/constitution-blobsha-")
    assert client.prs[0]["enable_auto_merge"] is True


def test_ensure_constitution_pr_opens_when_none_exists():
    ch = _ok_charter("blobsha")
    client = RecordingRepoClient({})
    pr_url, already = asyncio.run(
        charter._ensure_constitution_pr(client, "org/alpha", "alpha", ch, render_constitution(ch))
    )
    assert already is False
    assert len(client.prs) == 1
    assert client.prs[0]["path"] == CONSTITUTION_PATH
    assert pr_url.endswith("/pull/1")
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k ensure_constitution_pr -q`
Expected: FAIL — `AttributeError: module 'dsf.cli.charter' has no attribute '_ensure_constitution_pr'`.

- [ ] **Step 3: Implement `_ensure_constitution_pr`**

In `cli/src/dsf/cli/charter.py`, add this import at the top, changing:

```python
from dsf.charter.constitution import CONSTITUTION_PATH, render_constitution
```

to:

```python
from dsf.charter.constitution import (
    CONSTITUTION_PATH,
    is_constitution_current,
    render_constitution,
)
```

Add this function immediately above `async def _implement_async(` (around line 681):

```python
async def _ensure_constitution_pr(
    app, repo: str, product: str, charter: Charter, constitution: str
) -> tuple[str | None, bool]:
    """Reconcile the constitution PR; return ``(pr_url, already_on_main)``.

    - ``main`` already carries this charter's constitution -> ``(None, True)``:
      nothing to open, and the caller skips the merge wait.
    - an **open** PR for the *same* charter revision exists -> reuse it
      ``(pr_url, False)`` (never open a duplicate).
    - otherwise open a fresh PR ``(pr_url, False)``.

    The branch is scoped to the charter revision
    (``charter/constitution-<sha8>-<uuid>``) so a PR for a *different* revision
    lives under a different prefix and is never mistakenly reused.
    """
    sha8 = (charter.source_sha or "unknown")[:8]
    head_prefix = f"charter/constitution-{sha8}-"

    existing = await app.read_file(repo, CONSTITUTION_PATH, "main")
    existing_text = existing.text if existing is not None else None
    if is_constitution_current(existing_text, charter):
        print(f"[dsf] constitution already on main for {product}; skipping PR")
        return None, True

    ref = await app.latest_pr_with_head_prefix(repo, head_prefix=head_prefix)
    if ref is not None and ref.state == "open":
        print(f"[dsf] reusing open constitution PR: {ref.html_url}")
        return ref.html_url, False

    branch = f"{head_prefix}{uuid.uuid4().hex[:8]}"
    pr_url = await app.open_file_pr(
        repo,
        path=CONSTITUTION_PATH,
        content=constitution,
        branch=branch,
        title=f"Add Spec Kit constitution for {product}",
        body=(
            "Constitution derived from the product charter by "
            "`dsf charter implement`. Auto-merge is requested: on repos where "
            "it is enabled this merges once the `ci` check is green, otherwise "
            "it awaits a human review. (Creation-maturity gating is future "
            "scope.)"
        ),
        message=f"docs: add spec kit constitution for {product}",
        enable_auto_merge=True,
    )
    print(f"[dsf] opened constitution PR (auto-merge requested): {pr_url}")
    return pr_url, False
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k ensure_constitution_pr -q`
Expected: PASS (4 tests).

- [ ] **Step 5: Lint**

Run: `uv run ruff check cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -F - <<'EOF'
feat: reconcile the constitution PR before implementing

Add _ensure_constitution_pr: skip when main already carries the current
constitution, reuse an open PR for the same charter revision, else open a fresh
one. Branch is scoped to the charter revision (charter/constitution-<sha8>-<uuid>)
so a stale-revision PR is never mistakenly reused. Not yet wired into
_implement_async.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: c1ac6cfe-2001-4376-8b9e-4efc8389dc40
EOF
```

---

## Task 4: `cli` — `_wait_for_constitution_on_main` (poll)

**Files:**
- Modify: `cli/src/dsf/cli/charter.py`
- Test: `cli/tests/cli/test_charter.py`

Poll `main` until it carries the current constitution (merged) or the timeout elapses. Async analog of `_watch_and_request_review`: injectable `sleep`/`clock`/`out`, transient errors retried until timeout, status printed only on change.

- [ ] **Step 1: Write the failing tests**

Append to `cli/tests/cli/test_charter.py`:

```python
def _fake_async_sleep(record):
    async def _sleep(seconds):
        record.append(seconds)

    return _sleep


def test_wait_returns_true_when_main_becomes_current():
    ch = _ok_charter("blobsha")
    current = render_constitution(ch)
    client = RecordingRepoClient(
        read_file_sequence={CONSTITUTION_PATH: [None, None, (current, "s")]}
    )
    slept: list[float] = []
    merged = asyncio.run(
        charter._wait_for_constitution_on_main(
            client,
            "org/alpha",
            ch,
            timeout=None,
            poll_interval=5,
            sleep=_fake_async_sleep(slept),
            clock=lambda: 0.0,
        )
    )
    assert merged is True
    assert len(slept) == 2  # two "not yet" polls before it went current


def test_wait_returns_false_on_timeout():
    ch = _ok_charter("blobsha")
    client = RecordingRepoClient(read_file_sequence={CONSTITUTION_PATH: [None]})
    ticks = iter([0.0, 0.0, 10.0])  # start, guard#1 (< timeout), guard#2 (>= timeout)
    merged = asyncio.run(
        charter._wait_for_constitution_on_main(
            client,
            "org/alpha",
            ch,
            timeout=5,
            poll_interval=1,
            sleep=_fake_async_sleep([]),
            clock=lambda: next(ticks),
        )
    )
    assert merged is False


def test_wait_retries_transient_errors_until_current():
    from types import SimpleNamespace

    ch = _ok_charter("blobsha")
    current = render_constitution(ch)

    class _Flaky:
        def __init__(self) -> None:
            self.calls = 0

        async def read_file(self, repo, path, ref="main"):
            self.calls += 1
            if self.calls == 1:
                raise RuntimeError("transient blip")
            return SimpleNamespace(text=current, sha="s", ref=ref)

    app = _Flaky()
    slept: list[float] = []
    merged = asyncio.run(
        charter._wait_for_constitution_on_main(
            app,
            "org/alpha",
            ch,
            timeout=None,
            poll_interval=1,
            sleep=_fake_async_sleep(slept),
            clock=lambda: 0.0,
        )
    )
    assert merged is True
    assert app.calls == 2 and len(slept) == 1
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "wait_returns or wait_retries" -q`
Expected: FAIL — `AttributeError: module 'dsf.cli.charter' has no attribute '_wait_for_constitution_on_main'`.

- [ ] **Step 3: Implement `_wait_for_constitution_on_main`**

In `cli/src/dsf/cli/charter.py`, add this function immediately above `_ensure_constitution_pr` (both live just above `_implement_async`):

```python
async def _wait_for_constitution_on_main(
    app,
    repo: str,
    charter: Charter,
    *,
    timeout: float | None,
    poll_interval: float,
    sleep=asyncio.sleep,
    clock=time.monotonic,
    out=print,
) -> bool:
    """Poll ``main`` until it carries ``charter``'s constitution; return merged?

    Returns ``True`` once ``main``'s constitution matches ``charter`` (merged) or
    ``False`` if ``timeout`` elapses first (resumable). Async analog of
    :func:`_watch_and_request_review`: transient GitHub/network errors are logged
    and retried until the timeout so a single blip does not abort a long wait, and
    a missing/malformed read is treated as "not yet". ``sleep``/``clock``/``out``
    are injectable so tests drive the loop without real waiting.
    """
    import httpx

    transient = (httpx.HTTPError, OSError, RuntimeError, KeyError, TypeError)
    start = clock()
    last_status = ""

    def _emit(status: str) -> None:
        nonlocal last_status
        if status != last_status:
            out(f"[dsf] {status}")
            last_status = status

    while True:
        try:
            existing = await app.read_file(repo, CONSTITUTION_PATH, "main")
            text = existing.text if existing is not None else None
            if is_constitution_current(text, charter):
                out(f"[dsf] constitution merged to main: {repo}")
                return True
            _emit("waiting for the constitution PR to merge...")
        except transient as exc:
            _emit(f"transient GitHub error ({exc.__class__.__name__}); retrying...")

        if timeout is not None and clock() - start >= timeout:
            return False
        await sleep(poll_interval)
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "wait_returns or wait_retries" -q`
Expected: PASS (3 tests).

- [ ] **Step 5: Lint**

Run: `uv run ruff check cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -F - <<'EOF'
feat: poll for the constitution to reach main

Add _wait_for_constitution_on_main: an async analog of the build watch that
polls main's constitution (via read_file) until is_constitution_current is true
(merged) or the timeout elapses (resumable). Transient httpx/network errors are
retried until timeout; sleep/clock/out are injectable for deterministic tests.
Not yet wired into _implement_async.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: c1ac6cfe-2001-4376-8b9e-4efc8389dc40
EOF
```

---

## Task 5: `cli` — wire the gate into `_implement_async` + update tests

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (`_implement_async`, `--timeout` help)
- Test: `cli/tests/cli/test_charter.py`

> `_cmd_charter_implement` needs **no change**: its existing guard
> `if rc != 0 or not assigned or issue_url is None: return rc` already returns the
> resumable `rc=2` from a merge-gate timeout *before* it would start the build
> watch (verified by `test_implement_aborts_when_constitution_not_merged_in_time`).

In `cli/tests/cli/test_charter.py`:

**(a)** Replace the `_seed_ok_implement` helper:

```python
def _seed_ok_implement(monkeypatch, *, create_issue_error=None):
    """Wire an OK, non-drifted charter + an App double for `charter implement`."""
    charter = _ok_charter("blobsha")
    store = InMemoryCharterStore()
    _put(store, charter, CharterStatus.OK)
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(charter), "blobsha")},
        create_issue_error=create_issue_error,
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    return client
```

with (adds the scripted constitution: absent at reconcile -> opens PR, then current on `main` -> merged on the first poll):

```python
def _seed_ok_implement(monkeypatch, *, create_issue_error=None):
    """Wire an OK, non-drifted charter + an App double for `charter implement`.

    The constitution is absent at reconcile (so a PR is opened) and current on
    `main` on the first poll (so the merge gate passes immediately).
    """
    charter = _ok_charter("blobsha")
    store = InMemoryCharterStore()
    _put(store, charter, CharterStatus.OK)
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(charter), "blobsha")},
        create_issue_error=create_issue_error,
        read_file_sequence={
            CONSTITUTION_PATH: [None, (render_constitution(charter), "csha")]
        },
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    return client
```

**(b)** In `test_implement_closes_the_store`, replace the `client = RecordingRepoClient(...)` construction:

```python
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(_ok_charter("blobsha")), "blobsha")}
    )
```

with:

```python
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(_ok_charter("blobsha")), "blobsha")},
        read_file_sequence={
            CONSTITUTION_PATH: [None, (render_constitution(_ok_charter("blobsha")), "csha")]
        },
    )
```

**(c)** In `test_implement_syncs_stale_charter_then_proceeds`, replace:

```python
    client = RecordingRepoClient({CHARTER_PATH: (render_charter(_ok_charter("newsha")), "newsha")})
```

with (the charter synced from `main` has `source_sha="newsha"`, so the current constitution must be rendered from that revision):

```python
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(_ok_charter("newsha")), "newsha")},
        read_file_sequence={
            CONSTITUTION_PATH: [None, (render_constitution(_ok_charter("newsha")), "csha")]
        },
    )
```

**(d)** Replace `test_implement_no_wait_skips_watch` to also prove the gate ran:

```python
def test_implement_no_wait_skips_watch(monkeypatch, capsys):
    client = _seed_ok_implement(monkeypatch)
    called = {"n": 0}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda *a, **k: called.__setitem__("n", called["n"] + 1) or 0,
    )
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    out = capsys.readouterr().out
    assert rc == 0 and called["n"] == 0  # build watch skipped
    assert len(client.prs) == 1 and len(client.issues) == 1  # gate still ran
    assert "dsf charter watch" in out
```

**(e)** Append four new end-to-end cases:

```python
def test_implement_skips_pr_when_constitution_already_on_main(monkeypatch, capsys):
    ch = _ok_charter("blobsha")
    store = InMemoryCharterStore()
    _put(store, ch, CharterStatus.OK)
    client = RecordingRepoClient(
        {
            CHARTER_PATH: (render_charter(ch), "blobsha"),
            CONSTITUTION_PATH: (render_constitution(ch), "csha"),
        }
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    out = capsys.readouterr().out
    assert rc == 0
    assert client.prs == []  # already on main -> no PR opened
    assert len(client.issues) == 1  # bootstrap issue still filed
    assert "already on main" in out


def test_implement_reuses_open_constitution_pr(monkeypatch, capsys):
    from datetime import UTC, datetime

    from dsf_testing.github import SeedPr

    ch = _ok_charter("blobsha")  # sha8 == "blobsha"
    store = InMemoryCharterStore()
    _put(store, ch, CharterStatus.OK)
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(ch), "blobsha")},
        prs=[
            SeedPr(
                html_url="https://github.com/org/alpha/pull/7",
                state="open",
                created_at=datetime(2026, 7, 14, tzinfo=UTC),
                head_ref="charter/constitution-blobsha-deadbeef",
            )
        ],
        read_file_sequence={
            CONSTITUTION_PATH: [None, (render_constitution(ch), "csha")]
        },
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    out = capsys.readouterr().out
    assert rc == 0
    assert client.prs == []  # reused the seeded open PR; no new open_file_pr
    assert len(client.issues) == 1
    assert "reusing open constitution PR" in out


def test_implement_opens_new_pr_when_only_stale_revision_pr_exists(monkeypatch, capsys):
    from datetime import UTC, datetime

    from dsf_testing.github import SeedPr

    ch = _ok_charter("blobsha")  # sha8 == "blobsha"
    store = InMemoryCharterStore()
    _put(store, ch, CharterStatus.OK)
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(ch), "blobsha")},
        prs=[
            SeedPr(
                html_url="https://github.com/org/alpha/pull/3",
                state="open",
                created_at=datetime(2026, 7, 13, tzinfo=UTC),
                head_ref="charter/constitution-oldsha00-deadbeef",  # different sha8
            )
        ],
        read_file_sequence={
            CONSTITUTION_PATH: [None, (render_constitution(ch), "csha")]
        },
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    assert rc == 0
    assert len(client.prs) == 1  # stale-revision PR ignored -> fresh PR opened
    assert client.prs[0]["branch"].startswith("charter/constitution-blobsha-")
    assert len(client.issues) == 1


def test_implement_aborts_when_constitution_not_merged_in_time(monkeypatch, capsys):
    client = _seed_ok_implement(monkeypatch)

    async def _never_merges(app, repo, charter, **kw):
        return False

    monkeypatch.setattr("dsf.cli.charter._wait_for_constitution_on_main", _never_merges)
    watched = {"n": 0}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda *a, **k: watched.__setitem__("n", watched["n"] + 1) or 0,
    )
    rc = main(["charter", "implement", "--product", "alpha"])
    captured = capsys.readouterr()
    assert rc == 2
    assert "has not merged" in captured.err
    assert client.issues == []  # no bootstrap issue filed on timeout
    assert watched["n"] == 0  # build watch not started
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k implement -q`
Expected: FAIL — the new cases fail (no gate wired yet) and the updated existing cases fail because `_implement_async` still files the issue without waiting / does not print "already on main" / "reusing open constitution PR".

- [ ] **Step 3: Wire the gate into `_implement_async`**

In `cli/src/dsf/cli/charter.py`, replace the body of `_implement_async` from `charter = stored.charter` down to (but **not** including) the `title, body = render_bootstrap_issue(charter)` line. Concretely, replace:

```python
        charter = stored.charter
        constitution = render_constitution(charter)
        branch = f"charter/constitution-{uuid.uuid4().hex[:8]}"
        pr_url = await app.open_file_pr(
            repo_full,
            path=CONSTITUTION_PATH,
            content=constitution,
            branch=branch,
            title=f"Add Spec Kit constitution for {product}",
            body=(
                "Constitution derived from the product charter by "
                "`dsf charter implement`. Auto-merge is requested: on repos where "
                "it is enabled this merges once the `ci` check is green, otherwise "
                "it awaits a human review. (Creation-maturity gating is future "
                "scope.)"
            ),
            message=f"docs: add spec kit constitution for {product}",
            enable_auto_merge=True,
        )
        print(f"[dsf] opened constitution PR (auto-merge requested): {pr_url}")

        title, body = render_bootstrap_issue(charter)
```

with:

```python
        charter = stored.charter
        constitution = render_constitution(charter)
        pr_url, already_current = await _ensure_constitution_pr(
            app, repo_full, product, charter, constitution
        )
        if not already_current:
            merged = await _wait_for_constitution_on_main(
                app,
                repo_full,
                charter,
                timeout=_resolve_watch_timeout(args.timeout),
                poll_interval=_resolve_watch_poll_interval(args.poll_interval),
            )
            if not merged:
                print(
                    "[dsf] error: the constitution PR has not merged within the "
                    "timeout; not filing the bootstrap issue.",
                    file=sys.stderr,
                )
                print(
                    f"[dsf]   approve + merge the constitution PR ({pr_url}) then "
                    f"re-run `dsf charter implement --product {product}`; it resumes "
                    "and skips the already-merged constitution.",
                    file=sys.stderr,
                )
                return 2, None, False

        title, body = render_bootstrap_issue(charter)
```

- [ ] **Step 4: Update the `_implement_async` docstring**

Replace the existing docstring of `_implement_async`:

```python
    """Sync charter, open the constitution PR, file+assign the bootstrap issue.

    Returns ``(rc, issue_url, assigned)``: ``rc`` non-zero on a hard failure,
    ``issue_url`` of the filed bootstrap issue (or ``None``), and whether the
    Copilot coding agent was successfully assigned (so the caller can decide
    whether it is worth watching the build).
    """
```

with:

```python
    """Sync charter, land the constitution on ``main``, then file the bootstrap issue.

    Reconciles the constitution PR (``_ensure_constitution_pr``) and, unless
    ``main`` already carries it, waits for it to merge
    (``_wait_for_constitution_on_main``) before filing + assigning the bootstrap
    issue -- the constitution governs the implementation (ADR 0017), so it must be
    on ``main`` first. Returns ``(rc, issue_url, assigned)``: ``rc`` is ``1`` on a
    hard failure, ``2`` when the constitution does not merge within the timeout
    (resumable: no issue is filed), and ``0`` otherwise. ``issue_url`` is the filed
    bootstrap issue (or ``None``) and ``assigned`` reports whether the Copilot
    coding agent was assigned (so the caller can decide whether to watch the build).
    """
```

- [ ] **Step 5: Update the `--timeout` help text**

The merge gate reuses the same `--timeout`/`--poll-interval` as the build watch. In `add_charter_subcommands`, replace the implement parser's `--timeout` argument:

```python
    implement_parser.add_argument(
        "--timeout",
        type=float,
        default=None,
        help="max seconds to watch the build (default 1800; 0 = unbounded)",
    )
```

with:

```python
    implement_parser.add_argument(
        "--timeout",
        type=float,
        default=None,
        help=(
            "max seconds to wait for the constitution to merge and then to watch "
            "the build (default 1800; 0 = unbounded)"
        ),
    )
```

- [ ] **Step 6: Run the implement tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k implement -q`
Expected: PASS (all updated + new implement cases green).

- [ ] **Step 7: Run the whole charter test module**

Run: `uv run pytest cli/tests/cli/test_charter.py -q`
Expected: PASS.

- [ ] **Step 8: Lint**

Run: `uv run ruff check cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py`
Expected: `All checks passed!`

- [ ] **Step 9: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -F - <<'EOF'
feat: gate charter implement on the constitution reaching main

_implement_async now reconciles the constitution PR and, unless main already
carries it, waits for it to merge before filing + assigning the bootstrap issue
(the constitution governs the implementation, ADR 0017). A timeout returns a
resumable rc=2 with no issue filed and prints the PR URL + guidance; --no-wait
still runs the gate and only skips the build watch. Reuses the existing
--timeout/--poll-interval and updates their help.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
Copilot-Session: c1ac6cfe-2001-4376-8b9e-4efc8389dc40
EOF
```

---

## Task 6: Full verification

**Files:** none (verification only).

- [ ] **Step 1: Run the full core + cli test suites**

Run: `uv run pytest core/tests cli/tests -q`
Expected: PASS (no failures; the new tests included).

- [ ] **Step 2: Lint the whole tree**

Run: `uv run ruff check .`
Expected: `All checks passed!`

- [ ] **Step 3: Check import boundaries**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken.`

- [ ] **Step 4: Confirm no stray changes**

Run: `git status --short`
Expected: clean working tree (all changes committed across Tasks 1-5).

---

## Notes for the implementer

- **`uv` only** — never call bare `python`/`pip`/`pytest`.
- **`sha8`** is `(charter.source_sha or "unknown")[:8]`; for the test charter `_ok_charter("blobsha")` that is the 7-char string `"blobsha"`, so the reuse prefix is `charter/constitution-blobsha-`. This is why the reuse test seeds a head ref under that prefix and the stale test uses `oldsha00` (a *different* `sha8`).
- **Why `main` becoming current is the merge signal:** whoever merges the PR (GitHub auto-merge on `high`, a human on `low`) updates `main`; the poll reads `main` and matches the header — it does not call any PR-merge API, and it also covers the "already merged in a prior run" resume case.
- **`is_constitution_current` compares identity, not full text**, so the daily `**Ratified**: <date>` footer churn never reads as stale.
- The App client (`read_file`) surfaces transient transport faults as `httpx.HTTPError`/`OSError`; those are caught and retried until the timeout. The poll never merges anything — merge authority stays with branch protection.
