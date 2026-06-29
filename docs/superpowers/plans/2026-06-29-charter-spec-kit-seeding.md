# Charter → Spec Kit greenfield seeding (`dsf charter implement`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** For greenfield products, bridge the human-owned charter to a built app by seeding every new repo with the GitHub Spec Kit scaffold and adding a `dsf charter implement` command that renders a charter-derived constitution (landed via an auto-merged PR) and files one `creation:ready` bootstrap issue for the Copilot Coding Agent.

**Architecture:** A deterministic, offline renderer in `core` projects the `Charter` into a Spec Kit constitution. The `cli` member's `dsf charter implement` reuses the existing GitHub App client (`open_file_pr` + `create_issue`) to land the constitution and file the bootstrap issue; `main` is branch-protected so the constitution lands via an **auto-merged PR**, not a direct push. `dsf new`'s `seed_repo` step is decorated to run `specify init` into the repo clone. No new cross-member imports: the renderer lives in `core`, the command/templates in `cli`.

**Tech Stack:** Python 3.12, `uv` workspace, `pytest` (asyncio_mode=auto), `httpx` (App client + `MockTransport` tests), `dsf_testing` doubles, `specify-cli` v0.11.9 (operator prerequisite, bundled templates).

---

## Spec

Source of truth: `docs/superpowers/specs/2026-06-29-charter-spec-kit-seeding-design.md`. Read it before starting.

Key decisions locked in:
- Constitution lands via `open_file_pr(..., enable_auto_merge=True)` (branch protection blocks direct pushes; auto-merge degrades to a human merge at low maturity — tracked in #97).
- `specify init` flags: `--here --integration copilot --script sh --ignore-agent-tools --force`, pinned to `specify-cli` **v0.11.9** (templates bundled, offline).
- The charter is embedded in the bootstrap issue as `UNTRUSTED` data via the existing `dsf.charter.context.charter_context` helper (ADR 0017).
- Deferred (do NOT build): paved road (#96), per-task `taskstoissues` fan-out, deploy/URL/validate loop, council rename.

## File Structure

| File | Responsibility | Action |
| --- | --- | --- |
| `core/src/dsf/charter/constitution.py` | Pure `render_constitution(charter)` + `CONSTITUTION_PATH` | Create |
| `core/tests/charter/test_constitution.py` | Renderer golden/field tests | Create |
| `core/src/dsf/github_app_client.py` | `open_file_pr(enable_auto_merge=...)` + graceful degrade | Modify |
| `core/tests/test_github_app_client.py` | Auto-merge happy/degrade tests | Modify |
| `testing/dsf_testing/github.py` | `RecordingRepoClient.create_issue` + record `enable_auto_merge` | Modify |
| `cli/tests/test_recording_repo_client.py` | Focused double tests | Create |
| `cli/src/dsf/instance/bootstrap_issue.py` | `render_bootstrap_issue(charter) -> (title, body)` | Create |
| `cli/tests/instance/test_bootstrap_issue.py` | Bootstrap-issue render tests | Create |
| `cli/src/dsf/cli/charter.py` | `dsf charter implement` subcommand | Modify |
| `cli/tests/cli/test_charter.py` | `implement` command tests | Modify |
| `cli/src/dsf/instance/provisioner.py` | Decorate `seed_repo` with `specify init` (clone path) | Modify |
| `cli/tests/instance/test_provisioner.py` | Clone-seed tests | Modify |
| `docs/site/get-started/provision-a-factory.md` | `specify-cli` prerequisite + implement step | Modify |
| `docs/site/get-started/quickstart.md` | `specify-cli` prerequisite | Modify |

## Commands (from repo root unless noted)

- Single test: `uv run pytest <path>::<test> -q`
- Member suite: `uv run pytest core/tests -q` / `uv run pytest cli/tests -q`
- Lint: `uv run ruff check <paths>` (line length 100, rules E,F,I,UP,B)
- Import boundaries: `uv run lint-imports` (must stay 4 kept / 0 broken)

---

## Task 1: Constitution renderer (deterministic, pure)

**Files:**
- Create: `core/src/dsf/charter/constitution.py`
- Test: `core/tests/charter/test_constitution.py`

- [ ] **Step 1: Write the failing test**

Create `core/tests/charter/test_constitution.py`:

```python
from __future__ import annotations

from datetime import date

from dsf.charter.constitution import CONSTITUTION_PATH, render_constitution
from dsf.contracts.charter import Charter


def _charter(**over) -> Charter:
    base = dict(
        product="alpha",
        vision="Make billing painless.",
        target_users="Small-business owners.",
        goals=["Cut invoice time", "Reduce errors"],
        non_goals=["No payroll", "No tax filing"],
        success_metrics=["p50 invoice < 60s", "error rate < 1%"],
        constraints="Must run in the EU. PCI-DSS scope minimized.",
        glossary={"Invoice": "a billable document", "Dunning": "payment chasing"},
        source_sha="deadbeef",
        source_ref="main",
    )
    base.update(over)
    return Charter(**base)


def test_path_constant():
    assert CONSTITUTION_PATH == ".specify/memory/constitution.md"


def test_title_and_preamble_carry_product_vision_and_users():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    assert md.startswith("<!-- dsf:constitution")
    assert "# alpha Constitution" in md
    assert "Make billing painless." in md
    assert "Small-business owners." in md


def test_every_charter_list_field_lands_in_a_section():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    for needle in (
        "Cut invoice time",          # goal
        "No payroll",                # non-goal
        "p50 invoice < 60s",         # metric
        "Must run in the EU.",       # constraints verbatim
        "**Invoice**: a billable document",  # glossary
    ):
        assert needle in md


def test_footer_date_is_deterministic_via_today_seam():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    assert "**Ratified**: 2026-01-01" in md
    assert "**Last Amended**: 2026-01-01" in md


def test_marker_carries_charter_provenance():
    md = render_constitution(_charter(), today=date(2026, 1, 1))
    assert "source_ref=main" in md
    assert "source_sha=deadbeef" in md


def test_empty_optional_fields_render_cleanly():
    md = render_constitution(
        _charter(non_goals=[], glossary={}, constraints=""),
        today=date(2026, 1, 1),
    )
    assert "(none declared in the charter)" in md
    assert "(no shared vocabulary declared)" in md
    assert "No additional constraints declared in the charter." in md


def test_same_charter_renders_identically():
    a = render_constitution(_charter(), today=date(2026, 1, 1))
    b = render_constitution(_charter(), today=date(2026, 1, 1))
    assert a == b
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest core/tests/charter/test_constitution.py -q`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.charter.constitution'`.

- [ ] **Step 3: Write the implementation**

Create `core/src/dsf/charter/constitution.py`:

```python
"""Deterministic Charter -> Spec Kit constitution renderer.

Projects the human-owned :class:`Charter` into the Spec Kit constitution document
(`.specify/memory/constitution.md`) that the ``/speckit.*`` lifecycle reads as its
governing principles. Pure and offline: the same charter always renders the same
constitution. The constitution is a *derived projection* of the charter and
introduces no new intent; the charter stays the single source of truth (ADR 0017).
"""

from __future__ import annotations

from datetime import UTC, date, datetime

from dsf.contracts.charter import Charter

#: Repo-relative path Spec Kit reads the constitution from.
CONSTITUTION_PATH = ".specify/memory/constitution.md"

_SCHEMA_VERSION = 1
_NONE_DECLARED = "- (none declared in the charter)"


def _today() -> date:
    return datetime.now(UTC).date()


def render_constitution(charter: Charter, *, today: date | None = None) -> str:
    """Render ``charter`` as a Spec Kit constitution (markdown).

    ``today`` defaults to the current UTC date and is injectable so tests pin the
    dated footer.
    """
    stamp = (today or _today()).isoformat()

    def bullets(items: list[str]) -> str:
        return "\n".join(f"- {item}" for item in items) if items else _NONE_DECLARED

    glossary = (
        "\n".join(f"- **{term}**: {definition}" for term, definition in charter.glossary.items())
        if charter.glossary
        else "- (no shared vocabulary declared)"
    )
    constraints = (
        charter.constraints.strip()
        or "No additional constraints declared in the charter."
    )

    sections = [
        (
            f"<!-- dsf:constitution schema_version={_SCHEMA_VERSION} "
            f"source_ref={charter.source_ref or 'unknown'} "
            f"source_sha={charter.source_sha or 'unknown'} -->"
        ),
        f"# {charter.product} Constitution",
        (
            "This constitution is a derived projection of the human-owned product "
            "charter (`.dsf/charter.md`). The charter is the single source of truth; "
            "if they ever disagree, the charter wins and this file is re-rendered "
            "from it.\n\n"
            f"**Vision.** {charter.vision.strip()}\n\n"
            f"**Target users.** {charter.target_users.strip()}"
        ),
        "## Core Principles",
        (
            "### I. Charter-Governed (NON-NEGOTIABLE)\n"
            "The product charter is authoritative. Every spec, plan, task, and line "
            "of code must trace back to the charter's vision, goals, and success "
            "metrics. Nothing here may introduce intent the charter does not express."
        ),
        ("### II. Goal-Driven Scope\nWork exists to advance these charter goals:\n" + bullets(charter.goals)),
        (
            "### III. Explicit Non-Goals\n"
            "The charter rules the following out of scope; do not build them:\n"
            + bullets(charter.non_goals)
        ),
        (
            "### IV. Measured by Success Metrics\n"
            '"Done and faithful to intent" means moving these charter metrics:\n'
            + bullets(charter.success_metrics)
        ),
        (
            "### V. Shared Vocabulary\n"
            "Use the charter's glossary terms consistently in specs, code, and UI:\n"
            + glossary
        ),
        "## Additional Constraints",
        constraints,
        "## Governance",
        (
            "This constitution supersedes ad-hoc practice for this product. It is "
            "regenerated by `dsf charter implement` whenever the charter changes; "
            "amend the charter (`.dsf/charter.md`), never this file directly. All "
            "specs, plans, and PRs must verify compliance with the principles above."
        ),
        f"**Version**: 1.0.0 | **Ratified**: {stamp} | **Last Amended**: {stamp}",
    ]
    return "\n\n".join(sections) + "\n"


__all__ = ["CONSTITUTION_PATH", "render_constitution"]
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest core/tests/charter/test_constitution.py -q`
Expected: PASS (7 passed).

- [ ] **Step 5: Lint**

Run: `uv run ruff check core/src/dsf/charter/constitution.py core/tests/charter/test_constitution.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add core/src/dsf/charter/constitution.py core/tests/charter/test_constitution.py
git commit -m "feat: render a charter-derived Spec Kit constitution

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: App client — opt-in auto-merge on `open_file_pr`

**Files:**
- Modify: `core/src/dsf/github_app_client.py`
- Test: `core/tests/test_github_app_client.py`

- [ ] **Step 1: Write the failing tests**

Append to `core/tests/test_github_app_client.py` (the helpers `_app_client`, `_token_handler` already exist at the top of the file):

```python
async def test_open_file_pr_enables_auto_merge_when_requested():
    graphql_bodies: list[dict] = []

    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})
        if method == "PUT" and "/contents/" in path:
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            return httpx.Response(
                201,
                json={
                    "html_url": "https://github.com/org/alpha/pull/7",
                    "number": 7,
                    "node_id": "PR_kw7",
                },
            )
        if method == "POST" and path == "/graphql":
            graphql_bodies.append(json.loads(request.read()))
            return httpx.Response(
                200,
                json={"data": {"enablePullRequestAutoMerge": {"pullRequest": {"autoMergeRequest": {"enabledAt": "now"}}}}},
            )
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".specify/memory/constitution.md",
        content="C",
        branch="charter/constitution/x",
        title="T",
        body="B",
        message="m",
        enable_auto_merge=True,
    )
    assert url == "https://github.com/org/alpha/pull/7"
    assert graphql_bodies and "enablePullRequestAutoMerge" in graphql_bodies[0]["query"]
    assert graphql_bodies[0]["variables"] == {"pullRequestId": "PR_kw7"}


async def test_open_file_pr_auto_merge_degrades_when_not_allowed():
    def extra(request: httpx.Request) -> httpx.Response:
        method, path = request.method, request.url.path
        if method == "GET" and path.endswith("/git/ref/heads/main"):
            return httpx.Response(200, json={"object": {"sha": "basesha"}})
        if method == "POST" and path.endswith("/git/refs"):
            return httpx.Response(201, json={})
        if method == "GET" and "/contents/" in path:
            return httpx.Response(404, json={})
        if method == "PUT" and "/contents/" in path:
            return httpx.Response(201, json={})
        if method == "POST" and path.endswith("/pulls"):
            return httpx.Response(
                201,
                json={"html_url": "https://github.com/org/alpha/pull/8", "number": 8, "node_id": "PR_x"},
            )
        if method == "POST" and path == "/graphql":
            return httpx.Response(
                200,
                json={"errors": [{"message": "Pull request Auto merge is not allowed for this repository"}]},
            )
        return httpx.Response(500, json={"unexpected": path})

    client = _app_client(_token_handler(extra))
    url = await client.open_file_pr(
        "org/alpha",
        path=".specify/memory/constitution.md",
        content="C",
        branch="b",
        title="T",
        body="B",
        message="m",
        enable_auto_merge=True,
    )
    # The GraphQL error is swallowed; the PR is still reported (a human will merge it).
    assert url == "https://github.com/org/alpha/pull/8"
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest core/tests/test_github_app_client.py -k auto_merge -q`
Expected: FAIL — `open_file_pr() got an unexpected keyword argument 'enable_auto_merge'`.

- [ ] **Step 3: Add the mutation constant**

In `core/src/dsf/github_app_client.py`, after the `_REPLACE_ACTORS_MUTATION` block (around line 36), add:

```python
_ENABLE_AUTO_MERGE_MUTATION = (
    "mutation($pullRequestId:ID!){"
    "enablePullRequestAutoMerge(input:{pullRequestId:$pullRequestId,mergeMethod:SQUASH}){"
    "pullRequest{autoMergeRequest{enabledAt}}}}"
)
```

- [ ] **Step 4: Add the private helper**

In `core/src/dsf/github_app_client.py`, add this method to `GitHubAppClient` immediately before `open_file_pr` (around line 238):

```python
    async def _enable_auto_merge(
        self, client: httpx.AsyncClient, token: str, pr_node_id: str
    ) -> None:
        """Best-effort enable PR auto-merge.

        A repo without auto-merge (low creation maturity) rejects the mutation; we
        swallow that so the constitution PR simply waits for a human merge instead
        of failing the seeding (the maturity-gated switch is tracked in #97).
        """
        try:
            await self._graphql(
                client, token, _ENABLE_AUTO_MERGE_MUTATION, {"pullRequestId": pr_node_id}
            )
        except (RuntimeError, httpx.HTTPStatusError):
            pass
```

- [ ] **Step 5: Thread the parameter through `open_file_pr`**

In `core/src/dsf/github_app_client.py`, change the `open_file_pr` signature to add the new keyword (after `labels`):

```python
    async def open_file_pr(
        self,
        repo: str,
        *,
        path: str,
        content: str,
        branch: str,
        base: str = "main",
        title: str,
        body: str,
        message: str,
        labels: list[str] | None = None,
        enable_auto_merge: bool = False,
    ) -> str:
```

Then, at the end of `open_file_pr`, replace the final `return created["html_url"]` block. The current tail is:

```python
            created = pull.json()
            if labels:
                applied = await client.post(
                    f"/repos/{repo}/issues/{created['number']}/labels",
                    headers=headers,
                    json={"labels": list(labels)},
                )
                applied.raise_for_status()
            return created["html_url"]
```

Replace it with:

```python
            created = pull.json()
            if labels:
                applied = await client.post(
                    f"/repos/{repo}/issues/{created['number']}/labels",
                    headers=headers,
                    json={"labels": list(labels)},
                )
                applied.raise_for_status()
            if enable_auto_merge:
                await self._enable_auto_merge(client, token, created["node_id"])
            return created["html_url"]
```

Also update the `open_file_pr` docstring's first paragraph to note the new behavior — append this sentence to the existing docstring:

```python
        When ``enable_auto_merge`` is set, best-effort enable auto-merge on the PR
        (a no-op on repos without auto-merge enabled).
```

- [ ] **Step 6: Run the new tests + the existing `open_file_pr` tests**

Run: `uv run pytest core/tests/test_github_app_client.py -k "open_file_pr or auto_merge" -q`
Expected: PASS (all `open_file_pr*` and both `auto_merge` tests). The existing default-path tests confirm `enable_auto_merge=False` makes no `/graphql` call.

- [ ] **Step 7: Lint**

Run: `uv run ruff check core/src/dsf/github_app_client.py core/tests/test_github_app_client.py`
Expected: `All checks passed!`

- [ ] **Step 8: Commit**

```bash
git add core/src/dsf/github_app_client.py core/tests/test_github_app_client.py
git commit -m "feat: opt-in auto-merge on GitHubAppClient.open_file_pr

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Bootstrap issue template

**Files:**
- Create: `cli/src/dsf/instance/bootstrap_issue.py`
- Test: `cli/tests/instance/test_bootstrap_issue.py`

- [ ] **Step 1: Write the failing test**

Create `cli/tests/instance/test_bootstrap_issue.py`:

```python
from __future__ import annotations

from dsf.contracts.charter import Charter
from dsf.instance.bootstrap_issue import render_bootstrap_issue


def _charter() -> Charter:
    return Charter(
        product="alpha",
        vision="Make billing painless.",
        target_users="SMB owners.",
        goals=["Cut invoice time"],
        success_metrics=["p50 < 60s"],
        constraints="EU only.",
        glossary={"Invoice": "a billable document"},
        source_sha="sha",
        source_ref="main",
    )


def test_title_names_the_product():
    title, _ = render_bootstrap_issue(_charter())
    assert "alpha" in title


def test_body_walks_the_speckit_lifecycle_and_points_at_docs():
    _, body = render_bootstrap_issue(_charter())
    for needle in (
        "/speckit.specify",
        "/speckit.plan",
        "/speckit.tasks",
        ".specify/memory/constitution.md",
        ".dsf/charter.md",
    ):
        assert needle in body


def test_body_embeds_charter_as_untrusted_data():
    _, body = render_bootstrap_issue(_charter())
    assert '<product_charter trust="UNTRUSTED">' in body
    assert "Make billing painless." in body  # vision rendered inside the envelope


def test_body_requests_a_large_model():
    _, body = render_bootstrap_issue(_charter())
    assert "Opus 4.8" in body
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_bootstrap_issue.py -q`
Expected: FAIL with `ModuleNotFoundError: No module named 'dsf.instance.bootstrap_issue'`.

- [ ] **Step 3: Write the implementation**

Create `cli/src/dsf/instance/bootstrap_issue.py`:

```python
"""The single ``creation:ready`` bootstrap issue for greenfield seeding.

``dsf charter implement`` files this one issue to hand a greenfield product's
accepted charter to the Copilot Coding Agent, which runs the whole Spec Kit
lifecycle in one session. The charter is embedded as UNTRUSTED data (ADR 0017)
through the shared :func:`dsf.charter.context.charter_context` chokepoint.
"""

from __future__ import annotations

from dsf.charter.constitution import CONSTITUTION_PATH
from dsf.charter.context import charter_context
from dsf.charter.sync import CHARTER_PATH
from dsf.contracts.charter import Charter

#: Requested (not guaranteed) model for the build — Copilot's model is a
#: repo/account setting, so the issue states this as a request. See the design's
#: Risks section.
_REQUESTED_MODEL = "Claude Opus 4.8"


def render_bootstrap_issue(charter: Charter) -> tuple[str, str]:
    """Return ``(title, body)`` for the greenfield Spec Kit bootstrap issue."""
    title = f"Build {charter.product} from its charter (Spec Kit)"
    body = (
        f"Bootstrap the **{charter.product}** product from its accepted charter "
        "using the GitHub Spec Kit lifecycle, in a single session.\n\n"
        "## What to do (one session)\n"
        f"1. `/speckit.specify` — derive the product specification from the charter "
        f"below and `{CHARTER_PATH}`.\n"
        "2. `/speckit.plan` — choose a sensible tech stack and architecture "
        "(a paved-road default is not wired yet — your choice for now).\n"
        "3. `/speckit.tasks` — break the plan into actionable tasks.\n"
        "4. Implement the tasks and open pull request(s); keep the `ci` check green.\n\n"
        "## Governing documents\n"
        f"- Constitution: `{CONSTITUTION_PATH}` (derived from the charter — your "
        "principles and quality gates).\n"
        f"- Charter: `{CHARTER_PATH}` (the human-owned source of truth).\n\n"
        "## Product charter (reference)\n"
        f"{charter_context(charter)}\n\n"
        "---\n"
        f"_Model request: this build is intended to run as {_REQUESTED_MODEL}. "
        "Copilot's model is a repository/account setting, so treat this as a "
        "request, not a guarantee._"
    )
    return title, body


__all__ = ["render_bootstrap_issue"]
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_bootstrap_issue.py -q`
Expected: PASS (4 passed).

- [ ] **Step 5: Lint**

Run: `uv run ruff check cli/src/dsf/instance/bootstrap_issue.py cli/tests/instance/test_bootstrap_issue.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/bootstrap_issue.py cli/tests/instance/test_bootstrap_issue.py
git commit -m "feat: render the greenfield Spec Kit bootstrap issue

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Extend the App-client test double

**Files:**
- Modify: `testing/dsf_testing/github.py`
- Test: `cli/tests/test_recording_repo_client.py`

`RecordingRepoClient` is the App-client double used by the charter command tests. It needs a `create_issue` method (the real App client files + assigns Copilot) and must record the new `enable_auto_merge` flag. To keep `dsf_testing` dependency-light (it imports only contracts/typing), the double raises a caller-supplied exception for the no-Copilot fallback rather than importing `dsf.ports`.

- [ ] **Step 1: Write the failing test**

Create `cli/tests/test_recording_repo_client.py`:

```python
from __future__ import annotations

import asyncio

from dsf.ports import CodingAgentAssignmentError
from dsf_testing.github import RecordingRepoClient


def test_create_issue_records_and_returns_url():
    client = RecordingRepoClient({})
    url = asyncio.run(client.create_issue("org/alpha", "T", "B", ["creation:ready"]))
    assert url == "local://issue/1"
    assert client.issues == [
        {"repo": "org/alpha", "title": "T", "body": "B", "labels": ["creation:ready"]}
    ]


def test_create_issue_raises_supplied_error():
    boom = CodingAgentAssignmentError(
        "no copilot", issue_url="local://issue/1", issue_node_id="N"
    )
    client = RecordingRepoClient({}, create_issue_error=boom)
    try:
        asyncio.run(client.create_issue("org/alpha", "T", "B", []))
        raise AssertionError("expected CodingAgentAssignmentError")
    except CodingAgentAssignmentError as exc:
        assert exc.issue_url == "local://issue/1"


def test_open_file_pr_records_auto_merge_flag():
    client = RecordingRepoClient({})
    asyncio.run(
        client.open_file_pr(
            "org/alpha",
            path="p",
            content="x",
            branch="b",
            title="T",
            body="B",
            message="m",
            enable_auto_merge=True,
        )
    )
    assert client.prs[0]["enable_auto_merge"] is True
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest cli/tests/test_recording_repo_client.py -q`
Expected: FAIL — `RecordingRepoClient.__init__() got an unexpected keyword argument 'create_issue_error'` (and no `create_issue` / `issues` attributes).

- [ ] **Step 3: Modify the double**

In `testing/dsf_testing/github.py`, update `RecordingRepoClient.__init__` to accept and store the new attributes. Replace the existing `__init__`:

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

Add a `create_issue` method to `RecordingRepoClient` (place it after `read_file`):

```python
    async def create_issue(
        self, repo: str, title: str, body: str, labels: list[str]
    ) -> str:
        if self._create_issue_error is not None:
            raise self._create_issue_error
        self.issues.append(
            {"repo": repo, "title": title, "body": body, "labels": list(labels)}
        )
        return f"local://issue/{len(self.issues)}"
```

Update `RecordingRepoClient.open_file_pr` to accept and record `enable_auto_merge`. Replace its signature and body:

```python
    async def open_file_pr(
        self,
        repo: str,
        *,
        path: str,
        content: str,
        branch: str,
        base: str = "main",
        title: str,
        body: str,
        message: str,
        labels: list[str] | None = None,
        enable_auto_merge: bool = False,
    ) -> str:
        self.prs.append(
            {
                "repo": repo,
                "path": path,
                "content": content,
                "branch": branch,
                "base": base,
                "title": title,
                "body": body,
                "message": message,
                "labels": list(labels or []),
                "enable_auto_merge": enable_auto_merge,
            }
        )
        return f"https://github.com/{repo}/pull/{len(self.prs)}"
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest cli/tests/test_recording_repo_client.py -q`
Expected: PASS (3 passed).

- [ ] **Step 5: Run the existing charter tests (regression — the double is shared)**

Run: `uv run pytest cli/tests/cli/test_charter.py -q`
Expected: PASS (existing `init`/`sync`/`status` tests unaffected — the new params are optional).

- [ ] **Step 6: Lint**

Run: `uv run ruff check testing/dsf_testing/github.py cli/tests/test_recording_repo_client.py`
Expected: `All checks passed!`

- [ ] **Step 7: Commit**

```bash
git add testing/dsf_testing/github.py cli/tests/test_recording_repo_client.py
git commit -m "test: add create_issue + auto-merge recording to RecordingRepoClient

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: `dsf charter implement` subcommand

**Files:**
- Modify: `cli/src/dsf/cli/charter.py`
- Test: `cli/tests/cli/test_charter.py`

Depends on Tasks 1, 3, 4. The command reuses the existing `_resolve_repo`, `_settings`, `_app_settings`, `build_charter_store`, `build_repo_app_client`, `_status_label`, and `_live_blob_sha` seams from `charter.py`.

- [ ] **Step 1: Write the failing tests**

Add to `cli/tests/cli/test_charter.py`. First, ensure `HANDOFF_LABEL` is importable in the test module — add this import near the top (with the other `dsf.contracts` imports):

```python
from dsf.contracts.handoff import HANDOFF_LABEL
```

Then append these tests at the end of the file:

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


def test_implement_subcommand_parses():
    parser = build_parser()
    args = parser.parse_args(["charter", "implement", "--product", "alpha"])
    assert args.command == "charter" and args.product == "alpha"


def test_implement_opens_constitution_pr_and_files_issue(monkeypatch, capsys):
    client = _seed_ok_implement(monkeypatch)
    rc = main(["charter", "implement", "--product", "alpha"])
    out = capsys.readouterr().out
    assert rc == 0
    assert len(client.prs) == 1
    assert client.prs[0]["path"] == ".specify/memory/constitution.md"
    assert client.prs[0]["enable_auto_merge"] is True
    assert len(client.issues) == 1
    assert client.issues[0]["labels"] == [HANDOFF_LABEL]
    assert "opened constitution PR" in out and "filed bootstrap issue" in out


def test_implement_refuses_when_charter_stale(monkeypatch, capsys):
    store = InMemoryCharterStore()
    _put(store, _ok_charter("oldsha"), CharterStatus.OK)
    client = RecordingRepoClient({CHARTER_PATH: (render_charter(_ok_charter("newsha")), "newsha")})
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha"])
    assert rc == 1 and "stale" in capsys.readouterr().err
    assert not client.prs and not client.issues


def test_implement_refuses_when_charter_missing(monkeypatch, capsys):
    store = InMemoryCharterStore()
    client = RecordingRepoClient({})  # read_file -> None -> live_sha None -> "missing"
    monkeypatch.setattr("dsf.cli.charter.build_charter_store", lambda s: store)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/alpha")
    rc = main(["charter", "implement", "--product", "alpha"])
    assert rc == 1 and "missing" in capsys.readouterr().err
    assert not client.prs and not client.issues


def test_implement_unknown_product(monkeypatch, capsys):
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: None)
    rc = main(["charter", "implement", "--product", "ghost"])
    assert rc == 1 and "not in registry" in capsys.readouterr().err


def test_implement_reports_copilot_assignment_failure(monkeypatch, capsys):
    from dsf.ports import CodingAgentAssignmentError

    boom = CodingAgentAssignmentError(
        "no copilot", issue_url="local://issue/1", issue_node_id="N"
    )
    client = _seed_ok_implement(monkeypatch, create_issue_error=boom)
    rc = main(["charter", "implement", "--product", "alpha"])
    captured = capsys.readouterr()
    assert rc == 0  # filing succeeded; assignment failure is a warning, not a hard fail
    assert "filed bootstrap issue: local://issue/1" in captured.out
    assert "assignment FAILED" in captured.err
    assert len(client.prs) == 1  # constitution PR still opened
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `uv run pytest cli/tests/cli/test_charter.py -k implement -q`
Expected: FAIL — `argument charter_command: invalid choice: 'implement'` (subcommand not registered yet).

- [ ] **Step 3: Add imports to `charter.py`**

In `cli/src/dsf/cli/charter.py`, add these imports alongside the existing `dsf.charter.*` / `dsf.contracts.*` imports near the top:

```python
from dsf.charter.constitution import CONSTITUTION_PATH, render_constitution
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.instance.bootstrap_issue import render_bootstrap_issue
from dsf.ports import CodingAgentAssignmentError
```

- [ ] **Step 4: Add the command handler**

In `cli/src/dsf/cli/charter.py`, add this function immediately after `_cmd_charter_init` (before `charter_init`):

```python
def _cmd_charter_implement(args: argparse.Namespace) -> int:
    """Seed the Spec Kit build from an accepted charter.

    Renders the constitution from the synced charter and lands it via an
    auto-merged PR (``main`` is branch-protected), then files one ``creation:ready``
    bootstrap issue assigned to the Copilot Coding Agent. Refuses unless the charter
    is present and synced OK against ``main`` (reusing the ``status`` drift logic),
    so we never seed from a non-accepted charter.
    """
    product = args.product
    repo_full = _resolve_repo(product)
    if not repo_full:
        print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
        return 1

    try:
        store = build_charter_store(_settings(product))
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    stored = asyncio.run(store.get_charter(product))
    live_sha, note = _live_blob_sha(argparse.Namespace(ref="main", file=None), product)
    label = _status_label(stored, live_sha)
    if label != "ok":
        print(
            f"[dsf] error: charter for {product} is {label}; merge it and run "
            "`dsf charter sync --product "
            f"{product} --ref main` before implementing.",
            file=sys.stderr,
        )
        if note:
            print(f"[dsf]   note: {note}", file=sys.stderr)
        return 1

    charter = stored.charter  # non-None when label == "ok"
    try:
        app = build_repo_app_client(_app_settings(product))
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    constitution = render_constitution(charter)
    branch = f"charter/constitution-{uuid.uuid4().hex[:8]}"
    pr_url = asyncio.run(
        app.open_file_pr(
            repo_full,
            path=CONSTITUTION_PATH,
            content=constitution,
            branch=branch,
            title=f"Add Spec Kit constitution for {product}",
            body=(
                "Constitution derived from the product charter by "
                "`dsf charter implement`. Auto-merges on a green `ci` check at high "
                "creation maturity; awaits a human review at low maturity."
            ),
            message=f"docs: add spec kit constitution for {product}",
            enable_auto_merge=True,
        )
    )
    print(f"[dsf] opened constitution PR (auto-merge requested): {pr_url}")

    title, body = render_bootstrap_issue(charter)
    try:
        issue_url = asyncio.run(app.create_issue(repo_full, title, body, [HANDOFF_LABEL]))
        print(f"[dsf] filed bootstrap issue + assigned Copilot: {issue_url}")
    except CodingAgentAssignmentError as exc:
        print(f"[dsf] filed bootstrap issue: {exc.issue_url}")
        print(
            f"[dsf] warning: Copilot coding agent assignment FAILED ({exc}); "
            "assign Copilot manually once enabled.",
            file=sys.stderr,
        )
    return 0
```

- [ ] **Step 5: Register the subcommand**

In `cli/src/dsf/cli/charter.py`, inside `add_charter_subcommands`, add the `implement` parser right after the `init` parser block (before the `for name, func, help_text in (...)` loop):

```python
    implement_parser = charter_sub.add_parser(
        "implement",
        help="render the constitution + file the Spec Kit bootstrap issue",
    )
    implement_parser.add_argument("--product", required=True, help="product key")
    implement_parser.set_defaults(func=_cmd_charter_implement)
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `uv run pytest cli/tests/cli/test_charter.py -q`
Expected: PASS (all existing + the 6 new `implement` tests).

- [ ] **Step 7: Lint + import boundaries**

Run: `uv run ruff check cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py && uv run lint-imports`
Expected: `All checks passed!` and `Contracts: 4 kept, 0 broken.`

- [ ] **Step 8: Commit**

```bash
git add cli/src/dsf/cli/charter.py cli/tests/cli/test_charter.py
git commit -m "feat: add dsf charter implement to seed the Spec Kit build

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 6: Decorate `seed_repo` with `specify init`

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` (the `seed_repo` step description + `_seed_repo`)
- Test: `cli/tests/instance/test_provisioner.py`

`_seed_repo` branches on whether a local clone exists (the same `Path(...).is_dir()` pattern the provisioner already uses for `create_repo` at line ~553). With a clone (the normal `dsf new` path via `gh repo create --clone`): run `specify init` + write the baseline `ci` workflow into the clone, then commit and push to `main` (before `branch_protection`). Without a clone: fall back to today's Contents-API PUT of just the `ci` workflow. This keeps every existing test green (they have no clone → fallback path) and adds full scaffold seeding.

- [ ] **Step 1: Write the failing tests**

In `cli/tests/instance/test_provisioner.py`, add three tests next to the existing seed tests (after `test_seed_repo_is_idempotent_when_workflow_present`, ~line 1427):

```python
def test_seed_repo_from_clone_runs_specify_and_pushes(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    (tmp_path / "demo").mkdir()  # the clone created by create_repo --clone
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[:3] == ["git", "status", "--porcelain"]:
            return _completed(stdout=" M .specify/memory/constitution.md\n")
        return _completed(stdout="")

    prov = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme"), run=fake_run
    )
    prov._seed_repo()

    specify = [c for c in calls if c[:2] == ["specify", "init"]]
    assert specify, "specify init should run in the clone"
    assert "--here" in specify[0] and "--force" in specify[0]
    assert "--integration" in specify[0] and "copilot" in specify[0]
    assert "--script" in specify[0] and "sh" in specify[0]
    workflow = tmp_path / "demo" / ".github" / "workflows" / "ci.yml"
    assert workflow.read_text(encoding="utf-8").startswith("name: ci")
    assert ["git", "push", "origin", "HEAD:main"] in calls


def test_seed_repo_from_clone_skips_commit_when_no_diff(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)
    (tmp_path / "demo").mkdir()
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        return _completed(stdout="")  # `git status --porcelain` clean -> nothing to do

    prov = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme"), run=fake_run
    )
    prov._seed_repo()

    assert not any("commit" in c for c in calls)
    assert not any(c[:2] == ["git", "push"] for c in calls)


def test_seed_repo_without_clone_falls_back_to_contents_api(monkeypatch, tmp_path):
    monkeypatch.chdir(tmp_path)  # no demo/ clone present
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if "--jq" in cmd and cmd[-1] == ".sha":
            raise subprocess.CalledProcessError(1, cmd, stderr="Not Found")
        return _completed(stdout="")

    prov = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme"), run=fake_run
    )
    prov._seed_repo()

    puts = [c for c in calls if "--method" in c and "PUT" in c]
    assert len(puts) == 1  # baseline ci workflow seeded via Contents API
    assert not any(c[:2] == ["specify", "init"] for c in calls)
```

Note: the two existing seed tests (`test_seed_repo_puts_baseline_ci_workflow_when_absent`, `test_seed_repo_is_idempotent_when_workflow_present`) run from a cwd with no `demo/` clone, so they exercise the Contents-API fallback and stay valid as-is.

- [ ] **Step 2: Run the new tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "seed_repo_from_clone or without_clone" -q`
Expected: FAIL — no `specify init` call is issued (current `_seed_repo` only does the Contents-API PUT), so the clone-path assertions fail.

- [ ] **Step 3: Refactor `_seed_repo` to branch on a clone**

In `cli/src/dsf/instance/provisioner.py`, replace the entire current `_seed_repo` method (lines ~644-673) with:

```python
    def _seed_repo(self) -> None:
        """Seed the repo before ``branch_protection``: Spec Kit scaffold + baseline ci.

        With a local clone (``create_repo --clone``), commit the ``specify init``
        scaffold and the baseline ``ci`` workflow from it and push to ``main``.
        Without a local clone (e.g. a re-run on a host that never cloned), fall back
        to seeding just the baseline ``ci`` workflow via the Contents API so the
        ruleset's required ``ci`` check stays producible. Both run under the
        operator's ``gh`` auth before ``branch_protection``, so pushing ``main`` is
        unobstructed.
        """
        clone_dir = self.spec.resolved_repo()
        if Path(clone_dir).is_dir():
            self._seed_repo_from_clone(clone_dir)
        else:
            self._seed_ci_workflow_via_api()

    def _seed_repo_from_clone(self, clone_dir: str) -> None:
        """Commit the Spec Kit scaffold + baseline ci workflow from the clone.

        ``specify init`` writes the ``.specify/`` scaffold and the Copilot command
        files; ``--force`` makes the re-run idempotent. When the resulting tree has
        no diff (a clean re-run), the commit/push is skipped.
        """
        self._run(
            [
                "specify", "init", "--here",
                "--integration", "copilot",
                "--script", "sh",
                "--ignore-agent-tools",
                "--force",
            ],
            check=True,
            cwd=clone_dir,
        )
        workflow = Path(clone_dir) / CI_WORKFLOW_PATH
        workflow.parent.mkdir(parents=True, exist_ok=True)
        workflow.write_text(baseline_ci_workflow(), encoding="utf-8")

        self._run(["git", "add", "-A"], check=True, cwd=clone_dir)
        status = self._run(
            ["git", "status", "--porcelain"],
            check=True, capture_output=True, text=True, cwd=clone_dir,
        )
        if not (getattr(status, "stdout", "") or "").strip():
            return
        self._run(
            [
                "git",
                "-c", "user.name=dsf-factory",
                "-c", "user.email=dsf-factory@users.noreply.github.com",
                "commit", "-m", "chore: seed spec kit scaffold and baseline ci workflow",
            ],
            check=True, cwd=clone_dir,
        )
        self._run(["git", "push", "origin", "HEAD:main"], check=True, cwd=clone_dir)

    def _seed_ci_workflow_via_api(self) -> None:
        """Seed only the baseline ``ci`` workflow via the Contents API (no clone).

        Idempotent: skips when the file is already present so a retry doesn't 422 on
        the missing blob sha.
        """
        repo = self.spec.github_repo()
        try:
            self._run(
                ["gh", "api", f"/repos/{repo}/contents/{CI_WORKFLOW_PATH}", "--jq", ".sha"],
                check=True, capture_output=True, text=True,
            )
            return
        except subprocess.CalledProcessError:
            pass
        content = base64.b64encode(baseline_ci_workflow().encode("utf-8")).decode("ascii")
        self._run(
            [
                "gh", "api", "--method", "PUT",
                f"/repos/{repo}/contents/{CI_WORKFLOW_PATH}",
                "-f", "message=chore: seed baseline ci workflow",
                "-f", f"content={content}",
                "-f", "branch=main",
            ],
            check=True,
        )
```

- [ ] **Step 4: Update the `seed_repo` step description**

In `cli/src/dsf/instance/provisioner.py`, update the `seed_repo` `ProvisionStep` description (lines ~253-258) to mention the scaffold:

```python
            ProvisionStep(
                name="seed_repo",
                description=(
                    f"Seed {s.github_repo()} with the Spec Kit scaffold (specify "
                    "init) and a baseline ci workflow so the required 'ci' check is "
                    "producible before branch protection"
                ),
            ),
```

- [ ] **Step 5: Run the seed tests (new + existing)**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k seed_repo -q`
Expected: PASS — the 3 new tests plus the 2 existing fallback tests.

- [ ] **Step 6: Run the full provisioner suite (regression)**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: PASS. Full-apply `execute=True` tests have no `demo/` clone in their cwd, so `seed_repo` takes the Contents-API fallback exactly as before — no new `specify`/`git` calls and no filesystem writes.

- [ ] **Step 7: Lint**

Run: `uv run ruff check cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py`
Expected: `All checks passed!` (`base64` and `subprocess` are still used by the fallback path, so no import removal is needed).

- [ ] **Step 8: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat: seed new product repos with the Spec Kit scaffold (specify init)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 7: Provisioning prerequisites documentation

**Files:**
- Modify: `docs/site/get-started/provision-a-factory.md`
- Modify: `docs/site/get-started/quickstart.md`

No tests (docs). Add the `specify-cli` prerequisite and mention the new `dsf charter implement` step.

- [ ] **Step 1: Add the `specify-cli` prerequisite to `provision-a-factory.md`**

In `docs/site/get-started/provision-a-factory.md`, in the `## Prerequisites` list, replace the existing **GitHub** bullet (lines ~62-63):

```markdown
- **GitHub:** a `gh auth login` session that can create repos under `--owner` and seed the
  baseline CI workflow.
```

with:

```markdown
- **GitHub:** a `gh auth login` session that can create repos under `--owner` and seed the
  baseline CI workflow.
- **Spec Kit CLI:** the [`specify`](https://github.com/github/spec-kit) CLI, used by `dsf
  new` to scaffold each product repo for Spec-Driven Development. Install it pinned:

    ```bash
    uv tool install specify-cli \
      --from git+https://github.com/github/spec-kit.git@v0.11.9
    ```

    The templates are bundled in the package (no network at `init`), so the pinned tag pins
    the scaffold. Ensure `specify` is on `PATH` (uv installs it to `~/.local/bin`).
```

- [ ] **Step 2: Extend the charter flow in `provision-a-factory.md`**

In `docs/site/get-started/provision-a-factory.md`, in the `## Seed its intent (the charter)` section, replace the closing flow block (lines ~124-133):

```markdown
Opening the PR is not the finish line. The charter only becomes authoritative once you
**review and merge** it, after which the next `dsf sweep` syncs it into the runtime. The
full path to a *charted* factory is:

```text
dsf new  →  charter PR  →  review & merge  →  dsf sweep
```

See [Operate it › The product charter](operate.md#the-product-charter) for the charter
commands in full.
```

with:

```markdown
Opening the PR is not the finish line. The charter only becomes authoritative once you
**review and merge** it, after which the next `dsf sweep` syncs it into the runtime. For a
**greenfield** product you then turn intent into a build with `dsf charter implement`: it
renders a charter-derived Spec Kit constitution (landed via an auto-merged PR) and files a
single `creation:ready` bootstrap issue assigned to the Copilot Coding Agent, which runs the
Spec Kit lifecycle (`/speckit.specify → plan → tasks → implement`) in one session. The full
path to a *building* factory is:

```text
dsf new  →  charter PR  →  review & merge  →  dsf sweep  →  dsf charter implement
```

See [Operate it › The product charter](operate.md#the-product-charter) for the charter
commands in full.
```

- [ ] **Step 3: Add the `specify-cli` prerequisite to `quickstart.md`**

In `docs/site/get-started/quickstart.md`, in the `## Prerequisites` list, after the **GitHub CLI** bullet (lines ~16-18), add:

```markdown
- The [**Spec Kit CLI**](https://github.com/github/spec-kit) (`specify`), used by `dsf new`
  to scaffold product repos for Spec-Driven Development. Install it pinned:
  `uv tool install specify-cli --from git+https://github.com/github/spec-kit.git@v0.11.9`.
```

- [ ] **Step 4: Verify the docs render sensibly**

Run: `git diff --stat docs/site/get-started/`
Expected: both files modified. Eyeball the diffs for fenced-block balance (no broken ```` ``` ```` nesting).

- [ ] **Step 5: Commit**

```bash
git add docs/site/get-started/provision-a-factory.md docs/site/get-started/quickstart.md
git commit -m "docs: add specify-cli prerequisite and dsf charter implement step

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Final verification

- [ ] **Run the full suite + gates**

```bash
uv run pytest -q
uv run ruff check .
uv run lint-imports
```

Expected: all tests pass; `All checks passed!`; `Contracts: 4 kept, 0 broken.`

- [ ] **Sanity-check the new command is wired**

Run: `uv run dsf charter implement --help`
Expected: prints the `implement` subcommand help (`--product` required).

---

## Spec coverage map

| Spec in-scope item | Task(s) |
| --- | --- |
| 1. Decorate `dsf new` seeding with `specify init` | Task 6 |
| 2. Deterministic charter→constitution renderer | Task 1 |
| 3. `dsf charter implement` (constitution PR + bootstrap issue) | Tasks 2, 3, 4, 5 |
| 4. Update provisioning prerequisites docs | Task 7 |
| Auto-merged constitution PR (branch-protection reconciliation, #97) | Tasks 2, 5 |
| Charter as `UNTRUSTED` data in the issue (ADR 0017) | Task 3 |
| No new cross-member imports (`lint-imports` green) | Tasks 5, Final |
