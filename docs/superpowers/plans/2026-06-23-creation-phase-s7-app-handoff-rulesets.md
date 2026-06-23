# Stage 3 — S7 App Handoff + Branch-Protection Rulesets Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the GitHub Copilot Coding Agent the builder: S7 files issues *and* assigns the Coding Agent under the DSF GitHub App, and `dsf new` sets a real, deterministic branch-protection ruleset (driven by the renamed `creation_maturity` dial) instead of the old `allow_auto_merge` no-op — resolving #54.

**Architecture:** `GitHubAppClient` (Stage 2, token-minting only) gains two async action methods — `create_issue` (REST file → capture node id → assign) and `assign_coding_agent` (GraphQL `suggestedActors` + `replaceActorsForAssignable` for the `copilot-swe-agent` bot) — so it satisfies the existing `GitHubClient` port and S7 stays unchanged. `build_services` constructs the App-backed client when the App is configured (reading the private key from Key Vault), else falls back to `RealGitHubClient` (Fork A: app-preferred with gh-CLI fallback). Tokens are scoped to the single product repo by **name** via a static `GITHUB_REPOSITORY` env var. The branch-protection ruleset is applied with the **operator's interactive `gh` auth** at provision time (Fork B), so the App needs no `administration:write` scope; the ruleset requires a status check named **`ci`** (a documented DSF convention the product CI must emit) plus N required reviews per dial.

**Tech Stack:** Python 3.12, `uv` workspace, `httpx` (+ `httpx.MockTransport` for tests), `PyJWT` + `cryptography`, `pydantic`, GitHub REST + GraphQL, GitHub repository rulesets, Bicep, pytest (`asyncio_mode=auto`).

---

## Locked decisions (do not re-litigate)

- **Fork A — runtime filing:** App-preferred with gh-CLI fallback. `build_services` wires `GitHubAppClient` when the App is configured; otherwise `RealGitHubClient`. Only the App path assigns the Coding Agent; the `gh` fallback files only (used in local/dev where no App is configured).
- **Fork B — branch-protection identity:** applied via the **operator's `gh` auth** at provision time (`gh api`), *not* the App. The operator is admin on the just-created repo; this still resolves #54 (which was about a *stored* full-scope PAT in squad pods, never interactive provisioning auth).
- **Maturity dial (design-pinned rename):** `squad_maturity` → `creation_maturity`; CLI `--squad-maturity` → `--creation-maturity`. **low** = ruleset requires 1 human approval **and** the green `ci` check before merge (auto-merge off). **high** = 0 required reviews, green `ci` check required, repo auto-merge enabled (merges itself once `ci` is green; no human). The LLM never decides a merge.
- **Required check convention:** the ruleset requires a status check context named **`ci`** (constant `_REQUIRED_CHECK_CONTEXT`). Product CI must publish a check named `ci` for merges (low) / auto-merge (high) to proceed. Documented in Task 8.
- **Token repo-scoping:** scope installation tokens to the product repo **by name** (GitHub's `repositories` token param accepts repo names). The name is known deterministically from the spec, so it is passed statically as `GITHUB_REPOSITORY=<owner>/<repo>` — no execute-time plumbing.
- **Copilot assignment mechanism:** bots are not REST-assignable. Assign via GraphQL: query `repository.suggestedActors(capabilities: [CAN_BE_ASSIGNED])` for the `Bot` with login `copilot-swe-agent`, then `replaceActorsForAssignable(input: {assignableId, actorIds: [botId]})`. If the actor is not found, **raise** (loud failure — Copilot must be enabled on the repo).

## Conventions (verified, must follow)

- `uv` workspace. Tests: `uv run pytest -q`. Lint: `uv run ruff check .` (**check only** — `ruff format` is intentionally NOT used; line-length 100; rules `E,F,I,UP,B`; compact multi-item lists are deliberate). Imports: `uv run lint-imports` (4 contracts). CI order: ruff → lint-imports → pytest. Bicep check: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK`.
- Every commit ends with the trailer `Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>`.
- No fakes/stubs in `src/`; deterministic doubles live in `testing/dsf_testing/`. New external I/O is injected (transport/clock/runner/reader) with a **real** default — same pattern as `GitHubAppClient.transport`/`clock` and `RealGitHubClient._run`.
- Commit directly to `main` (trunk-based, user-approved) after each task's gate is green.

## File map

| File | Change |
| --- | --- |
| `core/src/dsf/github_app_client.py` | Add `repositories` field + token-body wiring; add async `create_issue` + `assign_coding_agent`; small header/constant additions. |
| `core/tests/test_github_app_client.py` | Add tests for `repositories` scoping, `assign_coding_agent` (found + missing actor), `create_issue` (file + assign). |
| `core/src/dsf/container.py` | Add `github_repository` setting + `GITHUB_REPOSITORY` parse; add `_read_kv_secret` real reader + `_select_github_client` helper; wire `build_services` to use it. |
| `core/tests/test_container.py` | Add settings-parse test + `_select_github_client` App/fallback tests. |
| `core/pyproject.toml` | Add `azure-keyvault-secrets>=4.7` dependency. |
| `infra/main.bicep` | Add `githubRepository` param + `GITHUB_REPOSITORY` container env entry. |
| `cli/src/dsf/instance/provisioner.py` | `provision_azure` passes `githubRepository=`; replace `squad_governance` step with `branch_protection`; add `_apply_branch_protection`; swap imports; `squad_maturity`→`creation_maturity` description. |
| `cli/src/dsf/instance/spec.py` | Rename field + validator `squad_maturity`→`creation_maturity`. |
| `cli/src/dsf/cli/factory.py` | Rename `--squad-maturity`→`--creation-maturity` + `args.creation_maturity`. |
| `cli/src/dsf/instance/branch_protection.py` | **Create** — `RULESET_NAME`, `_REQUIRED_CHECK_CONTEXT`, `ruleset_payload`, `auto_merge_command`. |
| `cli/src/dsf/instance/squad_governance.py` | **Delete.** |
| `cli/tests/instance/test_squad_governance.py` | **Delete** (replaced by `test_branch_protection.py`). |
| `cli/tests/instance/test_branch_protection.py` | **Create** — ruleset payload + auto-merge + provisioner step tests. |
| `cli/tests/instance/test_spec.py` | Rename the 3 maturity tests. |
| `cli/tests/instance/test_factory.py` | Rename the 3 maturity tests + flags. |
| `cli/tests/instance/test_provisioner.py` | Update step-order list + replace the governance step test. |
| `docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md` | Task 8: reconcile to Fork A/B + `ci` convention. |

---

### Task 1: `GitHubAppClient` — repo name-scoping (`repositories` field)

**Files:**
- Modify: `core/src/dsf/github_app_client.py` (dataclass field + `installation_token` body)
- Test: `core/tests/test_github_app_client.py`

- [ ] **Step 1: Write the failing test**

Add to `core/tests/test_github_app_client.py`:

```python
def test_installation_token_scopes_by_repository_name():
    pem, _ = _rsa_pem()
    seen: dict[str, object] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = request.read()
        return httpx.Response(
            201,
            json={"token": "ghs_x", "expires_at": "2026-06-22T13:00:00Z"},
        )

    client = GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        repositories=["demo"],
        transport=httpx.MockTransport(handler),
        clock=_fixed_clock(datetime(2026, 6, 22, 12, 0, tzinfo=UTC)),
    )
    client.installation_token()
    import json
    assert json.loads(seen["body"]) == {"repositories": ["demo"]}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/test_github_app_client.py::test_installation_token_scopes_by_repository_name -q`
Expected: FAIL — `TypeError: GitHubAppClient.__init__() got an unexpected keyword argument 'repositories'`.

- [ ] **Step 3: Add the field and wire the token body**

In `core/src/dsf/github_app_client.py`, add the field after `repository_ids`:

```python
    repository_ids: list[int] | None = None
    repositories: list[str] | None = None
```

In `installation_token`, extend the body block:

```python
        body: dict[str, object] = {}
        if self.repository_ids:
            body["repository_ids"] = self.repository_ids
        if self.repositories:
            body["repositories"] = self.repositories
```

Update the class docstring line to mention name-scoping:

```python
    ``repository_ids`` / ``repositories`` (when set) scope minted tokens to exactly
    those repos (by numeric id or by name, respectively).
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest core/tests/test_github_app_client.py -q`
Expected: PASS (all existing + new).

- [ ] **Step 5: Commit**

```bash
git add core/src/dsf/github_app_client.py core/tests/test_github_app_client.py
git commit -m "feat(core): scope GitHub App installation tokens by repo name

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: `GitHubAppClient.assign_coding_agent` (GraphQL)

**Files:**
- Modify: `core/src/dsf/github_app_client.py` (new async method + a GraphQL constant)
- Test: `core/tests/test_github_app_client.py`

- [ ] **Step 1: Write the failing tests**

Add to `core/tests/test_github_app_client.py`:

```python
def _token_handler(extra):
    """MockTransport handler: serves the token mint + delegates other paths to ``extra``."""
    def handler(request: httpx.Request) -> httpx.Response:
        if request.url.path.endswith("/access_tokens"):
            return httpx.Response(
                201, json={"token": "ghs_x", "expires_at": "2026-06-22T13:00:00Z"}
            )
        return extra(request)
    return handler


def _app_client(handler):
    pem, _ = _rsa_pem()
    return GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        transport=httpx.MockTransport(handler),
        clock=_fixed_clock(datetime(2026, 6, 22, 12, 0, tzinfo=UTC)),
    )


async def test_assign_coding_agent_replaces_actors_with_copilot_bot():
    import json
    calls: list[dict] = []

    def extra(request: httpx.Request) -> httpx.Response:
        payload = json.loads(request.read())
        calls.append(payload)
        if "suggestedActors" in payload["query"]:
            return httpx.Response(200, json={
                "data": {"repository": {"suggestedActors": {"nodes": [
                    {"login": "someone-else", "__typename": "User", "id": "U_1"},
                    {"login": "copilot-swe-agent", "__typename": "Bot", "id": "BOT_42"},
                ]}}}
            })
        return httpx.Response(200, json={
            "data": {"replaceActorsForAssignable": {"assignable": {"id": "ISSUE_1"}}}
        })

    client = _app_client(_token_handler(extra))
    await client.assign_coding_agent("acme/demo", "ISSUE_1")

    assert calls[0]["variables"] == {"owner": "acme", "name": "demo"}
    assert calls[1]["variables"] == {"assignableId": "ISSUE_1", "actorIds": ["BOT_42"]}


async def test_assign_coding_agent_raises_when_copilot_not_assignable():
    def extra(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={
            "data": {"repository": {"suggestedActors": {"nodes": [
                {"login": "someone-else", "__typename": "User", "id": "U_1"},
            ]}}}
        })

    client = _app_client(_token_handler(extra))
    with pytest.raises(RuntimeError, match="copilot-swe-agent"):
        await client.assign_coding_agent("acme/demo", "ISSUE_1")
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest core/tests/test_github_app_client.py::test_assign_coding_agent_replaces_actors_with_copilot_bot -q`
Expected: FAIL — `AttributeError: 'GitHubAppClient' object has no attribute 'assign_coding_agent'`.

- [ ] **Step 3: Implement `assign_coding_agent`**

In `core/src/dsf/github_app_client.py`, add module constants near the top (after `_REFRESH_SKEW`):

```python
_COPILOT_LOGIN = "copilot-swe-agent"
_SUGGESTED_ACTORS_QUERY = (
    "query($owner:String!,$name:String!){"
    "repository(owner:$owner,name:$name){"
    "suggestedActors(capabilities:[CAN_BE_ASSIGNED],first:100){"
    "nodes{login __typename ... on Bot{id} ... on User{id}}}}}"
)
_REPLACE_ACTORS_MUTATION = (
    "mutation($assignableId:ID!,$actorIds:[ID!]!){"
    "replaceActorsForAssignable(input:{assignableId:$assignableId,actorIds:$actorIds}){"
    "assignable{__typename}}}"
)
```

Add the method to the `GitHubAppClient` class (after `installation_token`):

```python
    def _token_headers(self, token: str) -> dict[str, str]:
        return {
            "Authorization": f"Bearer {token}",
            "Accept": "application/vnd.github+json",
        }

    async def _graphql(
        self, client: httpx.AsyncClient, token: str, query: str, variables: dict
    ) -> dict:
        resp = await client.post(
            "/graphql",
            headers=self._token_headers(token),
            json={"query": query, "variables": variables},
        )
        resp.raise_for_status()
        data = resp.json()
        if data.get("errors"):
            raise RuntimeError(f"GraphQL error: {data['errors']}")
        return data["data"]

    async def assign_coding_agent(self, repo: str, issue_node_id: str) -> None:
        """Assign the Copilot coding agent (``copilot-swe-agent``) to an issue.

        Bots are not REST-assignable, so this uses GraphQL: resolve the bot's node
        id from the repo's suggested actors, then ``replaceActorsForAssignable``.
        Raises ``RuntimeError`` if Copilot is not an assignable actor on ``repo``.
        """
        owner, _, name = repo.partition("/")
        token = self.installation_token()
        async with httpx.AsyncClient(transport=self.transport, base_url=_GITHUB_API) as client:
            data = await self._graphql(
                client, token, _SUGGESTED_ACTORS_QUERY, {"owner": owner, "name": name}
            )
            nodes = data["repository"]["suggestedActors"]["nodes"]
            bot_id = next(
                (n["id"] for n in nodes if n.get("login") == _COPILOT_LOGIN), None
            )
            if bot_id is None:
                raise RuntimeError(
                    f"{_COPILOT_LOGIN} is not an assignable actor on {repo}; "
                    "ensure GitHub Copilot coding agent is enabled for the repo"
                )
            await self._graphql(
                client,
                token,
                _REPLACE_ACTORS_MUTATION,
                {"assignableId": issue_node_id, "actorIds": [bot_id]},
            )
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest core/tests/test_github_app_client.py -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add core/src/dsf/github_app_client.py core/tests/test_github_app_client.py
git commit -m "feat(core): assign Copilot coding agent to issues via GraphQL

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: `GitHubAppClient.create_issue` (REST file + assign)

**Files:**
- Modify: `core/src/dsf/github_app_client.py` (new async method satisfying the `GitHubClient` port)
- Test: `core/tests/test_github_app_client.py`

- [ ] **Step 1: Write the failing test**

Add to `core/tests/test_github_app_client.py`:

```python
async def test_create_issue_files_then_assigns_and_returns_url():
    import json
    seen: dict[str, object] = {}

    def extra(request: httpx.Request) -> httpx.Response:
        if request.url.path == "/repos/acme/demo/issues":
            seen["issue_body"] = json.loads(request.read())
            return httpx.Response(201, json={
                "html_url": "https://github.com/acme/demo/issues/7",
                "node_id": "ISSUE_NODE_7",
            })
        payload = json.loads(request.read())
        if "suggestedActors" in payload["query"]:
            return httpx.Response(200, json={
                "data": {"repository": {"suggestedActors": {"nodes": [
                    {"login": "copilot-swe-agent", "__typename": "Bot", "id": "BOT_42"},
                ]}}}
            })
        seen["assign_vars"] = payload["variables"]
        return httpx.Response(200, json={
            "data": {"replaceActorsForAssignable": {"assignable": {"id": "ISSUE_NODE_7"}}}
        })

    client = _app_client(_token_handler(extra))
    url = await client.create_issue("acme/demo", "Title", "Body", ["enhancement"])

    assert url == "https://github.com/acme/demo/issues/7"
    assert seen["issue_body"] == {"title": "Title", "body": "Body", "labels": ["enhancement"]}
    assert seen["assign_vars"] == {"assignableId": "ISSUE_NODE_7", "actorIds": ["BOT_42"]}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/test_github_app_client.py::test_create_issue_files_then_assigns_and_returns_url -q`
Expected: FAIL — `AttributeError: ... has no attribute 'create_issue'`.

- [ ] **Step 3: Implement `create_issue`**

In `core/src/dsf/github_app_client.py`, add to the `GitHubAppClient` class (after `assign_coding_agent`):

```python
    async def create_issue(
        self, repo: str, title: str, body: str, labels: list[str]
    ) -> str:
        """File an issue and hand it to the Copilot coding agent; return its URL.

        Files via REST, captures the issue node id, then assigns the coding agent
        (Feature Council's output is a build request for the agent). Satisfies the
        :class:`dsf.ports.GitHubClient` port.
        """
        token = self.installation_token()
        async with httpx.AsyncClient(transport=self.transport, base_url=_GITHUB_API) as client:
            resp = await client.post(
                f"/repos/{repo}/issues",
                headers=self._token_headers(token),
                json={"title": title, "body": body, "labels": labels},
            )
            resp.raise_for_status()
            data = resp.json()
        await self.assign_coding_agent(repo, data["node_id"])
        return data["html_url"]
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest core/tests/test_github_app_client.py -q`
Expected: PASS.

- [ ] **Step 5: Verify the port is satisfied (typing sanity)**

Run: `uv run python -c "from dsf.github_app_client import GitHubAppClient; from dsf.ports import GitHubClient; import inspect; print('create_issue' in dir(GitHubAppClient))"`
Expected: prints `True`.

- [ ] **Step 6: Commit**

```bash
git add core/src/dsf/github_app_client.py core/tests/test_github_app_client.py
git commit -m "feat(core): GitHubAppClient.create_issue files and assigns coding agent

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: `build_services` — App-backed GitHub client + repo env

**Files:**
- Modify: `core/pyproject.toml` (add `azure-keyvault-secrets`)
- Modify: `core/src/dsf/container.py` (setting + env parse, `_read_kv_secret`, `_select_github_client`, wire `build_services`)
- Test: `core/tests/test_container.py`

- [ ] **Step 1: Write the failing tests**

Add to `core/tests/test_container.py`:

```python
def test_settings_parse_github_repository_env():
    from dsf.container import AzureRuntimeSettings

    settings = AzureRuntimeSettings.from_env(
        {"DSF_PRODUCT": "demo", "GITHUB_REPOSITORY": "acme/demo"}
    )
    assert settings.github_repository == "acme/demo"


def test_select_github_client_uses_app_when_configured():
    from dsf.container import AzureRuntimeSettings, _select_github_client
    from dsf.github_app_client import GitHubAppClient

    settings = AzureRuntimeSettings(
        product="demo",
        keyvault_uri="https://kv.example",
        github_app_id="42",
        github_installation_id="9001",
        github_app_private_key_secret="github-app-private-key",
        github_repository="acme/demo",
    )
    client = _select_github_client(settings, key_reader=lambda uri, name: "PEM")

    assert isinstance(client, GitHubAppClient)
    assert client.app_id == "42"
    assert client.installation_id == "9001"
    assert client.private_key_pem == "PEM"
    assert client.repositories == ["demo"]


def test_select_github_client_falls_back_without_app():
    from dsf.container import AzureRuntimeSettings, _select_github_client
    from dsf.github_client import RealGitHubClient

    settings = AzureRuntimeSettings(product="demo")
    client = _select_github_client(settings, key_reader=lambda uri, name: "PEM")

    assert isinstance(client, RealGitHubClient)
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest core/tests/test_container.py::test_select_github_client_uses_app_when_configured -q`
Expected: FAIL — `ImportError: cannot import name '_select_github_client'`.

- [ ] **Step 3: Add the dependency**

In `core/pyproject.toml`, add to `dependencies` (alongside the other `azure-*` entries):

```toml
    "azure-keyvault-secrets>=4.7",
```

Run: `uv sync --all-packages`
Expected: resolves and installs `azure-keyvault-secrets`.

- [ ] **Step 4: Add the setting + env parse**

In `core/src/dsf/container.py`, add the field to `AzureRuntimeSettings` (after `github_app_private_key_secret`):

```python
    github_app_private_key_secret: str = ""
    github_repository: str = ""
```

In `AzureRuntimeSettings.from_env`, add to the returned `cls(...)` kwargs (after the `github_app_private_key_secret=...` line):

```python
            github_repository=(env.get("GITHUB_REPOSITORY") or "").strip(),
```

- [ ] **Step 5: Add the KV reader + selector helper**

In `core/src/dsf/container.py`, add these module-level functions (place them above `build_services`):

```python
def _read_kv_secret(keyvault_uri: str, secret_name: str) -> str:
    """Read a Key Vault secret's value (real adapter; deferred Azure import)."""
    from azure.identity import DefaultAzureCredential
    from azure.keyvault.secrets import SecretClient

    client = SecretClient(vault_url=keyvault_uri, credential=DefaultAzureCredential())
    return client.get_secret(secret_name).value


def _select_github_client(
    settings: AzureRuntimeSettings,
    *,
    key_reader: Callable[[str, str], str] = _read_kv_secret,
) -> GitHubClient:
    """Return the App-backed client when the App is fully configured, else gh fallback.

    App path (Fork A preference): app id + installation id + Key Vault uri + secret
    name all set. The private key is read from Key Vault and the minted tokens are
    scoped to the single product repo by name (``GITHUB_REPOSITORY`` -> repo name).
    Otherwise falls back to the gh-CLI ``RealGitHubClient`` (local/dev, no App).
    """
    app_configured = (
        settings.github_app_id
        and settings.github_installation_id
        and settings.keyvault_uri
        and settings.github_app_private_key_secret
    )
    if app_configured:
        from dsf.github_app_client import GitHubAppClient

        repo_name = settings.github_repository.split("/")[-1]
        return GitHubAppClient(
            app_id=settings.github_app_id,
            installation_id=settings.github_installation_id,
            private_key_pem=key_reader(
                settings.keyvault_uri, settings.github_app_private_key_secret
            ),
            repositories=[repo_name] if repo_name else None,
        )

    from dsf.github_client import RealGitHubClient

    return RealGitHubClient()
```

Add the `Callable` import at the top of the file (with the other `collections.abc` imports):

```python
from collections.abc import Callable, Mapping
```

(If the file already imports `Mapping` from `collections.abc`, extend that line to include `Callable`. If it imports `Mapping` from `typing`, add a separate `from collections.abc import Callable` line.)

- [ ] **Step 6: Wire `build_services`**

In `core/src/dsf/container.py` `build_services`, remove the local `from dsf.github_client import RealGitHubClient` import line, and replace the `github=RealGitHubClient(),` line in the `Services(...)` constructor with:

```python
        github=_select_github_client(settings),
```

- [ ] **Step 7: Run tests to verify they pass**

Run: `uv run pytest core/tests/test_container.py -q`
Expected: PASS (existing error-path tests + new selector/settings tests).

- [ ] **Step 8: Commit**

```bash
git add core/pyproject.toml core/src/dsf/container.py core/tests/test_container.py uv.lock
git commit -m "feat(core): wire App-backed GitHub client in build_services with repo scoping

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Runtime repo-scoping env (Bicep + provisioner)

**Files:**
- Modify: `infra/main.bicep` (param + env entry)
- Modify: `cli/src/dsf/instance/provisioner.py` (`provision_azure` command)
- Test: `cli/tests/instance/test_provisioner.py`

- [ ] **Step 1: Write the failing test**

Add to `cli/tests/instance/test_provisioner.py`:

```python
def test_provision_azure_passes_github_repository_param():
    spec = _spec()
    plan = InstanceProvisioner(spec).plan()
    step = next(s for s in plan.steps if s.name == "provision_azure")
    assert f"githubRepository={spec.github_repo()}" in step.command
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_provisioner.py::test_provision_azure_passes_github_repository_param -q`
Expected: FAIL — assertion error (`githubRepository=...` not in command).

- [ ] **Step 3: Pass the param from the provisioner**

In `cli/src/dsf/instance/provisioner.py`, in the `provision_azure` `ProvisionStep.command` list, add after the `f"githubInstallationId={self._github_installation_id}",` line:

```python
                    f"githubRepository={s.github_repo()}",
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_provisioner.py::test_provision_azure_passes_github_repository_param -q`
Expected: PASS.

- [ ] **Step 5: Add the Bicep param + env entry**

In `infra/main.bicep`, add a param after `githubInstallationId` (around line 42):

```bicep
@description('Product repository in owner/name form; scopes App tokens to the single repo.')
param githubRepository string = ''
```

In the container `env` array (after the `GITHUB_INSTALLATION_ID` entry, around line 396):

```bicep
            { name: 'GITHUB_REPOSITORY', value: githubRepository }
```

- [ ] **Step 6: Verify Bicep compiles**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK`
Expected: prints `BICEP_OK`.

- [ ] **Step 7: Commit**

```bash
git add infra/main.bicep cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat: pass GITHUB_REPOSITORY to runtime for App token repo-scoping

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Rename `squad_maturity` → `creation_maturity`

**Files:**
- Modify: `cli/src/dsf/instance/spec.py` (field + validator)
- Modify: `cli/src/dsf/cli/factory.py` (CLI flag + arg read)
- Modify: `cli/src/dsf/instance/provisioner.py` (step description reference)
- Test: `cli/tests/instance/test_spec.py`, `cli/tests/instance/test_factory.py`

> Note: `squad_maturity` is provision-time only — no Bicep/runtime references. The
> `squad_governance` provisioner step that *consumes* the dial is replaced in Task 7;
> here we only rename the field and its current reference in the step description.

- [ ] **Step 1: Update the spec tests (rename) — write the failing tests**

In `cli/tests/instance/test_spec.py`, replace the three maturity tests:

```python
def test_creation_maturity_defaults_to_low():
    spec = InstanceSpec(product="demo", owner="acme")
    assert spec.creation_maturity == "low"


def test_creation_maturity_accepts_high():
    spec = InstanceSpec(product="demo", owner="acme", creation_maturity="high")
    assert spec.creation_maturity == "high"
```

and the rejection test (around line 142):

```python
def test_creation_maturity_rejects_unknown_value():
    with pytest.raises(ValidationError):
        InstanceSpec(product="demo", owner="acme", creation_maturity="medium")
```

- [ ] **Step 2: Run to verify they fail**

Run: `uv run pytest cli/tests/instance/test_spec.py -k creation_maturity -q`
Expected: FAIL — `creation_maturity` is not a field yet.

- [ ] **Step 3: Rename in the spec**

In `cli/src/dsf/instance/spec.py`, rename the field (line ~39):

```python
    creation_maturity: str = "low"
```

and the validator (lines ~76-80):

```python
    @field_validator("creation_maturity")
    @classmethod
    def _validate_creation_maturity(cls, value: str) -> str:
        if value not in {"low", "high"}:
            raise ValueError(f"creation_maturity must be 'low' or 'high', got {value!r}")
        return value
```

- [ ] **Step 4: Run to verify spec tests pass**

Run: `uv run pytest cli/tests/instance/test_spec.py -k creation_maturity -q`
Expected: PASS.

- [ ] **Step 5: Update the factory CLI tests (rename) — write failing tests**

In `cli/tests/instance/test_factory.py` (or `cli/tests/cli/test_factory.py` — the file that currently contains them), update the three references:

```python
    assert args.creation_maturity == "low"
```

```python
def test_new_creation_maturity_high_flows_into_manifest(tmp_path):
    ...
        "--name-prefix", "demopfx", "--creation-maturity", "high",
    ...
    assert read_manifest("demo", repo_root=tmp_path).spec.creation_maturity == "high"
```

```python
def test_new_rejects_unknown_creation_maturity():
    ...
        "--name-prefix", "demopfx", "--creation-maturity", "wild",
    ...
```

- [ ] **Step 6: Run to verify they fail**

Run: `uv run pytest cli/tests/cli/test_factory.py -k creation_maturity -q`
Expected: FAIL — argparse has no `--creation-maturity`.

- [ ] **Step 7: Rename the CLI flag + arg read**

In `cli/src/dsf/cli/factory.py`, line ~276:

```python
        "--creation-maturity",
```

and line ~115:

```python
        creation_maturity=args.creation_maturity,
```

(argparse maps `--creation-maturity` to `args.creation_maturity` automatically.)

In `cli/src/dsf/instance/provisioner.py`, the `squad_governance` step description still reads `s.squad_maturity`. Update it for now (the whole step is rewritten in Task 7, but keep the tree green between tasks):

```python
                    f"Apply the '{s.creation_maturity}' creation maturity dial to "
```

- [ ] **Step 8: Run the full CLI suite**

Run: `uv run pytest cli/tests -q`
Expected: PASS except `test_squad_governance.py` (which still constructs `InstanceSpec(..., squad_maturity=...)`) — that file is **deleted** in Task 7. To keep this task green standalone, also update its two `_spec` calls now is unnecessary; instead run the suite excluding it:

Run: `uv run pytest cli/tests --ignore=cli/tests/instance/test_squad_governance.py -q`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add cli/src/dsf/instance/spec.py cli/src/dsf/cli/factory.py cli/src/dsf/instance/provisioner.py cli/tests/instance/test_spec.py cli/tests/cli/test_factory.py
git commit -m "refactor: rename squad_maturity -> creation_maturity (dial means repo controls)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Branch-protection ruleset step (replaces `squad_governance`)

**Files:**
- Create: `cli/src/dsf/instance/branch_protection.py`
- Delete: `cli/src/dsf/instance/squad_governance.py`, `cli/tests/instance/test_squad_governance.py`
- Modify: `cli/src/dsf/instance/provisioner.py` (imports, step, execute branch, `_apply_branch_protection`)
- Test: `cli/tests/instance/test_branch_protection.py` (create), `cli/tests/instance/test_provisioner.py` (step order + step test)

- [ ] **Step 1: Write the failing payload/command tests**

Create `cli/tests/instance/test_branch_protection.py`:

```python
"""Tests for the branch-protection ruleset builders."""

from __future__ import annotations

from dsf.instance.branch_protection import (
    RULESET_NAME,
    auto_merge_command,
    ruleset_payload,
)
from dsf.instance.spec import InstanceSpec


def _spec(maturity: str) -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme", creation_maturity=maturity)


def _rule(payload: dict, rule_type: str) -> dict:
    return next(r for r in payload["rules"] if r["type"] == rule_type)


def test_ruleset_targets_default_branch_and_requires_ci():
    payload = ruleset_payload(_spec("low"))
    assert payload["name"] == RULESET_NAME
    assert payload["target"] == "branch"
    assert payload["enforcement"] == "active"
    assert payload["conditions"]["ref_name"]["include"] == ["~DEFAULT_BRANCH"]
    checks = _rule(payload, "required_status_checks")["parameters"]["required_status_checks"]
    assert checks == [{"context": "ci"}]


def test_low_requires_one_review():
    params = _rule(ruleset_payload(_spec("low")), "pull_request")["parameters"]
    assert params["required_approving_review_count"] == 1


def test_high_requires_zero_reviews():
    params = _rule(ruleset_payload(_spec("high")), "pull_request")["parameters"]
    assert params["required_approving_review_count"] == 0


def test_auto_merge_command_enabled_only_for_high():
    assert auto_merge_command(_spec("low")) == [
        "gh", "api", "--method", "PATCH", "repos/acme/demo",
        "-F", "allow_auto_merge=false",
    ]
    assert auto_merge_command(_spec("high")) == [
        "gh", "api", "--method", "PATCH", "repos/acme/demo",
        "-F", "allow_auto_merge=true",
    ]
```

- [ ] **Step 2: Run to verify it fails**

Run: `uv run pytest cli/tests/instance/test_branch_protection.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'dsf.instance.branch_protection'`.

- [ ] **Step 3: Create the builder module**

Create `cli/src/dsf/instance/branch_protection.py`:

```python
"""Branch-protection ruleset: map the creation-maturity dial to a real ruleset.

This replaces the old ``allow_auto_merge``-only no-op (issue #54). The ruleset is
applied with the operator's interactive ``gh`` auth at provision time (the operator
is admin on the freshly created repo), so the DSF App needs no
``administration:write`` scope.

The dial governs repo controls, not any agent's behaviour:

- ``low``  — require 1 human approval **and** the green ``ci`` check; auto-merge off.
- ``high`` — require 0 reviews but still the green ``ci`` check; repo auto-merge on,
  so a PR merges itself once ``ci`` is green (no human).

``ci`` is a DSF convention: the product CI must publish a status check named ``ci``
for merges (low) / auto-merge (high) to proceed.
"""

from __future__ import annotations

from dsf.instance.spec import InstanceSpec

RULESET_NAME = "dsf-creation"
_REQUIRED_CHECK_CONTEXT = "ci"


def ruleset_payload(spec: InstanceSpec) -> dict:
    """Build the repo ruleset body for ``spec.creation_maturity``."""
    reviews = 0 if spec.creation_maturity == "high" else 1
    return {
        "name": RULESET_NAME,
        "target": "branch",
        "enforcement": "active",
        "conditions": {"ref_name": {"include": ["~DEFAULT_BRANCH"], "exclude": []}},
        "rules": [
            {
                "type": "pull_request",
                "parameters": {
                    "required_approving_review_count": reviews,
                    "dismiss_stale_reviews_on_push": True,
                    "require_code_owner_review": False,
                    "require_last_push_approval": False,
                    "required_review_thread_resolution": False,
                },
            },
            {
                "type": "required_status_checks",
                "parameters": {
                    "strict_required_status_checks_policy": True,
                    "required_status_checks": [{"context": _REQUIRED_CHECK_CONTEXT}],
                },
            },
        ],
    }


def auto_merge_command(spec: InstanceSpec) -> list[str]:
    """Return the ``gh`` command toggling repo auto-merge for the dial."""
    enabled = "true" if spec.creation_maturity == "high" else "false"
    return [
        "gh", "api", "--method", "PATCH", f"repos/{spec.github_repo()}",
        "-F", f"allow_auto_merge={enabled}",
    ]
```

- [ ] **Step 4: Run to verify builder tests pass**

Run: `uv run pytest cli/tests/instance/test_branch_protection.py -q`
Expected: PASS.

- [ ] **Step 5: Write the failing provisioner step tests**

In `cli/tests/instance/test_provisioner.py`, replace `test_squad_governance_low_maturity_disables_auto_merge` (lines ~576-583) with:

```python
def test_branch_protection_step_present_and_has_no_static_commands():
    spec = InstanceSpec(product="demo", owner="acme", creation_maturity="low")
    plan = InstanceProvisioner(spec).plan()
    step = next(s for s in plan.steps if s.name == "branch_protection")
    assert step.commands == []
    assert step.command == []


def test_branch_protection_dry_run_records_plan(tmp_path):
    spec = InstanceSpec(product="demo", owner="acme", creation_maturity="high")
    manifest = InstanceProvisioner(spec, repo_root=tmp_path).apply(execute=False)
    step = next(s for s in manifest.plan.steps if s.name == "branch_protection")
    assert step.result == "ruleset planned (dry-run)"
    assert step.executed is False


def test_branch_protection_execute_creates_ruleset_and_sets_auto_merge():
    import json
    calls: list[dict] = []

    def fake_run(cmd, **kwargs):
        calls.append({"cmd": cmd, "input": kwargs.get("input")})
        # The ruleset lookup is the only call carrying ``--jq``; empty stdout -> POST.
        if "--jq" in cmd:
            return _completed(stdout="")
        return _completed(stdout="")

    provisioner = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme", creation_maturity="high"),
        run=fake_run,
    )
    provisioner._apply_branch_protection()

    api_calls = [c for c in calls if c["cmd"][:2] == ["gh", "api"]]
    # 1) lookup existing ruleset id, 2) POST create with JSON on stdin, 3) PATCH auto-merge
    assert api_calls[1]["cmd"][:5] == [
        "gh", "api", "--method", "POST", "/repos/acme/demo/rulesets"
    ]
    assert api_calls[1]["cmd"][-2:] == ["--input", "-"]
    assert json.loads(api_calls[1]["input"])["name"] == "dsf-creation"
    assert api_calls[2]["cmd"] == [
        "gh", "api", "--method", "PATCH", "repos/acme/demo",
        "-F", "allow_auto_merge=true",
    ]


def test_branch_protection_execute_updates_existing_ruleset():
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if "--jq" in cmd:
            return _completed(stdout="123\n")
        return _completed(stdout="")

    provisioner = InstanceProvisioner(
        InstanceSpec(product="demo", owner="acme", creation_maturity="low"),
        run=fake_run,
    )
    provisioner._apply_branch_protection()

    methods = [c for c in calls if c[:2] == ["gh", "api"] and "--method" in c]
    assert methods[0][:5] == ["gh", "api", "--method", "PUT", "/repos/acme/demo/rulesets/123"]
```

Add a `_completed` helper near the top of `test_provisioner.py` (the existing tests return `MagicMock`, which has no real `.stdout` string — the lookup needs a real one):

```python
def _completed(stdout="", returncode=0):
    import subprocess
    return subprocess.CompletedProcess(args=[], returncode=returncode, stdout=stdout, stderr="")
```

Also update the step-order assertion in `test_plan_step_order_and_names` (line ~89): replace `"squad_governance",` with `"branch_protection",`.

- [ ] **Step 6: Run to verify they fail**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "branch_protection or step_order" -q`
Expected: FAIL — no `branch_protection` step / no `_apply_branch_protection`.

- [ ] **Step 7: Rewire the provisioner**

In `cli/src/dsf/instance/provisioner.py`:

(a) Replace the import line `from dsf.instance.squad_governance import governance_commands` with:

```python
from dsf.instance.branch_protection import RULESET_NAME, auto_merge_command, ruleset_payload
```

(b) Replace the entire `squad_governance` `ProvisionStep(...)` block with:

```python
            ProvisionStep(
                name="branch_protection",
                description=(
                    f"Apply the '{s.creation_maturity}' creation maturity dial to "
                    f"{s.github_repo()} as a branch-protection ruleset (required "
                    "reviews + green 'ci' check)"
                ),
            ),
```

(c) In `_execute_step`, add a branch (place it next to the other named branches, e.g. after the `deploy_council` branch):

```python
        elif step.name == "branch_protection":
            if not execute:
                step.result = "ruleset planned (dry-run)"
            else:
                self._apply_branch_protection()
                step.executed, step.result = True, "applied"
```

(d) Add the method (near `_install_app`):

```python
    def _apply_branch_protection(self) -> None:
        """Apply the creation-maturity dial as a real branch-protection ruleset.

        Uses the operator's interactive ``gh`` auth (admin on the just-created repo),
        not the App, so the App needs no ``administration:write`` scope. Idempotent:
        updates the existing ``dsf-creation`` ruleset in place when present. The
        ruleset JSON is passed on stdin (``gh api --input -``) — no temp files.
        """
        repo = self.spec.github_repo()
        payload = json.dumps(ruleset_payload(self.spec))
        lookup = self._run(
            [
                "gh", "api", f"/repos/{repo}/rulesets", "--jq",
                f'[.[] | select(.name=="{RULESET_NAME}") | .id] | first // empty',
            ],
            check=True, capture_output=True, text=True,
        )
        ruleset_id = (getattr(lookup, "stdout", "") or "").strip()
        if ruleset_id:
            self._run(
                ["gh", "api", "--method", "PUT",
                 f"/repos/{repo}/rulesets/{ruleset_id}", "--input", "-"],
                input=payload, text=True, check=True,
            )
        else:
            self._run(
                ["gh", "api", "--method", "POST",
                 f"/repos/{repo}/rulesets", "--input", "-"],
                input=payload, text=True, check=True,
            )
        self._run(auto_merge_command(self.spec), check=True)
```

(`json` is already imported in `provisioner.py`.)

- [ ] **Step 8: Delete the retired module + its test**

```bash
git rm cli/src/dsf/instance/squad_governance.py cli/tests/instance/test_squad_governance.py
```

- [ ] **Step 9: Run the CLI suite + import-linter**

Run: `uv run pytest cli/tests -q && uv run lint-imports`
Expected: PASS; lint-imports `4 contracts kept`.

- [ ] **Step 10: Commit**

```bash
git add cli/src/dsf/instance/branch_protection.py cli/src/dsf/instance/provisioner.py cli/tests/instance/test_branch_protection.py cli/tests/instance/test_provisioner.py
git commit -m "feat: apply branch-protection ruleset for creation maturity (closes #54)

Replace the squad_governance allow_auto_merge no-op with a real repo ruleset
(required reviews + green 'ci' check) applied via the operator's gh auth.

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Doc reconciliation + full-suite gate + self-review

**Files:**
- Modify: `docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md`

- [ ] **Step 1: Reconcile the design doc to the locked decisions**

In the design doc, update the two spots that say the ruleset is applied "via the App" and the filing model, so the spec matches what shipped:

- In **Reflection loop + deterministic merge gates**, change "the provisioner sets a **real** branch-protection ruleset instead of toggling `allow_auto_merge`" to note it is applied **via the operator's `gh` auth at provision time** (the App needs no `administration:write`), and add: "Both dials require a green status check named **`ci`** — a DSF convention the product CI must publish."
- In **Provisioning changes**, change the "Branch-protection ruleset step ... via the App" bullet to "... via the operator's interactive `gh` auth (admin on the new repo)".
- In the decomposition table / handoff text, note that runtime filing is **App-preferred with a gh-CLI fallback** (the fallback files only; the App path also assigns the Coding Agent).

- [ ] **Step 2: Run the full gate**

Run each and confirm green:

```bash
uv run ruff check .
uv run lint-imports
uv run pytest -q
az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK
uv run python -m dsf.evals.runner --gate
```

Expected: ruff `All checks passed!`; lint-imports `4 contracts kept`; pytest all pass (new total = prior 544 + the tests added here); `BICEP_OK`; evals gate passes.

- [ ] **Step 3: Self-review (mirror Stage 2 Task 8)**

Confirm by inspection:
- No fakes/stubs added to any `src/` (only `_read_kv_secret`/`_select_github_client` real defaults + injected test readers).
- `GitHubAppClient` private key still `repr=False`; never logged.
- The App path assigns the Coding Agent; the gh fallback files only (documented).
- The ruleset is applied via operator `gh` (no App `administration` scope); JSON passed on stdin (no temp files).
- `squad_*` names are gone from `cli/src` (run `git grep -n "squad_maturity\|squad_governance\|governance_commands" cli/src` → no hits).

- [ ] **Step 4: Commit the doc reconciliation**

```bash
git add docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md
git commit -m "docs: reconcile #71 design to operator-auth rulesets + ci convention + gh fallback

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Whole-stage review + push (after all tasks)

- [ ] Dispatch a `code-review` agent over the full Stage 3 range (`git log origin/main..HEAD`), apply the `receiving-code-review` skill to triage findings, fix anything real (TDD), re-run the full gate.
- [ ] Push: `git push origin main`.
- [ ] Open a follow-up GitHub issue (already agreed): "Reassess what gets committed to namespaced memory and when" — worth deliberate design + surfacing to end users in docs.

## Self-review of this plan (writing-plans checklist)

- **Spec coverage:** Decomposition row 3 = "S7 file + assign-to-Coding-Agent under the App (Tasks 1-4), deterministic branch-protection rulesets (Tasks 6-7), closes #54 (Task 7)." Repo-scoping infra (Task 5) supports the App tokens. Doc reconciliation (Task 8). The advisory-review method is correctly deferred to Stage 6 (reflection) — Stage 3 adds only the action methods it consumes (`create_issue`, `assign_coding_agent`).
- **Placeholders:** none — every code/test step shows full content.
- **Type consistency:** `creation_maturity` (str), `ruleset_payload(spec)->dict`, `auto_merge_command(spec)->list[str]`, `RULESET_NAME`/`_REQUIRED_CHECK_CONTEXT` constants, `GitHubAppClient.create_issue(repo,title,body,labels)->str` / `assign_coding_agent(repo,issue_node_id)->None` / `repositories: list[str]|None`, `_select_github_client(settings,*,key_reader)->GitHubClient`, `_read_kv_secret(uri,name)->str`, `github_repository` setting + `GITHUB_REPOSITORY` env + `githubRepository` Bicep param — all consistent across tasks.
