# Charter Watch + Quiet Async Clients Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop `dsf charter` commands from leaking aiohttp sessions, and make `dsf charter implement` monitor the Copilot coding agent's PR and hand it off to Copilot code review when it's ready (plus a re-runnable `dsf charter watch`).

**Architecture:** Part 1 makes the async Cosmos client + credential closeable in `core` and closes them from the CLI within the event loop. Part 2 adds a `gh`-CLI-based watch loop in `cli/charter.py` that finds the coding agent's linked PR, waits for it to leave draft, then requests review via `gh pr edit --add-reviewer "@copilot"`. `core` and the ACA runtime are untouched (laptop-only scope).

**Tech Stack:** Python 3.12, `uv`, pytest (`asyncio_mode=auto`), `argparse`, `azure.cosmos.aio` / `azure.identity.aio`, the `gh` CLI.

**Spec:** `docs/superpowers/specs/2026-07-01-charter-watch-and-quiet-async-clients-design.md`

**Conventions:** Conventional Commits; every commit ends with the trailer
`Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
Run from repo root with `uv run`. Verify at the end with `uv run pytest cli/tests core/tests -q`, `uv run ruff check .`, `uv run lint-imports`.

---

## Part 1 — Quiet the async clients

### Task 1: Make the Cosmos gateway + stores closeable

**Files:**
- Modify: `core/src/dsf/memory/azure_store.py` (protocol 25-29; `CosmosMemoryStore` after line 116; `_SdkCosmosGateway` 119-143)
- Modify: `core/src/dsf/charter/cosmos_store.py` (`CosmosCharterStore`, after line 41)
- Modify: `testing/dsf_testing/azure_doubles.py` (`InMemoryCosmosGateway` 32-53)
- Modify: `testing/dsf_testing/charter.py` (`InMemoryCharterStore` 8-19)
- Test: `core/tests/memory/test_cosmos_aclose.py` (create)

- [ ] **Step 1: Write the failing test**

Create `core/tests/memory/test_cosmos_aclose.py`:

```python
"""_SdkCosmosGateway.aclose() closes the aio client + credential exactly once."""

from __future__ import annotations

from dsf.charter.cosmos_store import CosmosCharterStore
from dsf.memory.azure_store import CosmosMemoryStore, _SdkCosmosGateway


class _Closable:
    def __init__(self) -> None:
        self.closed = 0

    async def close(self) -> None:
        self.closed += 1


async def test_sdk_gateway_aclose_closes_client_and_credential():
    gw = _SdkCosmosGateway("https://example.documents.azure.com", "prod")
    client, cred = _Closable(), _Closable()
    gw._client, gw._cred = client, cred
    await gw.aclose()
    assert client.closed == 1 and cred.closed == 1


async def test_sdk_gateway_aclose_before_first_use_is_noop():
    gw = _SdkCosmosGateway("https://example.documents.azure.com", "prod")
    await gw.aclose()  # no client built yet -> must not raise


async def test_stores_delegate_aclose_to_gateway():
    class _Gw:
        def __init__(self) -> None:
            self.closed = 0

        async def aclose(self) -> None:
            self.closed += 1

    gw = _Gw()
    await CosmosMemoryStore(gw).aclose()
    assert gw.closed == 1
    gw2 = _Gw()
    await CosmosCharterStore(gw2).aclose()
    assert gw2.closed == 1
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/memory/test_cosmos_aclose.py -q`
Expected: FAIL — `AttributeError: '_SdkCosmosGateway' object has no attribute 'aclose'` (and `_cred`).

- [ ] **Step 3: Add `aclose()` to the protocol + real gateway**

In `core/src/dsf/memory/azure_store.py`, extend the protocol (lines 25-29):

```python
class CosmosGateway(Protocol):
    """Narrow async seam over Cosmos data-plane operations."""

    async def upsert(self, container: str, item: dict) -> None: ...
    async def query(self, container: str, field: str, value: Any) -> list[dict]: ...
    async def aclose(self) -> None: ...
```

Replace `_SdkCosmosGateway.__init__` + `_container` (lines 126-143) so the credential is retained, and append `aclose`:

```python
    def __init__(self, endpoint: str, database: str) -> None:
        self._endpoint = endpoint
        self._database = database
        self._client: Any = None
        self._cred: Any = None

    def _container(self, name: str) -> Any:  # pragma: no cover - requires azure extra
        if self._client is None:
            try:
                from azure.cosmos.aio import CosmosClient
                from azure.identity.aio import DefaultAzureCredential
            except ImportError as exc:
                raise RuntimeError(
                    "azure extra not installed; run: uv pip install -e '.[azure]'"
                ) from exc
            self._cred = DefaultAzureCredential()
            self._client = CosmosClient(self._endpoint, credential=self._cred)
        return self._client.get_database_client(self._database).get_container_client(name)

    async def aclose(self) -> None:
        """Close the aio Cosmos client + credential; a no-op before first use."""
        if self._client is not None:
            await self._client.close()
            self._client = None
        if self._cred is not None:
            await self._cred.close()
            self._cred = None
```

- [ ] **Step 4: Add `aclose()` to both stores**

In `core/src/dsf/memory/azure_store.py`, add to `CosmosMemoryStore` (after `get_lessons`, at line 116):

```python
    async def aclose(self) -> None:
        """Close the underlying Cosmos gateway (aio client + credential)."""
        await self._gw.aclose()
```

In `core/src/dsf/charter/cosmos_store.py`, add to `CosmosCharterStore` (after `put_charter`, line 41):

```python
    async def aclose(self) -> None:
        """Close the underlying Cosmos gateway (aio client + credential)."""
        await self._gw.aclose()
```

- [ ] **Step 5: Add no-op `aclose()` to the test doubles**

In `testing/dsf_testing/azure_doubles.py`, add to `InMemoryCosmosGateway` (after `query`, line 53):

```python
    async def aclose(self) -> None:
        """No-op: the dict-backed gateway holds no resources."""
        return None
```

In `testing/dsf_testing/charter.py`, add to `InMemoryCharterStore` (after `put_charter`, line 18):

```python
    async def aclose(self) -> None:
        """No-op: the dict-backed store holds no resources."""
        return None
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `uv run pytest core/tests/memory/test_cosmos_aclose.py -q`
Expected: PASS (3 tests).

- [ ] **Step 7: Commit**

```bash
git add core/src/dsf/memory/azure_store.py core/src/dsf/charter/cosmos_store.py \
  testing/dsf_testing/azure_doubles.py testing/dsf_testing/charter.py \
  core/tests/memory/test_cosmos_aclose.py
git commit -m "fix: add aclose to Cosmos gateway and stores

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: Close the store in `status` and `sync`

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (imports; add `_charter_store`; `_cmd_charter_status` 213-239; `_cmd_charter_sync` 242-283)
- Test: `cli/tests/cli/test_charter.py` (add a test)

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_charter.py`:

```python
def test_status_closes_the_store(monkeypatch, capsys):
    closed = {"n": 0}

    class _ClosingStore(InMemoryCharterStore):
        async def aclose(self) -> None:
            closed["n"] += 1

    store = _ClosingStore()
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter._live_blob_sha", lambda a, p: (None, None))
    rc = main(["charter", "status", "--product", "alpha"])
    assert rc == 0
    assert closed["n"] == 1
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/cli/test_charter.py::test_status_closes_the_store -q`
Expected: FAIL — `closed["n"] == 0` (status never closes the store).

- [ ] **Step 3: Add the `_charter_store` context manager**

In `cli/src/dsf/cli/charter.py`, add `import contextlib` next to the other stdlib imports (after line 13 `import asyncio`), then add this helper just above `_cmd_charter_status` (line 213):

```python
@contextlib.asynccontextmanager
async def _charter_store(product: str):
    """Yield a charter store for ``product`` and close it (aio client) on exit."""
    store = build_charter_store(_settings(product))
    try:
        yield store
    finally:
        await store.aclose()
```

- [ ] **Step 4: Route `status` through it**

Replace the body of `_cmd_charter_status` (lines 213-239) with a single async run that holds the store open then closes it:

```python
def _cmd_charter_status(args: argparse.Namespace) -> int:
    """Print the stored charter status and its drift vs the file/ref."""
    product = args.product

    async def _run():
        async with _charter_store(product) as store:
            return await store.get_charter(product)

    try:
        stored = asyncio.run(_run())
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1
    live_sha, note = _live_blob_sha(args, product)
    print(f"[dsf] charter {product}: {_status_label(stored, live_sha)}")
    if stored is not None:
        if stored.last_synced_at is not None:
            print(f"[dsf]   last_synced_at={stored.last_synced_at.isoformat()}")
        if stored.charter is not None and stored.charter.source_sha:
            print(
                f"[dsf]   stored_sha={stored.charter.source_sha} "
                f"ref={stored.charter.source_ref}"
            )
        if stored.last_error:
            print(f"[dsf]   last_error={stored.last_error}")
    if note:
        print(f"[dsf]   note: {note}")
    elif live_sha is not None:
        print(f"[dsf]   file_sha={live_sha}")
    return 0
```

- [ ] **Step 5: Route `sync` through it**

Replace `_cmd_charter_sync` (lines 242-283). Move the store build into `_charter_store` and keep the branching inside the async body:

```python
def _cmd_charter_sync(args: argparse.Namespace) -> int:
    """Pull the charter into Cosmos from a local file (default) or a repo ref."""
    product = args.product

    if args.ref is not None:
        repo_full = _resolve_repo(product)
        if not repo_full:
            print(
                f"[dsf] error: cannot resolve repo for product {product!r} from the "
                "owner App Config index (is DSF_OWNER_APPCONFIG_ENDPOINT set?)",
                file=sys.stderr,
            )
            return 1
    else:
        repo_full = None
        path = Path(args.file or CHARTER_PATH)
        try:
            data = path.read_bytes()
        except OSError as exc:
            print(f"[dsf] error: cannot read {path}: {exc}", file=sys.stderr)
            return 1

    async def _run():
        async with _charter_store(product) as store:
            if repo_full is not None:
                app = build_repo_app_client(_app_settings(product))
                return await sync_charter(
                    store, app, product=product, repo=repo_full, ref=args.ref
                )
            return await sync_charter_text(
                store,
                product=product,
                text=data.decode("utf-8"),
                source_sha=git_blob_sha(data),
                source_ref=f"file:{path}",
            )

    try:
        stored = asyncio.run(_run())
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    print(f"[dsf] synced charter for {product}: {stored.status.value}")
    if stored.last_error:
        print(f"[dsf]   {stored.last_error}")
    return 1 if stored.status == CharterStatus.INVALID else 0
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -q`
Expected: PASS (existing + the new `test_status_closes_the_store`).

- [ ] **Step 7: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "fix: close cosmos client in charter status/sync

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: Single-loop `implement` that closes the store

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (`_cmd_charter_implement` 407-488)
- Test: `cli/tests/cli/test_charter.py`

This task only restructures `implement` to one `asyncio.run` that closes the store; the watch call is added in Task 7. The function returns a 3-tuple `(rc, issue_url, assigned)` from an async helper.

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_charter.py`:

```python
def test_implement_closes_the_store(monkeypatch, capsys):
    closed = {"n": 0}

    class _ClosingStore(InMemoryCharterStore):
        async def aclose(self) -> None:
            closed["n"] += 1

    store = _ClosingStore()
    _put(store, _ok_charter("blobsha"), CharterStatus.OK)
    client = RecordingRepoClient(
        {CHARTER_PATH: (render_charter(_ok_charter("blobsha")), "blobsha")}
    )
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    assert rc == 0
    assert closed["n"] == 1
```

Note: `--no-wait` is added to the parser in Task 7. For this task, temporarily invoke without it (`main(["charter", "implement", "--product", "alpha"])`) and drop the flag; re-add `--no-wait` to this test in Task 7.

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/cli/test_charter.py::test_implement_closes_the_store -q`
Expected: FAIL — `closed["n"] == 0` (implement builds the store but never closes it).

- [ ] **Step 3: Split `implement` into a sync shell + async body**

Replace `_cmd_charter_implement` (lines 407-488) with:

```python
async def _implement_async(
    product: str, repo_full: str, args: argparse.Namespace
) -> tuple[int, str | None, bool]:
    """Sync charter, open the constitution PR, file+assign the bootstrap issue.

    Returns ``(rc, issue_url, assigned)``: ``rc`` non-zero on a hard failure,
    ``issue_url`` of the filed bootstrap issue (or ``None``), and whether the
    Copilot coding agent was successfully assigned (so the caller can decide
    whether it is worth watching the build).
    """
    async with _charter_store(product) as store:
        app = build_repo_app_client(_app_settings(product))

        stored = await sync_charter(
            store, app, product=product, repo=repo_full, ref="main"
        )
        print(f"[dsf] synced charter for {product} from main: {stored.status.value}")
        if stored.status != CharterStatus.OK or stored.charter is None:
            print(
                f"[dsf] error: charter for {product} on main is "
                f"{stored.status.value.lower()}; merge the charter PR "
                "(and fix any errors) before implementing.",
                file=sys.stderr,
            )
            if stored.last_error:
                print(f"[dsf]   note: {stored.last_error}", file=sys.stderr)
            return 1, None, False

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
        try:
            issue_url = await app.create_issue(repo_full, title, body, [HANDOFF_LABEL])
            print(f"[dsf] filed bootstrap issue + assigned Copilot: {issue_url}")
            return 0, issue_url, True
        except CodingAgentAssignmentError as exc:
            # GitHub forbids assigning the coding agent with a GitHub App
            # installation token, so fall back to the operator's local `gh` user
            # token for just the assignment step (see _assign_copilot_via_gh).
            if _assign_copilot_via_gh(repo_full, exc.issue_node_id):
                print(
                    f"[dsf] filed bootstrap issue + assigned Copilot via gh: "
                    f"{exc.issue_url}"
                )
                return 0, exc.issue_url, True
            print(f"[dsf] filed bootstrap issue: {exc.issue_url}")
            print(
                "[dsf] warning: could not assign the Copilot coding agent; assign it "
                "manually (ensure `gh auth login` and that the Copilot coding agent is "
                "enabled for the repo).",
                file=sys.stderr,
            )
            return 0, exc.issue_url, False


def _cmd_charter_implement(args: argparse.Namespace) -> int:
    """Seed the Spec Kit build from an accepted charter, then watch the build.

    Pulls the charter from ``main`` into Cosmos first (like ``dsf charter sync
    --ref main``), renders the constitution via an auto-merged PR, and files one
    ``creation:ready`` bootstrap issue assigned to the Copilot Coding Agent.
    Unless ``--no-wait`` is given, it then blocks watching the coding agent's PR
    and requests Copilot code review once it is ready.
    """
    product = args.product
    repo_full = _resolve_repo(product)
    if not repo_full:
        print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
        return 1

    try:
        rc, issue_url, assigned = asyncio.run(
            _implement_async(product, repo_full, args)
        )
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1
    return rc
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -q`
Expected: PASS. (`test_implement_closes_the_store` now closes once; existing implement tests still pass. Remember to call `main` without `--no-wait` until Task 7 adds the flag.)

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "refactor: run charter implement in one loop and close the store

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Part 2 — Monitor the build + hand off to Copilot review

### Task 4: Extend `_gh_graphql` typed vars + `_find_agent_pr`

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (`_gh_graphql`; add constants + `_find_agent_pr`)
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_charter.py`:

```python
def test_gh_graphql_passes_int_vars(monkeypatch):
    import subprocess

    seen = {}

    def fake_run(argv, check, capture_output, text):
        seen["argv"] = argv
        return SimpleNamespace(stdout='{"data":{"ok":true}}')

    monkeypatch.setattr("subprocess.run", fake_run)
    data = charter._gh_graphql("query($num:Int!){x}", int_vars={"num": 7}, owner="o")
    assert data == {"ok": True}
    assert "-F" in seen["argv"] and "num=7" in seen["argv"]
    assert "-f" in seen["argv"] and "owner=o" in seen["argv"]


def test_find_agent_pr_selects_copilot_pr(monkeypatch):
    def fake_graphql(query, *, int_vars=None, **variables):
        return {
            "repository": {
                "issue": {
                    "timelineItems": {
                        "nodes": [
                            {
                                "__typename": "CrossReferencedEvent",
                                "source": {
                                    "__typename": "PullRequest",
                                    "number": 8,
                                    "url": "https://x/pull/8",
                                    "isDraft": True,
                                    "state": "OPEN",
                                    "author": {"login": "copilot-swe-agent"},
                                },
                            }
                        ]
                    }
                }
            }
        }

    monkeypatch.setattr("dsf.cli.charter._gh_graphql", fake_graphql)
    pr = charter._find_agent_pr("org/alpha", 7)
    assert pr == {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}


def test_find_agent_pr_none_when_no_copilot_pr(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: {"repository": {"issue": {"timelineItems": {"nodes": []}}}},
    )
    assert charter._find_agent_pr("org/alpha", 7) is None
```

Ensure the test module imports `types` and the module under test as `charter` (e.g. `from dsf.cli import charter`) — add these imports at the top of the test file if absent.

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "gh_graphql_passes_int or find_agent_pr" -q`
Expected: FAIL — `_gh_graphql` has no `int_vars` param; `_find_agent_pr` is undefined.

- [ ] **Step 3: Extend `_gh_graphql` and add `_find_agent_pr`**

In `cli/src/dsf/cli/charter.py`, change `_gh_graphql`'s signature + argv build to accept typed ints (keep string `**variables` for back-compat):

```python
def _gh_graphql(
    query: str, *, int_vars: dict[str, int] | None = None, **variables: str
) -> dict:
    """Run a GraphQL ``query`` via ``gh api graphql`` and return its ``data``.

    String variables go through ``-f``; ``int_vars`` go through ``-F`` so GraphQL
    ``Int!`` variables are typed correctly. Uses the operator's ``gh`` user token.
    Raises ``CalledProcessError`` when ``gh`` fails and ``RuntimeError`` on a
    GraphQL error.
    """
    import json
    import subprocess

    argv = ["gh", "api", "graphql", "-f", f"query={query}"]
    for key, value in variables.items():
        argv += ["-f", f"{key}={value}"]
    for key, ivalue in (int_vars or {}).items():
        argv += ["-F", f"{key}={ivalue}"]
    result = subprocess.run(argv, check=True, capture_output=True, text=True)
    payload = json.loads(result.stdout)
    if payload.get("errors"):
        raise RuntimeError(f"GraphQL error: {payload['errors']}")
    return payload["data"]
```

Add these constants near `_COPILOT_LOGIN` (line 342):

```python
#: GraphQL: PR nodes linked to an issue (the coding agent opens one per issue).
_GH_ISSUE_TIMELINE_QUERY = (
    "query($owner:String!,$name:String!,$num:Int!){"
    "repository(owner:$owner,name:$name){issue(number:$num){"
    "timelineItems(itemTypes:[CONNECTED_EVENT,CROSS_REFERENCED_EVENT],first:50){"
    "nodes{__typename "
    "... on ConnectedEvent{subject{__typename ... on PullRequest{"
    "number url isDraft state author{login}}}} "
    "... on CrossReferencedEvent{source{__typename ... on PullRequest{"
    "number url isDraft state author{login}}}}}}}}}"
)
```

Add `_find_agent_pr` just below `_gh_graphql`:

```python
def _find_agent_pr(repo: str, issue_number: int) -> dict | None:
    """Return the Copilot coding agent's PR linked to ``issue_number``, or None.

    Scans the issue timeline for connected/cross-referenced PRs and picks the one
    authored by ``copilot-swe-agent`` (GraphQL bot logins have no ``app/`` prefix),
    preferring an OPEN one and, among those, the highest number. Returns
    ``{"number", "url", "is_draft", "state"}``.
    """
    owner, _, name = repo.partition("/")
    data = _gh_graphql(
        _GH_ISSUE_TIMELINE_QUERY, int_vars={"num": issue_number}, owner=owner, name=name
    )
    nodes = data["repository"]["issue"]["timelineItems"]["nodes"]
    prs: list[dict] = []
    for node in nodes:
        pr = node.get("subject") or node.get("source") or {}
        if pr.get("__typename") != "PullRequest":
            continue
        login = (pr.get("author") or {}).get("login", "")
        if login.split("/")[-1] != _COPILOT_LOGIN:
            continue
        prs.append(
            {
                "number": pr["number"],
                "url": pr["url"],
                "is_draft": bool(pr["isDraft"]),
                "state": pr["state"],
            }
        )
    if not prs:
        return None
    prs.sort(key=lambda p: (p["state"] == "OPEN", p["number"]), reverse=True)
    return prs[0]
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "gh_graphql or find_agent_pr" -q`
Expected: PASS (existing `_gh_graphql` tests + the three new ones).

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: find the coding agent PR linked to an issue via gh

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Request Copilot review + idempotency check

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (add `_pr_has_copilot_reviewer`, `_request_copilot_review`)
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_charter.py`:

```python
def test_request_copilot_review_runs_gh_pr_edit(monkeypatch):
    import subprocess

    calls = []
    monkeypatch.setattr(
        "subprocess.run",
        lambda argv, check, capture_output, text: calls.append(argv)
        or SimpleNamespace(stdout=""),
    )
    charter._request_copilot_review("org/alpha", "https://x/pull/8")
    assert calls == [
        ["gh", "pr", "edit", "https://x/pull/8", "--repo", "org/alpha",
         "--add-reviewer", "@copilot"]
    ]


def test_pr_has_copilot_reviewer_true(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: {
            "repository": {
                "pullRequest": {
                    "reviewRequests": {
                        "nodes": [
                            {"requestedReviewer": {"__typename": "Bot",
                                                   "login": "copilot-pull-request-reviewer"}}
                        ]
                    }
                }
            }
        },
    )
    assert charter._pr_has_copilot_reviewer("org/alpha", 8) is True


def test_pr_has_copilot_reviewer_false(monkeypatch):
    monkeypatch.setattr(
        "dsf.cli.charter._gh_graphql",
        lambda *a, **k: {
            "repository": {"pullRequest": {"reviewRequests": {"nodes": []}}}
        },
    )
    assert charter._pr_has_copilot_reviewer("org/alpha", 8) is False
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "request_copilot_review or has_copilot_reviewer" -q`
Expected: FAIL — helpers undefined.

- [ ] **Step 3: Implement the two helpers**

Add near `_find_agent_pr` in `cli/src/dsf/cli/charter.py`:

```python
_GH_PR_REVIEWERS_QUERY = (
    "query($owner:String!,$name:String!,$num:Int!){"
    "repository(owner:$owner,name:$name){pullRequest(number:$num){"
    "reviewRequests(first:20){nodes{requestedReviewer{__typename "
    "... on Bot{login} ... on User{login}}}}}}}"
)


def _pr_has_copilot_reviewer(repo: str, number: int) -> bool:
    """True when Copilot code review is already requested on the PR."""
    owner, _, name = repo.partition("/")
    data = _gh_graphql(
        _GH_PR_REVIEWERS_QUERY, int_vars={"num": number}, owner=owner, name=name
    )
    nodes = data["repository"]["pullRequest"]["reviewRequests"]["nodes"]
    for node in nodes:
        login = (node.get("requestedReviewer") or {}).get("login", "")
        if "copilot" in login.lower():
            return True
    return False


def _request_copilot_review(repo: str, pr_url: str) -> None:
    """Request GitHub Copilot code review on ``pr_url`` via the operator's gh token.

    gh 2.x supports the ``@copilot`` reviewer value natively; this runs under a
    user token, sidestepping the App-installation-token restriction. Raises
    ``CalledProcessError`` if gh fails.
    """
    import subprocess

    subprocess.run(
        ["gh", "pr", "edit", pr_url, "--repo", repo, "--add-reviewer", "@copilot"],
        check=True,
        capture_output=True,
        text=True,
    )
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "request_copilot_review or has_copilot_reviewer" -q`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: request Copilot review via gh pr edit

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: The watch loop `_watch_and_request_review`

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (add poll-cadence constants/resolvers + the loop)
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_charter.py`:

```python
def _watch_env(monkeypatch, pr_states, *, reviewer=False):
    """Drive _find_agent_pr through a scripted list of PR snapshots (or None)."""
    seq = list(pr_states)

    def fake_find(repo, issue):
        return seq.pop(0) if seq else pr_states[-1]

    requested = {"n": 0}
    monkeypatch.setattr("dsf.cli.charter._find_agent_pr", fake_find)
    monkeypatch.setattr(
        "dsf.cli.charter._pr_has_copilot_reviewer", lambda repo, num: reviewer
    )
    monkeypatch.setattr(
        "dsf.cli.charter._request_copilot_review",
        lambda repo, url: requested.__setitem__("n", requested["n"] + 1),
    )
    return requested


def test_watch_requests_review_when_pr_becomes_ready(monkeypatch, capsys):
    draft = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}
    ready = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [None, draft, ready])
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    out = capsys.readouterr().out
    assert rc == 0 and requested["n"] == 1
    assert "requested Copilot review" in out


def test_watch_skips_when_review_already_requested(monkeypatch, capsys):
    ready = {"number": 8, "url": "https://x/pull/8", "is_draft": False, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [ready], reviewer=True)
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    assert rc == 0 and requested["n"] == 0
    assert "already requested" in capsys.readouterr().out


def test_watch_stops_when_pr_closed(monkeypatch, capsys):
    closed = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "CLOSED"}
    requested = _watch_env(monkeypatch, [closed])
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=None, poll_interval=0.0, sleep=lambda s: None
    )
    assert rc == 0 and requested["n"] == 0
    assert "closed" in capsys.readouterr().out.lower()


def test_watch_times_out(monkeypatch, capsys):
    draft = {"number": 8, "url": "https://x/pull/8", "is_draft": True, "state": "OPEN"}
    requested = _watch_env(monkeypatch, [draft])
    clock = iter([0.0, 5.0, 999.0])
    rc = charter._watch_and_request_review(
        "org/alpha", 7, timeout=10.0, poll_interval=0.0,
        sleep=lambda s: None, clock=lambda: next(clock),
    )
    assert rc == 2 and requested["n"] == 0
    assert "re-run" in capsys.readouterr().out
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k watch -q`
Expected: FAIL — `_watch_and_request_review` undefined.

- [ ] **Step 3: Implement the loop + cadence resolvers**

Add near the top of `cli/src/dsf/cli/charter.py` (with the other constants) and a `import time` at the top with the stdlib imports:

```python
_DEFAULT_WATCH_POLL_INTERVAL = 20.0
_MIN_WATCH_POLL_INTERVAL = 1.0
_DEFAULT_WATCH_TIMEOUT = 1800.0
_WATCH_POLL_ENV = "DSF_WATCH_POLL_INTERVAL"


def _resolve_watch_poll_interval(explicit: float | None) -> float:
    """Poll cadence: explicit flag (floored 1s) > ``DSF_WATCH_POLL_INTERVAL`` > 20s."""
    import os

    if explicit is not None:
        return max(_MIN_WATCH_POLL_INTERVAL, explicit)
    raw = os.environ.get(_WATCH_POLL_ENV, "").strip()
    if raw:
        try:
            return max(_MIN_WATCH_POLL_INTERVAL, float(raw))
        except ValueError:
            pass
    return _DEFAULT_WATCH_POLL_INTERVAL


def _resolve_watch_timeout(explicit: float | None) -> float | None:
    """Timeout seconds: explicit flag > 1800s; ``0`` (or negative) means unbounded."""
    seconds = _DEFAULT_WATCH_TIMEOUT if explicit is None else explicit
    return None if seconds <= 0 else seconds
```

Add the loop below `_request_copilot_review`:

```python
def _watch_and_request_review(
    repo: str,
    issue_number: int,
    *,
    timeout: float | None,
    poll_interval: float,
    sleep=time.sleep,
    clock=time.monotonic,
    out=print,
) -> int:
    """Poll the coding agent's PR; request Copilot review once it is ready.

    Returns ``0`` on success (review requested, already requested, or the PR
    reached a terminal non-reviewable state) and ``2`` on timeout (resumable).
    """
    start = clock()
    last_status = ""

    def _emit(status: str) -> None:
        nonlocal last_status
        if status != last_status:
            out(f"[dsf] {status}")
            last_status = status

    while True:
        pr = _find_agent_pr(repo, issue_number)
        if pr is None:
            _emit("waiting for the coding agent to open its PR...")
        elif pr["state"] in ("MERGED", "CLOSED"):
            out(f"[dsf] {repo}#{pr['number']} is {pr['state'].lower()}; nothing to review.")
            return 0
        elif not pr["is_draft"]:
            if _pr_has_copilot_reviewer(repo, pr["number"]):
                out(f"[dsf] Copilot review already requested: {pr['url']}")
                return 0
            _request_copilot_review(repo, pr["url"])
            out(f"[dsf] requested Copilot review: {pr['url']}")
            return 0
        else:
            _emit(f"{repo}#{pr['number']} building (draft)...")

        if timeout is not None and clock() - start >= timeout:
            out(
                f"[dsf] still building after {int(timeout)}s; re-run "
                f"`dsf charter watch --product <product>` to resume."
            )
            return 2
        sleep(poll_interval)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k watch -q`
Expected: PASS (4 watch tests).

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: add charter build watch loop

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Wire `implement` to watch (with `--no-wait`)

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (`_cmd_charter_implement` tail; `implement` argparse 511-516)
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_charter.py`:

```python
def test_implement_watches_build_by_default(monkeypatch, capsys):
    _seed_ok_implement(monkeypatch)
    seen = {}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda repo, issue, **kw: seen.update(repo=repo, issue=issue) or 0,
    )
    rc = main(["charter", "implement", "--product", "alpha"])
    assert rc == 0
    # RecordingRepoClient.create_issue returns local://issue/1 -> issue number 1
    assert seen == {"repo": "org/alpha", "issue": 1}


def test_implement_no_wait_skips_watch(monkeypatch, capsys):
    _seed_ok_implement(monkeypatch)
    called = {"n": 0}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda *a, **k: called.__setitem__("n", called["n"] + 1) or 0,
    )
    rc = main(["charter", "implement", "--product", "alpha", "--no-wait"])
    assert rc == 0 and called["n"] == 0
    assert "dsf charter watch" in capsys.readouterr().out
```

Note: confirm `RecordingRepoClient.create_issue` returns a URL ending in the issue number (e.g. `local://issue/1`); `_issue_number_from_url` (Step 3) parses the trailing integer. If the double returns a different shape, adjust the expected `issue` value to match.

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "implement_watches or no_wait" -q`
Expected: FAIL — `--no-wait` is not a known arg / watch is never called.

- [ ] **Step 3: Add the URL parser, wire the tail, add the flags**

Add a helper near `_find_agent_pr` in `cli/src/dsf/cli/charter.py`:

```python
def _issue_number_from_url(url: str) -> int:
    """Parse the trailing issue number from an issue URL/ref (e.g. .../issues/7)."""
    return int(url.rstrip("/").rsplit("/", 1)[-1])
```

Replace the tail of `_cmd_charter_implement` (the `return rc` block from Task 3) with:

```python
    try:
        rc, issue_url, assigned = asyncio.run(
            _implement_async(product, repo_full, args)
        )
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    if rc != 0 or not assigned or issue_url is None:
        return rc
    if args.no_wait:
        print(
            f"[dsf] not waiting; run `dsf charter watch --product {product}` to hand "
            "off to Copilot review once the build is ready."
        )
        return 0
    return _watch_and_request_review(
        repo_full,
        _issue_number_from_url(issue_url),
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
```

Extend the `implement` parser (lines 511-516) with the new flags:

```python
    implement_parser = charter_sub.add_parser(
        "implement",
        help="render the constitution + file the Spec Kit bootstrap issue, then "
        "watch the build and request Copilot review",
    )
    implement_parser.add_argument("--product", required=True, help="product key")
    implement_parser.add_argument(
        "--no-wait",
        action="store_true",
        dest="no_wait",
        help="file + assign only; do not watch the build or request review",
    )
    implement_parser.add_argument(
        "--timeout",
        type=float,
        default=None,
        help="max seconds to watch the build (default 1800; 0 = unbounded)",
    )
    implement_parser.add_argument(
        "--poll-interval",
        type=float,
        default=None,
        dest="poll_interval",
        help=f"seconds between polls (default 20; env {_WATCH_POLL_ENV})",
    )
    implement_parser.set_defaults(func=_cmd_charter_implement)
```

Re-add `--no-wait` to `test_implement_closes_the_store` (Task 3) so it does not block on the watch.

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -q`
Expected: PASS (all charter tests).

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: charter implement watches the build and requests Copilot review

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: The standalone `dsf charter watch` subcommand

**Files:**
- Modify: `cli/src/dsf/cli/charter.py` (add `_newest_handoff_issue`, `_cmd_charter_watch`, register the subparser)
- Test: `cli/tests/cli/test_charter.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/cli/test_charter.py`:

```python
def test_watch_subcommand_parses():
    parser = build_parser()
    args = parser.parse_args(["charter", "watch", "--product", "alpha", "--issue", "7"])
    assert args.command == "charter" and args.product == "alpha" and args.issue == 7


def test_watch_command_uses_explicit_issue(monkeypatch):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    seen = {}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda repo, issue, **kw: seen.update(repo=repo, issue=issue) or 0,
    )
    rc = main(["charter", "watch", "--product", "alpha", "--issue", "7"])
    assert rc == 0 and seen == {"repo": "org/alpha", "issue": 7}


def test_watch_command_finds_newest_handoff_issue(monkeypatch):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    monkeypatch.setattr("dsf.cli.charter._newest_handoff_issue", lambda repo: 42)
    seen = {}
    monkeypatch.setattr(
        "dsf.cli.charter._watch_and_request_review",
        lambda repo, issue, **kw: seen.update(issue=issue) or 0,
    )
    rc = main(["charter", "watch", "--product", "alpha"])
    assert rc == 0 and seen == {"issue": 42}


def test_watch_command_errors_when_no_issue(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    monkeypatch.setattr("dsf.cli.charter._newest_handoff_issue", lambda repo: None)
    rc = main(["charter", "watch", "--product", "alpha"])
    assert rc == 1 and "no open" in capsys.readouterr().err.lower()
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "watch_subcommand or watch_command" -q`
Expected: FAIL — `watch` is not a registered subcommand.

- [ ] **Step 3: Implement `_newest_handoff_issue`, the handler, and register it**

Add helpers near the other watch helpers in `cli/src/dsf/cli/charter.py`:

```python
def _newest_handoff_issue(repo: str) -> int | None:
    """Newest OPEN issue carrying the handoff label (the bootstrap issue), or None."""
    import json
    import subprocess

    try:
        result = subprocess.run(
            [
                "gh", "issue", "list", "--repo", repo, "--label", HANDOFF_LABEL,
                "--state", "open", "--limit", "20", "--json", "number",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        rows = json.loads(result.stdout)
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError):
        return None
    numbers = [row["number"] for row in rows if "number" in row]
    return max(numbers) if numbers else None


def _cmd_charter_watch(args: argparse.Namespace) -> int:
    """Watch the coding agent's build for a product and hand it off to review."""
    product = args.product
    repo_full = _resolve_repo(product)
    if not repo_full:
        print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
        return 1

    issue_number = args.issue if args.issue is not None else _newest_handoff_issue(repo_full)
    if issue_number is None:
        print(
            f"[dsf] error: no open {HANDOFF_LABEL!r} issue found for {product}; pass "
            "--issue N.",
            file=sys.stderr,
        )
        return 1

    return _watch_and_request_review(
        repo_full,
        issue_number,
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )
```

Register the subparser inside `add_charter_subcommands`, next to `implement` (after line 516):

```python
    watch_parser = charter_sub.add_parser(
        "watch",
        help="watch the coding agent's build and request Copilot review when ready",
    )
    watch_parser.add_argument("--product", required=True, help="product key")
    watch_parser.add_argument(
        "--issue", type=int, default=None,
        help="bootstrap issue number (default: newest open handoff issue)",
    )
    watch_parser.add_argument(
        "--timeout", type=float, default=None,
        help="max seconds to watch (default 1800; 0 = unbounded)",
    )
    watch_parser.add_argument(
        "--poll-interval", type=float, default=None, dest="poll_interval",
        help=f"seconds between polls (default 20; env {_WATCH_POLL_ENV})",
    )
    watch_parser.set_defaults(func=_cmd_charter_watch)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -k "watch_subcommand or watch_command" -q`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: add dsf charter watch subcommand

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 9: Full verification gate

**Files:** none (verification only)

- [ ] **Step 1: Run the full CLI + core suites**

Run: `uv run pytest cli/tests core/tests -q`
Expected: PASS (no failures; the pre-existing aiohttp `Unclosed client session` lines no longer appear on stderr for the charter commands under test).

- [ ] **Step 2: Lint**

Run: `uv run ruff check .`
Expected: `All checks passed!`

- [ ] **Step 3: Import boundaries**

Run: `uv run lint-imports`
Expected: `Contracts: 4 kept, 0 broken.`

- [ ] **Step 4: Manual smoke (optional, real Copilot build)**

Run: `uv run dsf charter status --product todo-app`
Expected: clean output, no `Unclosed client session` / `Unclosed connector` lines on stderr.

Then, when ready to drive a real build:
`uv run dsf charter implement --product <product>` (blocks, streams `building (draft)...`, then `requested Copilot review: <pr-url>`), or `--no-wait` to skip.

- [ ] **Step 5: Push**

```bash
git push origin main
```

---

## Notes for the executor

- Run everything with `uv run` from the repo root; never bare `python`/`pytest`.
- `asyncio_mode = "auto"` — async tests need no decorator.
- The new tests reference `charter` (the module under test); add `from dsf.cli import
  charter` to the test file's imports if not already present. `SimpleNamespace` is already
  imported at the top of `cli/tests/cli/test_charter.py` (`from types import SimpleNamespace`).
  Existing helpers used above (`main`, `build_parser`,
  `_seed_ok_implement`, `_put`, `_ok_charter`, `InMemoryCharterStore`,
  `RecordingRepoClient`, `render_charter`, `CHARTER_PATH`, `HANDOFF_LABEL`,
  `CharterStatus`) already exist in `cli/tests/cli/test_charter.py`.
- Keep changes inside `core/src/dsf/{memory,charter}`, `testing/dsf_testing/`, and
  `cli/src/dsf/cli/charter.py`. Do NOT touch the ACA runtime (`s7_filing`) or the
  `core` GitHub client — this is laptop-only scope.
- If `RecordingRepoClient.create_issue` returns a URL whose trailing segment is not
  the integer issue number, adjust `_issue_number_from_url` expectations in Task 7's
  test accordingly (read the double in `testing/dsf_testing/github.py`).
