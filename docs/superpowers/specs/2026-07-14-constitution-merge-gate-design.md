# Design: `dsf charter implement` waits for the constitution PR to merge before filing the implementation issue

- Status: Draft
- Date: 2026-07-14
- Scope: laptop-driven `dsf charter implement` (creation kickoff). Builds on ADR 0017
  (the constitution is a derived projection of the human-owned charter; the charter is
  the single source of truth), ADR 0016 (Copilot Coding Agent is the executor; the DSF
  App files + assigns), ADR 0007 (council->creation handoff), ADR 0014 (real-only
  `src/`).

## Problem

`dsf charter implement --product X` renders the Spec Kit constitution into its own PR
and then, in the same breath, files the bootstrap issue and assigns the Copilot Coding
Agent:

```
_implement_async:
  sync charter
  open_file_pr(constitution, enable_auto_merge=True)   # separate PR
  create_issue(bootstrap, [HANDOFF_LABEL])             # assigns Copilot immediately
```

The implementation hand-off fires the instant the constitution PR is *opened*. Nothing
waits for it to land on `main`. Because the constitution governs the implementation
(ADR 0017), the Coding Agent can start building before its governing document exists on
`main`. The constitution should be **merged before implementation begins**.

The gap is sharper under branch protection. The creation-maturity dial
(`cli/src/dsf/instance/branch_protection.py`) makes:

- **`low`** — require 1 human approval **and** the green `ci` check; repo auto-merge
  **off**. The constitution PR's `enable_auto_merge=True` is a no-op here, so it never
  merges on its own; a human must approve + merge it.
- **`high`** — require 0 reviews, still the green `ci` check; repo auto-merge **on**.
  The constitution PR auto-merges once `ci` is green.

## Decision

Insert a **merge gate** between opening the constitution PR and filing the bootstrap
issue: `implement` polls `main` until the constitution is present there, then files the
issue. It never merges anything itself — the maturity dial already decides *who* merges
(`high` = GitHub auto-merge via the existing `enable_auto_merge=True`; `low` = a human).
On both dials `implement` simply waits.

### Merge signal: `main` carries the current constitution

The "merged" signal is **`main`'s constitution matching the charter revision**, not a
PR-merge API. The rendered constitution embeds a header comment:

```
<!-- dsf:constitution schema_version=1 source_ref=<ref> source_sha=<sha> -->
```

The gate polls `read_file(main, .specify/memory/constitution.md)` and compares that
header's `schema_version` + `source_ref` + `source_sha` to the charter being
implemented. This single check covers every case we care about:

- already current on `main` (idempotent resume, or a no-op re-render) -> proceed with no
  new PR;
- merged via this run's PR -> proceed;
- merged via a prior run's PR -> proceed.

It compares *identity*, not full text, so the constitution's daily-changing
`**Ratified**: <date>` footer does not cause false "stale" readings. It reuses
`read_file`, which already exists on both the real client
(`core/src/dsf/github_app_client.py:221`) and the test double
(`testing/dsf_testing/github.py`), so **no new GitHub API surface is added**.

Rejected alternatives: (B) a PR-merge API (`GET /pulls/{n}.merged`) — needs new
client+double methods, cannot distinguish merged from closed via the list API, and
misses the "already current" case; (C) polling `gh pr view` like the build watch — pulls
a user token into a step the App client already covers and does not handle "already
current."

## Part 1 — `core`: constitution currency check

Add one pure function next to the renderer that emits the header, in
`core/src/dsf/charter/constitution.py`:

```python
def is_constitution_current(existing_text: str | None, charter: Charter) -> bool:
    """True iff an existing constitution document already reflects ``charter``.

    Parses the ``<!-- dsf:constitution schema_version=.. source_ref=.. source_sha=.. -->``
    header and matches it against the charter's identity. Offline and pure.
    """
```

- Returns `False` for `None`/empty/malformed text or a missing header.
- Matches on `schema_version == _SCHEMA_VERSION` **and** `source_ref == (charter.source_ref or "unknown")`
  **and** `source_sha == (charter.source_sha or "unknown")` — the exact tokens
  `render_constitution` already writes.
- The `or "unknown"` mirrors the renderer so a charter that genuinely has an unknown
  ref/sha round-trips consistently.

Keeping this beside `render_constitution` keeps the header's format and its parser in
one place (change them together).

## Part 2 — `cli`: the merge gate in `_implement_async`

`cli/src/dsf/cli/charter.py`, `_implement_async` becomes:

1. **Sync charter** (unchanged). Refuse if not `OK` (unchanged).
2. **Render constitution** (unchanged).
3. **Reconcile the PR** (new `_ensure_constitution_pr`). To make reuse *correct* under
   a mid-flight charter amendment, scope the branch to the charter revision:
   `sha8 = (charter.source_sha or "unknown")[:8]`, reuse prefix
   `charter/constitution-<sha8>-`, and open on a fresh unique branch
   `charter/constitution-<sha8>-<uuid4hex8>` (always unique, so `open_file_pr`'s
   create-branch step is unchanged and never 422s).
   - `existing = await app.read_file(repo, CONSTITUTION_PATH, "main")`; if
     `is_constitution_current(existing.text, charter)` -> already on `main`; log
     `constitution already on main`; skip opening a PR (`pr_url = None`).
   - else `ref = await latest_pr_with_head_prefix(repo, head_prefix="charter/constitution-<sha8>-")`;
     if `ref` exists and `ref.state == "open"`, reuse it (`pr_url = ref.html_url`; log
     `reusing open constitution PR`) — do **not** open a duplicate. A stale PR for a
     *different* charter revision lives under a different `<sha8>` prefix, so it is
     never mistakenly reused (the operator can close it).
   - else `open_file_pr(...)` exactly as today (`enable_auto_merge=True`,
     `branch=charter/constitution-<sha8>-<uuid4hex8>`) -> `pr_url`.
4. **Wait for the merge gate** (new `_wait_for_constitution_on_main`), only when step 3
   was not already current:
   - Poll `read_file(repo, CONSTITUTION_PATH, "main")` until
     `is_constitution_current(...)` is `True` (merged) or the timeout elapses.
   - Async analog of `_watch_and_request_review`: injectable
     `sleep=asyncio.sleep`, `clock=time.monotonic`, `out=print`; timeout/cadence from
     the existing `_resolve_watch_timeout` / `_resolve_watch_poll_interval`
     (default 1800s / 20s; `0` = unbounded). Status printed only on change
     (`waiting for the constitution PR to merge...`).
   - Transient GitHub/network errors are logged and retried until the timeout (same
     resilience as the build watch); a `None`/malformed read is treated as "not yet."
   - Returns merged vs timed-out.
5. **Merged** -> file + assign the bootstrap issue (unchanged, including the
   `CodingAgentAssignmentError` -> `gh` fallback).
6. **Timeout** -> do **not** file the issue. Print the constitution PR URL and guidance
   (`approve + merge the constitution PR, then re-run \`dsf charter implement --product X\`;
   it resumes and skips the already-merged constitution`). Return a resumable rc.

### Return contract + `_cmd_charter_implement`

`_implement_async` currently returns `(rc, issue_url, assigned)`. The merge-gate timeout
is a new non-fatal, resumable outcome. Reuse the watch loop's convention: a timeout
returns `rc = 2` with `issue_url = None`, `assigned = False`.

`_cmd_charter_implement`:

- `rc == 2` (constitution not merged in time) -> return `2`, do not watch the build.
- `rc == 0` -> unchanged: unless `--no-wait`, watch the build and hand off to Copilot
  review.
- `--no-wait` is **unchanged in meaning** — it skips only the build watch. The
  constitution merge gate always runs (it is a precondition for filing the issue).

## Part 3 — test double: scripted `read_file` polls

`testing/dsf_testing/github.py` `RecordingRepoClient.read_file` returns a fixed value
today. To drive the poll loop deterministically (not current for N reads, then current),
add an **optional** scripted mode that leaves current behavior untouched:

- New optional ctor arg `read_file_sequence: dict[str, list[tuple[str, str] | None]]`
  mapping `path -> successive (text, sha) results` (or `None`). When present for a
  path, each `read_file` call pops the next entry (the last entry sticks once
  exhausted); otherwise it falls back to today's static `files` behavior.

This keeps existing tests untouched and lets a test seed "main becomes current on the
3rd poll."

## Error handling

- Charter not `OK` on `main` -> abort before touching the constitution (unchanged).
- Merge gate timeout -> resumable rc `2`, no issue filed, PR URL + guidance printed.
- Transient GitHub/network errors during the poll -> logged and retried until timeout.
- Re-runs are idempotent: an already-merged constitution short-circuits step 3; an
  already-open constitution PR is reused, never duplicated.

## Test approach (TDD)

**`core` (`core/tests/charter/test_constitution.py`):** unit-test
`is_constitution_current`:

- matching `schema_version`+`source_ref`+`source_sha` -> `True`;
- differing `source_sha` -> `False`; differing `schema_version` -> `False`;
- `None`/empty/headerless text -> `False`;
- two renders of the same charter on different dates (different `Ratified:` footer) ->
  both `True` (identity, not full text).

**`cli` (`cli/tests/cli/test_charter.py`):** with `RecordingRepoClient` + the in-memory
charter store, an injected fast `sleep`/`clock`:

- already current on `main` -> no PR opened, bootstrap issue filed;
- not current -> opens PR, `main` becomes current after N polls -> issue filed;
- timeout (never becomes current) -> no issue filed, rc `2`, guidance + PR URL printed,
  build watch not started;
- an open PR under the charter's `charter/constitution-<sha8>-` prefix is seeded ->
  reused, no duplicate `open_file_pr`; a seeded open PR under a *different* `<sha8>`
  (stale charter revision) is **not** reused -> a fresh PR is opened;
- `--no-wait` -> constitution gate still runs; on merge, issue filed and the build watch
  is skipped;
- update the existing `test_implement_*` cases so `main` is (or becomes) current, so
  they exercise the gate rather than the old fire-and-forget path.

## Scope / non-goals

- Laptop-only. The ACA runtime (`s7_filing`) and the `core` GitHub client's network
  surface are unchanged; no new GitHub API is added (the gate reuses `read_file`).
- `implement` never approves or merges the constitution PR itself. Merge authority stays
  with the maturity dial / branch protection (`high` auto-merge, `low` human approval).
- Paved-road customization of the constitution/bootstrap (`instance/bootstrap_issue.py`)
  remains separate, future scope.

## Verification

`uv run pytest core/tests cli/tests -q`, `uv run ruff check .`, `uv run lint-imports`
(expect "4 kept, 0 broken"). Live smoke on a real product (`high` auto-merge and `low`
human-merge paths) is manual.
