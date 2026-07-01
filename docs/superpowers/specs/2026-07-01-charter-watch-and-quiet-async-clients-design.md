# Design: `dsf charter implement` watches the build + hands off to Copilot review, and the CLI stops leaking async clients

- Status: Draft
- Date: 2026-07-01
- Scope: laptop-driven `dsf charter` CLI (creation kickoff). Builds on ADR 0007
  (council->creation handoff), ADR 0016 (Copilot Coding Agent is the executor; the
  DSF App files + assigns; advisory review is a DSF->GitHub action), ADR 0014
  (real-only `src/`).

## Problem

Two issues surfaced operating `dsf charter implement --product todo-app`:

1. **Noisy output.** Every charter command that touches Cosmos ends with a burst of
   aiohttp warnings on stderr:

   ```
   Unclosed client session
   client_session: <aiohttp.client.ClientSession object at 0x...>
   Unclosed connector
   connections: ['deque([...])']
   ```

2. **The handoff stops halfway.** `implement` files the bootstrap issue and assigns
   the Copilot Coding Agent, then returns. The agent builds and opens a PR, but
   nothing ever hands that PR to a reviewer, so the loop stalls waiting on a human to
   notice and request review.

## Part 1 -- Quiet the async clients

### Root cause

`core/src/dsf/memory/azure_store.py` `_SdkCosmosGateway._container()` lazily builds:

```python
self._client = CosmosClient(self._endpoint, credential=DefaultAzureCredential())
```

Both `azure.cosmos.aio.CosmosClient` and `azure.identity.aio.DefaultAzureCredential`
are aiohttp-backed and hold open `ClientSession`s. Nothing closes them, so when the
interpreter tears down the event loop aiohttp emits "Unclosed client session /
connector" for each. The CLI amplifies it: `_cmd_charter_implement` calls
`asyncio.run()` three times (sync, open PR, file issue), each a fresh loop, and the
gateway created in the first loop is never closed.

`GitHubAppClient` uses `httpx` with `async with` per call (no leak); App
Configuration is a synchronous gateway (no aiohttp). So the Cosmos gateway is the
sole source.

### Decision

Make the Cosmos gateway closeable, then close it deterministically in the CLI within
the loop that used it.

- **`core`:**
  - `_SdkCosmosGateway`: keep a handle to the credential
    (`self._cred = DefaultAzureCredential()`) and add
    `async def aclose(self)` that awaits `self._client.close()` and
    `self._cred.close()` when they exist (idempotent; safe before first use).
  - `CosmosGateway` protocol: add `async def aclose(self) -> None: ...`.
  - `CosmosCharterStore` and `CosmosMemoryStore`: add
    `async def aclose(self)` delegating to `self._gw.aclose()`.
  - `InMemoryCosmosGateway` (test double, `testing/dsf_testing/azure_doubles.py`):
    add a no-op `async def aclose(self)` so it still satisfies the protocol.
- **`cli` (`charter.py`):**
  - Add an async context manager:

    ```python
    @contextlib.asynccontextmanager
    async def _charter_store(product):
        store = build_charter_store(_settings(product))
        try:
            yield store
        finally:
            await store.aclose()
    ```

  - Rework `_cmd_charter_implement` into a single `async def` body run once via
    `asyncio.run(...)`, holding the store open across sync -> open PR -> file issue,
    and closing it in `finally`. This both closes the client and removes the
    multi-loop hazard.
  - Apply the same `_charter_store` wrapper to `sync` and `status`.

### Test approach (TDD)

- Unit: a fake gateway/store records that `aclose()` was awaited; assert each charter
  command awaits it exactly once, including on the error paths (invalid charter,
  missing repo). The existing `RecordingRepoClient` + in-memory charter store back
  these.
- `_SdkCosmosGateway.aclose()` is exercised with a stub client/credential exposing an
  awaitable `close()`, asserting both are awaited and that calling `aclose()` before
  any client is built is a no-op. (No live Azure; ADR 0014 injectable-seam style.)

## Part 2 -- Monitor the build + hand off to Copilot review

### Confirmed mechanics (live todo-app + gh 2.89.0)

- The Coding Agent PR has author login `copilot-swe-agent` (a Bot), head branch
  prefixed `copilot/`, and is opened as a **draft** with a `[WIP]` title while the
  agent works.
- The PR is linked to its issue: `issue.timelineItems(itemTypes:[CONNECTED_EVENT,
  CROSS_REFERENCED_EVENT])` yields the PR node (verified: issue #7 -> PR #8).
- **Done** = the linked PR's `isDraft` flips from `true` to `false`.
- **Review handoff** = `gh pr edit <pr-url> --add-reviewer "@copilot"`. gh 2.89.0
  documents `@copilot` as a first-class reviewer value; it runs under the operator's
  gh **user** token, sidestepping the App-installation-token restriction (the same
  wall the assignment step hit) with no `core` change.

### Behavior

- `dsf charter implement --product X [--no-wait] [--timeout SECONDS]`
  - Unchanged: sync charter, open constitution PR, file bootstrap issue, assign
    Copilot (App-first, gh fallback).
  - New default: after filing, run the watch+handoff over the just-filed issue.
  - `--no-wait`: skip watching; print a hint
    (`run \`dsf charter watch --product X\` to hand off to review once it's ready`).
- `dsf charter watch --product X [--issue N] [--timeout SECONDS]
  [--poll-interval SECONDS]`
  - Standalone, re-runnable, idempotent. Resolves the repo, finds the target issue
    (explicit `--issue`, else the newest open issue carrying `HANDOFF_LABEL`), then
    runs the same watch+handoff. Safe to re-run after a Ctrl-C or timeout.

### Watch algorithm

Given `repo` and `issue_number`:

1. **Locate the PR.** GraphQL over the issue timeline; select the linked PR whose
   author login is `copilot-swe-agent` (fallback: head starts with `copilot/`),
   preferring an OPEN one. If none yet, the agent has not opened its PR -- keep
   waiting (print `waiting for the coding agent to open its PR...`).
2. **Poll to ready.** Until the PR is non-draft, terminal, or timed out: sleep the
   poll interval; print a status line only on change
   (`todo-app#8 building (draft)...` -> `todo-app#8 ready for review`).
3. **Terminal PR.** If the PR is merged/closed before becoming a review target, print
   a note and stop (nothing to review).
4. **Idempotent handoff.** When ready, if `@copilot` is already among the PR's
   requested reviewers, print `Copilot review already requested` and stop; otherwise
   run `gh pr edit <pr-url> --add-reviewer "@copilot"` and print
   `requested Copilot review: <pr-url>`.
5. **Timeout.** Print a resumable message
   (`still building after Ns; re-run \`dsf charter watch --product X\` to resume`) and
   exit non-zero (`2`) so it is distinguishable from success.

### Cadence + configuration

Mirror `cli/src/dsf/instance/deploy_progress.py`: `--poll-interval` (floored at 1s),
env fallback `DSF_WATCH_POLL_INTERVAL` (default ~20s), and `--timeout` (default ~1800s
/ 30 min) with an unbounded option. `time.sleep` is injectable for tests.

### GitHub access

All GitHub calls in the watch path go through the operator's `gh` CLI, keeping the
feature CLI-local and `core` untouched (consistent with the assignment fix and the
laptop-only scope):

- Reads (issue -> PR, PR draft state, existing review requests): `gh api graphql`.
  `_gh_graphql` is extended to pass typed variables (`-F`) so an issue number can be
  an `Int!`.
- Write (request review): `gh pr edit ... --add-reviewer "@copilot"`.

A thin `_find_agent_pr(repo, issue)`, `_pr_state(repo, number)`, and
`_request_copilot_review(repo, pr_url)` sit alongside the existing
`_assign_copilot_via_gh` helper.

### Test approach (TDD)

Monkeypatch the `gh` seam (`_gh_graphql` and a `subprocess.run` for `gh pr edit`) and
an injected sleep. Cases:

- PR not present yet -> waits, then finds it once it appears.
- draft -> ready transition -> requests review exactly once.
- already ready at start -> requests immediately.
- review already requested -> no-op, no `gh pr edit` call.
- PR merged/closed before ready -> stops with a note, no review request.
- timeout -> resumable message + non-zero exit, no review request.
- `implement` default path calls watch with the filed issue; `--no-wait` skips it and
  prints the hint.
- `watch` resolves the newest `HANDOFF_LABEL` issue when `--issue` is omitted.

## Scope / non-goals

- Laptop-only. The ACA runtime (`s7_filing`) and `core` GitHub client are unchanged;
  the runtime keeps its existing soft-warning behavior.
- No DSF-authored LLM/council review here -- the reviewer is GitHub's native Copilot
  code review. A DSF advisory review remains future scope (ADR 0016).
- Merge gating stays with branch protection / the maturity dial; this design only
  requests a review, it does not approve or merge.

## Verification

`uv run pytest cli/tests -q`, `uv run ruff check .`, `uv run lint-imports`
(expect "4 kept, 0 broken"). Live smoke on todo-app is manual (real Copilot build).
