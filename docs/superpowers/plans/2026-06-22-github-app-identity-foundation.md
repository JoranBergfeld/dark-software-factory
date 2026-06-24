# Stage 2 — GitHub App Identity Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up the DSF GitHub App as the governed, PAT-free identity for DSF orchestration — a real token-minting `GitHubAppClient` in `core`, a one-time interactive App bootstrap (`dsf bootstrap`) that stores the master credentials in an owner-level Key Vault, and `dsf new` steps that add each product repo to the owner installation and seed the App private key into the product Key Vault.

**Architecture:** A single owner-level DSF GitHub App is created **once** via GitHub's App-manifest flow and installed **once** on the owner account; its App id + private key + installation id live in a dedicated **owner Key Vault**. Each `dsf new` reads those from the owner KV, adds the new product repo to that single installation (API, no browser), seeds the App private key into the product KV, and wires the App id + installation id into the ACA runtime env. At runtime the App client signs a short-lived RS256 JWT with the private key and exchanges it for an installation token **scoped to just the product repo** — least-privilege, ephemeral, centrally revocable. This stage ships the token-minting core + the full provisioning/bootstrap surface; the four orchestration *actions* (file issue, assign, advisory review, branch-protection) are wired into S7 in **Stage 3** (deliberate YAGNI boundary).

**Tech Stack:** Python 3.12, `uv` workspace, pydantic v2, `httpx` (already a `core` dep), **`pyjwt[crypto]`** (new `core` dep — RS256 signing needs it), Azure CLI (`az keyvault`/`az group`), GitHub CLI (`gh api`), Bicep. Tests: `pytest` with `httpx.MockTransport` + injected clock/runner — no live GitHub/Azure calls (ADR 0014 real-only `src/`; deterministic doubles in `dsf_testing`).

**Decisions locked with the user (2026-06-22):**
1. **Bootstrap = full App-manifest flow** (browser + local callback server), per the design doc.
2. **Installation model = one owner-level installation.** `dsf new` adds each product repo to it via API and records that (shared) installation id on the manifest; the runtime mints **repo-scoped** tokens. No per-product browser consent.
3. **Master credentials live in a dedicated owner-level Azure Key Vault** (separate from per-product vaults). `dsf new` reads the App private key from it to seed each product vault. The owner KV URI is a **non-secret pointer** supplied to `dsf new` via `DSF_OWNER_KEYVAULT_URI` (or `--owner-keyvault-uri`); no local credential file is written.

**Refines the design doc** (`docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md` lines 63-83, 164-190): the doc said "install the App on each product repo"; decision 2 refines that to one owner installation + per-product repo-add. A doc touch-up is **Task 8** so the approved spec is not left stale. Branch-protection rulesets + S7 file/assign remain **Stage 3**; this stage does not touch `squad_governance` or S7.

---

## File Structure

**Create:**
- `core/src/dsf/github_app_client.py` — `GitHubAppClient`: RS256 App JWT → installation access token, cached by expiry, repo-scoped. The one reusable identity primitive.
- `core/tests/test_github_app_client.py` — token-minting units (MockTransport + fixed clock + real RSA keypair).
- `cli/src/dsf/instance/app_bootstrap.py` — the `dsf bootstrap` engine: App-manifest generation, `code`→credentials exchange, installation discovery, owner-KV ensure/store command builders, and the interactive driver.
- `cli/tests/instance/test_app_bootstrap.py` — units for every non-interactive seam.

**Modify:**
- `core/pyproject.toml` — add `pyjwt[crypto]>=2.8` to `dependencies`.
- `cli/src/dsf/instance/spec.py` — add `GitHubAppBinding` model + `InstanceManifest.github_app`.
- `cli/src/dsf/instance/provisioner.py` — constructor params (`owner_keyvault_uri`, `github_app_id`, `github_installation_id`); `install_app` + `seed_app_key` steps + their `_execute_step` branches + helper methods; `provision_azure` Bicep params; thread `github_app` onto the manifest.
- `cli/tests/instance/test_provisioner.py` — plan/command-batch + binding assertions for the new steps.
- `cli/src/dsf/cli/factory.py` — `dsf bootstrap` subcommand; `_cmd_new` reads owner-KV pointer + passes the three new params to the provisioner.
- `cli/tests/cli/test_factory.py` — bootstrap subcommand wiring + `_cmd_new` owner-KV plumbing (existing module — append).
- `infra/main.bicep` — `githubAppId`/`githubInstallationId` params + three container env vars.
- `core/src/dsf/container.py` — `AzureRuntimeSettings` gains `github_app_id` / `github_installation_id` / `github_app_private_key_secret` (parsed now, consumed in Stage 3).
- `core/tests/test_container.py` — assert the new settings parse from env.

**Out of scope (Stage 3+):** the four orchestration action methods on `GitHubAppClient`; wiring the App client into `build_services`; branch-protection rulesets / replacing `squad_governance`; Cosmos namespace; MCP server; reflection job.

---

### Task 1: `GitHubAppClient` token-minting core + `pyjwt[crypto]` dependency

**Files:**
- Modify: `core/pyproject.toml` (`dependencies` list)
- Create: `core/src/dsf/github_app_client.py`
- Test: `core/tests/test_github_app_client.py`

- [ ] **Step 1: Add the dependency and sync**

In `core/pyproject.toml`, add `pyjwt[crypto]` to the base `dependencies` (it is required at runtime to mint tokens, not just in tests):

```toml
dependencies = [
    "pydantic>=2.6",
    "httpx>=0.27",
    "fastapi>=0.110",
    "pyjwt[crypto]>=2.8",
]
```

Then run `uv sync --all-packages` (installs PyJWT + cryptography into the workspace venv).

- [ ] **Step 2: Write the failing test**

`core/tests/test_github_app_client.py`:

```python
from __future__ import annotations

from datetime import datetime, timedelta, timezone

import httpx
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

from dsf.github_app_client import GitHubAppClient


def _rsa_pem() -> tuple[str, object]:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    pem = key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    ).decode()
    return pem, key.public_key()


def _fixed_clock(moment: datetime):
    return lambda: moment


def test_installation_token_mints_repo_scoped_token_and_signs_valid_jwt():
    pem, public_key = _rsa_pem()
    now = datetime(2026, 6, 22, 12, 0, tzinfo=timezone.utc)
    seen: dict[str, object] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        seen["auth"] = request.headers["Authorization"]
        seen["body"] = request.read()
        # The Authorization bearer must be a JWT signed by our App private key.
        token = request.headers["Authorization"].removeprefix("Bearer ")
        claims = jwt.decode(token, public_key, algorithms=["RS256"])
        seen["iss"] = claims["iss"]
        return httpx.Response(
            201,
            json={"token": "ghs_installtoken", "expires_at": "2026-06-22T13:00:00Z"},
        )

    client = GitHubAppClient(
        app_id="123",
        installation_id="456",
        private_key_pem=pem,
        repository_ids=[789],
        transport=httpx.MockTransport(handler),
        clock=_fixed_clock(now),
    )

    assert client.installation_token() == "ghs_installtoken"
    assert seen["url"] == "https://api.github.com/app/installations/456/access_tokens"
    assert seen["iss"] == "123"
    assert b'"repository_ids"' in seen["body"] and b"789" in seen["body"]


def test_installation_token_caches_until_near_expiry_then_refreshes():
    pem, _ = _rsa_pem()
    now = {"t": datetime(2026, 6, 22, 12, 0, tzinfo=timezone.utc)}
    calls = {"n": 0}

    def handler(request: httpx.Request) -> httpx.Response:
        calls["n"] += 1
        return httpx.Response(
            201,
            json={"token": f"ghs_{calls['n']}", "expires_at": "2026-06-22T13:00:00Z"},
        )

    client = GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        transport=httpx.MockTransport(handler),
        clock=lambda: now["t"],
    )

    assert client.installation_token() == "ghs_1"
    assert client.installation_token() == "ghs_1"  # cached, no second call
    assert calls["n"] == 1

    now["t"] = datetime(2026, 6, 22, 12, 59, 30, tzinfo=timezone.utc)  # inside skew
    assert client.installation_token() == "ghs_2"
    assert calls["n"] == 2


def test_installation_token_raises_on_api_error():
    pem, _ = _rsa_pem()
    client = GitHubAppClient(
        app_id="1",
        installation_id="2",
        private_key_pem=pem,
        transport=httpx.MockTransport(lambda r: httpx.Response(404, json={})),
        clock=lambda: datetime(2026, 6, 22, tzinfo=timezone.utc),
    )
    with pytest.raises(httpx.HTTPStatusError):
        client.installation_token()
```

- [ ] **Step 3: Run test to verify it fails**

Run: `uv run pytest core/tests/test_github_app_client.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'dsf.github_app_client'`.

- [ ] **Step 4: Write minimal implementation**

`core/src/dsf/github_app_client.py`:

```python
"""Real GitHub App identity client: mint short-lived, repo-scoped installation tokens.

The App private key never authenticates the API directly — it only signs a
<=10-minute RS256 JWT that exchanges for an installation access token. Tokens are
cached until just before expiry and re-minted on demand. Injecting ``transport``
(an ``httpx`` transport) and ``clock`` makes minting fully deterministic in tests
with no live call (ADR 0014 real-only ``src/``).
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

import httpx
import jwt

_GITHUB_API = "https://api.github.com"
_JWT_TTL = timedelta(minutes=9)  # GitHub caps App JWTs at 10 minutes
_REFRESH_SKEW = timedelta(seconds=60)  # re-mint slightly before expiry


def _utcnow() -> datetime:
    return datetime.now(timezone.utc)


@dataclass
class _CachedToken:
    token: str
    expires_at: datetime


@dataclass
class GitHubAppClient:
    """Mints installation access tokens for the DSF GitHub App.

    ``repository_ids`` (when set) scopes minted tokens to exactly those repos.
    """

    app_id: str
    installation_id: str
    private_key_pem: str
    repository_ids: list[int] | None = None
    transport: httpx.BaseTransport | None = None
    clock: Callable[[], datetime] = _utcnow
    _cached: _CachedToken | None = field(default=None, init=False, repr=False)

    def _app_jwt(self) -> str:
        now = self.clock()
        payload = {
            "iat": int(now.timestamp()) - 60,  # backdate for clock skew
            "exp": int((now + _JWT_TTL).timestamp()),
            "iss": self.app_id,
        }
        return jwt.encode(payload, self.private_key_pem, algorithm="RS256")

    def installation_token(self) -> str:
        """Return a cached token, or mint a fresh repo-scoped installation token."""
        now = self.clock()
        if self._cached and self._cached.expires_at - _REFRESH_SKEW > now:
            return self._cached.token

        body: dict[str, object] = {}
        if self.repository_ids:
            body["repository_ids"] = self.repository_ids
        with httpx.Client(transport=self.transport, base_url=_GITHUB_API) as client:
            resp = client.post(
                f"/app/installations/{self.installation_id}/access_tokens",
                headers={
                    "Authorization": f"Bearer {self._app_jwt()}",
                    "Accept": "application/vnd.github+json",
                },
                json=body,
            )
            resp.raise_for_status()
            data = resp.json()
        expires_at = datetime.fromisoformat(data["expires_at"].replace("Z", "+00:00"))
        self._cached = _CachedToken(token=data["token"], expires_at=expires_at)
        return self._cached.token
```

- [ ] **Step 5: Run test to verify it passes**

Run: `uv run pytest core/tests/test_github_app_client.py -q`
Expected: PASS (3 passed).

- [ ] **Step 6: Commit**

```bash
git add core/pyproject.toml core/src/dsf/github_app_client.py core/tests/test_github_app_client.py uv.lock
git commit -m "feat(core): add GitHubAppClient token-minting core (App JWT -> installation token)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 2: `GitHubAppBinding` manifest model

**Files:**
- Modify: `cli/src/dsf/instance/spec.py` (after `AzureProvisionResult`, before `InstanceManifest`)
- Test: `cli/tests/instance/test_spec.py` (append; create the file only if it does not exist)

- [ ] **Step 1: Write the failing test**

Append to `cli/tests/instance/test_spec.py`:

```python
from dsf.instance.spec import GitHubAppBinding, InstanceManifest, InstancePlan, InstanceSpec


def test_manifest_round_trips_github_app_binding():
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(product="demo", steps=[])
    binding = GitHubAppBinding(app_id="123", installation_id="456", repository_id=789)
    manifest = InstanceManifest(spec=spec, plan=plan, github_app=binding)

    loaded = InstanceManifest.model_validate_json(manifest.model_dump_json())
    assert loaded.github_app is not None
    assert loaded.github_app.app_id == "123"
    assert loaded.github_app.installation_id == "456"
    assert loaded.github_app.repository_id == 789
    assert loaded.github_app.private_key_secret == "github-app-private-key"


def test_manifest_github_app_defaults_to_none():
    spec = InstanceSpec(product="demo", owner="acme")
    plan = InstancePlan(product="demo", steps=[])
    assert InstanceManifest(spec=spec, plan=plan).github_app is None
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_spec.py -q`
Expected: FAIL — `ImportError: cannot import name 'GitHubAppBinding'`.

- [ ] **Step 3: Write minimal implementation**

In `cli/src/dsf/instance/spec.py`, add the model after `AzureProvisionResult` and the field on `InstanceManifest`:

```python
class GitHubAppBinding(BaseModel):
    """The DSF GitHub App binding captured for one product at provision time.

    ``app_id`` and ``installation_id`` are owner-level (shared across products);
    ``repository_id`` is this product's repo, added to that single installation.
    ``private_key_secret`` is the product Key Vault secret name the runtime reads.
    """

    app_id: str
    installation_id: str
    repository_id: int
    private_key_secret: str = "github-app-private-key"


class InstanceManifest(BaseModel):
    """Persisted record of an instance: spec + plan + execution status."""

    spec: InstanceSpec
    plan: InstancePlan
    executed: bool = False
    azure: AzureProvisionResult | None = None
    github_app: GitHubAppBinding | None = None
```

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_spec.py -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/instance/spec.py cli/tests/instance/test_spec.py
git commit -m "feat(cli): add GitHubAppBinding to the instance manifest

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 3: App-bootstrap engine — manifest, exchange, installation discovery, owner-KV commands

**Files:**
- Create: `cli/src/dsf/instance/app_bootstrap.py` (non-interactive seams only in this task)
- Test: `cli/tests/instance/test_app_bootstrap.py`

This task builds every **pure / injectable** piece of `dsf bootstrap`. The interactive driver that calls them is Task 4.

- [ ] **Step 1: Write the failing test**

`cli/tests/instance/test_app_bootstrap.py`:

```python
from __future__ import annotations

from datetime import datetime, timezone

import httpx
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

from dsf.instance.app_bootstrap import (
    AppCredentials,
    app_manifest,
    discover_installation_id,
    exchange_manifest_code,
    owner_kv_ensure_commands,
    owner_kv_store_commands,
)


def _rsa_pem() -> str:
    key = rsa.generate_private_key(public_exponent=65537, key_size=2048)
    return key.private_bytes(
        encoding=serialization.Encoding.PEM,
        format=serialization.PrivateFormat.PKCS8,
        encryption_algorithm=serialization.NoEncryption(),
    ).decode()


def test_app_manifest_describes_least_privilege_app():
    manifest = app_manifest(name="dsf-acme", callback_url="http://127.0.0.1:8765/cb")
    assert manifest["name"] == "dsf-acme"
    assert manifest["url"] == "http://127.0.0.1:8765/cb"
    assert manifest["public"] is False
    assert manifest["default_permissions"] == {
        "issues": "write",
        "pull_requests": "write",
        "contents": "read",
        "administration": "write",
    }


def test_exchange_manifest_code_posts_to_conversions_and_parses_credentials():
    seen: dict[str, str] = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["url"] = str(request.url)
        return httpx.Response(
            201,
            json={
                "id": 42,
                "slug": "dsf-acme",
                "pem": "-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n",
                "webhook_secret": "whs",
                "client_id": "cid",
                "client_secret": "csec",
            },
        )

    creds = exchange_manifest_code("tempcode", transport=httpx.MockTransport(handler))
    assert seen["url"] == "https://api.github.com/app-manifests/tempcode/conversions"
    assert creds == AppCredentials(
        app_id="42",
        slug="dsf-acme",
        pem="-----BEGIN PRIVATE KEY-----\nx\n-----END PRIVATE KEY-----\n",
        webhook_secret="whs",
        client_id="cid",
        client_secret="csec",
    )


def test_discover_installation_id_polls_until_present():
    creds = AppCredentials(app_id="42", slug="s", pem=_rsa_pem(), webhook_secret="",
                           client_id="", client_secret="")
    responses = iter([
        httpx.Response(200, json=[]),
        httpx.Response(200, json=[{"id": 9001}]),
    ])

    def handler(request: httpx.Request) -> httpx.Response:
        # Must authenticate as the App (JWT signed by the App key).
        assert request.headers["Authorization"].startswith("Bearer ")
        jwt.decode(
            request.headers["Authorization"].removeprefix("Bearer "),
            options={"verify_signature": False},
        )
        return next(responses)

    slept: list[float] = []
    got = discover_installation_id(
        creds,
        transport=httpx.MockTransport(handler),
        clock=lambda: datetime(2026, 6, 22, tzinfo=timezone.utc),
        sleep=slept.append,
        max_attempts=5,
    )
    assert got == "9001"
    assert slept == [pytest.approx(5.0)]  # one wait between the two polls


def test_owner_kv_ensure_commands_create_rbac_vault_and_grant_operator():
    cmds = owner_kv_ensure_commands(
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf-app",
        location="swedencentral",
        operator_object_id="oid-1",
    )
    assert ["az", "group", "create", "--name", "rg-dsf-app",
            "--location", "swedencentral"] in cmds
    create = next(c for c in cmds if c[:3] == ["az", "keyvault", "create"])
    assert "--enable-rbac-authorization" in create
    grant = next(c for c in cmds if c[:4] == ["az", "role", "assignment", "create"])
    assert "Key Vault Secrets Officer" in grant
    assert "oid-1" in grant


def test_owner_kv_store_commands_set_id_installation_and_keyfile():
    cmds = owner_kv_store_commands(
        keyvault_name="kv-dsf-app",
        app_id="42",
        installation_id="9001",
        pem_path="/tmp/app.pem",
    )
    assert ["az", "keyvault", "secret", "set", "--vault-name", "kv-dsf-app",
            "--name", "github-app-id", "--value", "42"] in cmds
    assert ["az", "keyvault", "secret", "set", "--vault-name", "kv-dsf-app",
            "--name", "github-app-installation-id", "--value", "9001"] in cmds
    keyset = next(c for c in cmds if "github-app-private-key" in c)
    assert "--file" in keyset and "/tmp/app.pem" in keyset
    assert "--value" not in keyset  # never pass the PEM as a process argument
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_app_bootstrap.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'dsf.instance.app_bootstrap'`.

- [ ] **Step 3: Write minimal implementation**

`cli/src/dsf/instance/app_bootstrap.py` (this task adds everything *except* the `bootstrap_app` driver, which is Task 4):

```python
"""Engine for ``dsf bootstrap`` — create the owner-level DSF GitHub App once and
store its credentials in the owner Key Vault.

The unit-testable seams live here (manifest generation, the ``code`` -> credentials
exchange, installation discovery, owner-KV command builders). The interactive driver
that opens a browser + runs the local callback server is in ``bootstrap_app`` (added
in the same module in the next task); its glue is validated by a real bootstrap, not
the unit suite (ADR 0014 framing).
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone

import httpx
import jwt

_GITHUB_API = "https://api.github.com"
_PRIVATE_KEY_SECRET = "github-app-private-key"
_APP_ID_SECRET = "github-app-id"
_INSTALLATION_SECRET = "github-app-installation-id"
_JWT_TTL = timedelta(minutes=9)
_SECRETS_OFFICER = "Key Vault Secrets Officer"


@dataclass(frozen=True)
class AppCredentials:
    """Credentials returned by the App-manifest conversion."""

    app_id: str
    slug: str
    pem: str
    webhook_secret: str
    client_id: str
    client_secret: str


def app_manifest(*, name: str, callback_url: str) -> dict:
    """Build the GitHub App manifest (least-privilege permissions)."""
    return {
        "name": name,
        "url": callback_url,
        "redirect_url": callback_url,
        "public": False,
        "default_permissions": {
            "issues": "write",
            "pull_requests": "write",
            "contents": "read",
            "administration": "write",
        },
        "default_events": ["pull_request", "issues"],
    }


def exchange_manifest_code(
    code: str, *, transport: httpx.BaseTransport | None = None
) -> AppCredentials:
    """Exchange a temporary manifest ``code`` for the App's permanent credentials."""
    with httpx.Client(transport=transport, base_url=_GITHUB_API) as client:
        resp = client.post(
            f"/app-manifests/{code}/conversions",
            headers={"Accept": "application/vnd.github+json"},
        )
        resp.raise_for_status()
        data = resp.json()
    return AppCredentials(
        app_id=str(data["id"]),
        slug=data["slug"],
        pem=data["pem"],
        webhook_secret=data.get("webhook_secret", ""),
        client_id=data.get("client_id", ""),
        client_secret=data.get("client_secret", ""),
    )


def _app_jwt(creds: AppCredentials, now: datetime) -> str:
    payload = {
        "iat": int(now.timestamp()) - 60,
        "exp": int((now + _JWT_TTL).timestamp()),
        "iss": creds.app_id,
    }
    return jwt.encode(payload, creds.pem, algorithm="RS256")


def discover_installation_id(
    creds: AppCredentials,
    *,
    transport: httpx.BaseTransport | None = None,
    clock: Callable[[], datetime] = lambda: datetime.now(timezone.utc),
    sleep: Callable[[float], None] = lambda _s: None,
    max_attempts: int = 60,
    poll_seconds: float = 5.0,
) -> str:
    """Poll ``GET /app/installations`` (authenticated as the App) until the
    operator's installation appears, then return its id."""
    with httpx.Client(transport=transport, base_url=_GITHUB_API) as client:
        for attempt in range(1, max_attempts + 1):
            resp = client.get(
                "/app/installations",
                headers={
                    "Authorization": f"Bearer {_app_jwt(creds, clock())}",
                    "Accept": "application/vnd.github+json",
                },
            )
            resp.raise_for_status()
            installations = resp.json()
            if installations:
                return str(installations[0]["id"])
            if attempt < max_attempts:
                sleep(poll_seconds)
    raise RuntimeError(
        "no installation appeared for the DSF App; install it on the owner "
        "account and retry `dsf bootstrap`"
    )


def owner_kv_ensure_commands(
    *, resource_group: str, keyvault_name: str, location: str, operator_object_id: str
) -> list[list[str]]:
    """`az` commands creating the owner RG + RBAC Key Vault and granting the
    operator Secrets Officer so they can write the App secrets."""
    return [
        ["az", "group", "create", "--name", resource_group, "--location", location],
        [
            "az", "keyvault", "create", "--name", keyvault_name,
            "--resource-group", resource_group, "--location", location,
            "--enable-rbac-authorization", "true",
        ],
        [
            "az", "role", "assignment", "create",
            "--role", _SECRETS_OFFICER,
            "--assignee-object-id", operator_object_id,
            "--assignee-principal-type", "User",
            "--scope",
            f"/subscriptions/{{subscription}}/resourceGroups/{resource_group}"
            f"/providers/Microsoft.KeyVault/vaults/{keyvault_name}",
        ],
    ]


def owner_kv_store_commands(
    *, keyvault_name: str, app_id: str, installation_id: str, pem_path: str
) -> list[list[str]]:
    """`az keyvault secret set` commands persisting the App id, installation id,
    and (from a file, never a process arg) the private key into the owner vault."""
    return [
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _APP_ID_SECRET, "--value", app_id],
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _INSTALLATION_SECRET, "--value", installation_id],
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _PRIVATE_KEY_SECRET, "--file", pem_path],
    ]
```

> Note on the role-assignment scope: the literal `{subscription}` placeholder is
> resolved by the driver in Task 4 from `az account show --query id -o tsv` before
> the command runs; the builder stays pure (no `az` call) so it is unit-testable.

- [ ] **Step 4: Run test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_app_bootstrap.py -q`
Expected: PASS (5 passed).

- [ ] **Step 5: Run ruff on the new files**

Run: `uv run ruff check core/src/dsf/github_app_client.py cli/src/dsf/instance/app_bootstrap.py`
Expected: `All checks passed!`

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/app_bootstrap.py cli/tests/instance/test_app_bootstrap.py
git commit -m "feat(cli): add App-bootstrap engine (manifest, code exchange, install discovery, owner-KV cmds)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 4: Interactive `bootstrap_app` driver + `dsf bootstrap` CLI subcommand

**Files:**
- Modify: `cli/src/dsf/instance/app_bootstrap.py` (add `BootstrapConfig` + `bootstrap_app`)
- Modify: `cli/src/dsf/cli/factory.py` (add `_cmd_bootstrap` + the `bootstrap` subparser)
- Test: `cli/tests/instance/test_app_bootstrap.py` (append), `cli/tests/cli/test_factory.py` (append)

The driver ties the Task-3 seams together. The browser-open + local-callback-server capture of the manifest `code` is unavoidably interactive and is validated by a real bootstrap (stated plainly per ADR 0014 / the design's testing strategy). Everything *around* that capture — config resolution, subscription-id substitution, the ordered command plan, and the temp-file PEM handling — is unit-tested by injecting the capture + runner.

- [ ] **Step 1: Write the failing test**

Append to `cli/tests/instance/test_app_bootstrap.py`:

```python
from dsf.instance.app_bootstrap import BootstrapConfig, bootstrap_app


def test_bootstrap_app_runs_ensure_then_store_with_resolved_scope(tmp_path):
    creds = AppCredentials(app_id="42", slug="dsf-acme", pem="PEMDATA",
                           webhook_secret="", client_id="", client_secret="")
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        class R:
            stdout = ""
            returncode = 0
        if cmd[:3] == ["az", "account", "show"]:
            R.stdout = "sub-123\n"
        if cmd[:4] == ["az", "ad", "signed-in-user", "show"]:
            R.stdout = "oid-1\n"
        return R()

    cfg = BootstrapConfig(
        app_name="dsf-acme",
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf-app",
        location="swedencentral",
    )
    result = bootstrap_app(
        cfg,
        run=fake_run,
        capture_code=lambda manifest: "tempcode",
        exchange=lambda code, **_: creds,
        discover=lambda c, **_: "9001",
        write_pem=lambda pem: str(tmp_path / "app.pem"),
    )

    assert result.app_id == "42"
    assert result.installation_id == "9001"
    assert result.keyvault_uri.endswith("kv-dsf-app")  # uri or name surfaced to operator
    # Secrets Officer scope had its subscription id substituted before running.
    grant = next(c for c in calls if c[:4] == ["az", "role", "assignment", "create"])
    assert "/subscriptions/sub-123/resourceGroups/rg-dsf-app/" in " ".join(grant)
    # The private key was stored from a file, not as a CLI value.
    keyset = next(c for c in calls if "github-app-private-key" in c)
    assert "--file" in keyset and "--value" not in keyset
```

Append to `cli/tests/cli/test_factory.py` (mirror the existing factory-test style; if the file imports `build_parser`, reuse it):

```python
def test_bootstrap_subcommand_is_wired():
    from dsf.cli.factory import build_parser

    args = build_parser().parse_args(
        ["bootstrap", "--app-name", "dsf-acme", "--keyvault-name", "kv-dsf-app",
         "--resource-group", "rg-dsf-app"]
    )
    assert args.command == "bootstrap"
    assert args.app_name == "dsf-acme"
    assert args.keyvault_name == "kv-dsf-app"
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `uv run pytest cli/tests/instance/test_app_bootstrap.py::test_bootstrap_app_runs_ensure_then_store_with_resolved_scope cli/tests/cli/test_factory.py::test_bootstrap_subcommand_is_wired -q`
Expected: FAIL — `ImportError: cannot import name 'bootstrap_app'` / `argument command: invalid choice: 'bootstrap'`.

- [ ] **Step 3: Write minimal implementation**

Append to `cli/src/dsf/instance/app_bootstrap.py`:

```python
import subprocess
import tempfile
from collections.abc import Callable
from dataclasses import dataclass
from pathlib import Path

Runner = Callable[..., object]


@dataclass(frozen=True)
class BootstrapConfig:
    """Operator inputs for a one-time `dsf bootstrap`."""

    app_name: str
    resource_group: str
    keyvault_name: str
    location: str = "swedencentral"


@dataclass(frozen=True)
class BootstrapResult:
    """What the operator needs after bootstrap: the App identity + owner-KV pointer."""

    app_id: str
    installation_id: str
    keyvault_name: str
    keyvault_uri: str


def _default_write_pem(pem: str) -> str:
    """Write the PEM to a private (0600) temp file; caller unlinks it."""
    fd, path = tempfile.mkstemp(prefix="dsf-app-", suffix=".pem")
    with open(fd, "w", encoding="utf-8") as fh:
        fh.write(pem)
    Path(path).chmod(0o600)
    return path


def bootstrap_app(
    cfg: BootstrapConfig,
    *,
    run: Runner | None = None,
    capture_code: Callable[[dict], str],
    exchange: Callable[..., AppCredentials] = exchange_manifest_code,
    discover: Callable[..., str] = discover_installation_id,
    write_pem: Callable[[str], str] = _default_write_pem,
) -> BootstrapResult:
    """Drive the one-time App bootstrap end to end.

    ``capture_code`` performs the interactive manifest submission (browser + local
    callback) and returns the temporary code; it is injected so the rest is unit-
    testable. The PEM is materialized to a 0600 temp file only long enough to push
    it into the owner Key Vault, then unlinked.
    """
    runner = run or subprocess.run

    def _capture(cmd: list[str]) -> str:
        res = runner(cmd, check=True, capture_output=True, text=True)
        return getattr(res, "stdout", "").strip()

    manifest = app_manifest(name=cfg.app_name, callback_url="http://127.0.0.1:8765/callback")
    code = capture_code(manifest)
    creds = exchange(code)
    installation_id = discover(creds)

    subscription = _capture(["az", "account", "show", "--query", "id", "-o", "tsv"])
    operator_oid = _capture(
        ["az", "ad", "signed-in-user", "show", "--query", "id", "-o", "tsv"]
    )

    for cmd in owner_kv_ensure_commands(
        resource_group=cfg.resource_group,
        keyvault_name=cfg.keyvault_name,
        location=cfg.location,
        operator_object_id=operator_oid,
    ):
        cmd = [part.replace("{subscription}", subscription) for part in cmd]
        runner(cmd, check=True)

    pem_path = write_pem(creds.pem)
    try:
        for cmd in owner_kv_store_commands(
            keyvault_name=cfg.keyvault_name,
            app_id=creds.app_id,
            installation_id=installation_id,
            pem_path=pem_path,
        ):
            runner(cmd, check=True)
    finally:
        Path(pem_path).unlink(missing_ok=True)

    return BootstrapResult(
        app_id=creds.app_id,
        installation_id=installation_id,
        keyvault_name=cfg.keyvault_name,
        keyvault_uri=f"https://{cfg.keyvault_name}.vault.azure.net/",
    )
```

> The default `capture_code` (the real browser + `http.server` callback) is added as
> a private helper `_browser_capture_code(manifest)` in this module and passed by
> `_cmd_bootstrap`. It POSTs the manifest to `https://github.com/settings/apps/new`
> via an auto-submitting local HTML page, serves one request on `127.0.0.1:8765`, and
> reads the `code` query param GitHub redirects back. It performs live I/O and is
> validated by a real bootstrap run, not the unit suite.

Add the real capture helper + the CLI command. In `app_bootstrap.py`:

```python
import http.server
import json
import urllib.parse
import webbrowser


def _browser_capture_code(manifest: dict) -> str:
    """Interactive: open GitHub's App-create page with the manifest and capture the
    redirect ``code`` on a one-shot local callback. Validated by a real bootstrap."""
    captured: dict[str, str] = {}

    class _Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802 - stdlib signature
            query = urllib.parse.urlparse(self.path).query
            captured["code"] = urllib.parse.parse_qs(query).get("code", [""])[0]
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"DSF App created. You can close this tab.")

        def log_message(self, *_a):  # silence
            pass

    server = http.server.HTTPServer(("127.0.0.1", 8765), _Handler)
    page = (
        "<form action='https://github.com/settings/apps/new' method='post'>"
        f"<input type='hidden' name='manifest' value='{json.dumps(manifest)}'>"
        "</form><script>document.forms[0].submit()</script>"
    )
    html_path = Path(tempfile.mkstemp(prefix="dsf-app-", suffix=".html")[1])
    html_path.write_text(page, encoding="utf-8")
    webbrowser.open(html_path.as_uri())
    try:
        server.handle_request()  # one callback
    finally:
        server.server_close()
        html_path.unlink(missing_ok=True)
    if not captured.get("code"):
        raise RuntimeError("App-manifest callback returned no code")
    return captured["code"]
```

In `cli/src/dsf/cli/factory.py`, add the command function and subparser:

```python
def _cmd_bootstrap(args: argparse.Namespace) -> int:
    """Create the one-time owner-level DSF GitHub App and store it in the owner KV."""
    from dsf.instance.app_bootstrap import (
        BootstrapConfig,
        _browser_capture_code,
        bootstrap_app,
    )

    cfg = BootstrapConfig(
        app_name=args.app_name,
        resource_group=args.resource_group,
        keyvault_name=args.keyvault_name,
        location=args.location,
    )
    result = bootstrap_app(cfg, capture_code=_browser_capture_code)
    print(f"[dsf] DSF GitHub App created: app_id={result.app_id} "
          f"installation_id={result.installation_id}")
    print(f"[dsf] master credentials stored in owner Key Vault {result.keyvault_name}")
    print(f"[dsf] now export DSF_OWNER_KEYVAULT_URI={result.keyvault_uri} for `dsf new`")
    return 0
```

And in `build_parser()` (after the `new` subparser, before `delete`):

```python
    p_boot = sub.add_parser(
        "bootstrap",
        help="one-time: create the DSF GitHub App and store it in the owner Key Vault",
    )
    p_boot.add_argument("--app-name", required=True, help="GitHub App name (globally unique)")
    p_boot.add_argument(
        "--keyvault-name", required=True, help="owner Key Vault name for App credentials"
    )
    p_boot.add_argument(
        "--resource-group", default="rg-dsf-app", help="resource group for the owner Key Vault"
    )
    p_boot.add_argument(
        "--location", default="swedencentral", help="Azure region for the owner Key Vault"
    )
    p_boot.set_defaults(func=_cmd_bootstrap)
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_app_bootstrap.py cli/tests/cli/test_factory.py -q`
Expected: PASS.

- [ ] **Step 5: Lint**

Run: `uv run ruff check cli/src/dsf/instance/app_bootstrap.py cli/src/dsf/cli/factory.py`
Expected: `All checks passed!` (the `_browser_capture_code` import in `_cmd_bootstrap` is intentional; if ruff flags the private import, import the module and call `app_bootstrap._browser_capture_code`).

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/app_bootstrap.py cli/src/dsf/cli/factory.py cli/tests/instance/test_app_bootstrap.py cli/tests/cli/test_factory.py
git commit -m "feat(cli): add interactive bootstrap_app driver + 'dsf bootstrap' command

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 5: Provisioner `install_app` step + manifest binding + factory owner-KV plumbing

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` (constructor; `plan()`; `_execute_step`; new `_install_app`; manifest construction in `apply()`)
- Modify: `cli/src/dsf/cli/factory.py` (`_cmd_new`: read owner-KV pointer + pass new params)
- Test: `cli/tests/instance/test_provisioner.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `cli/tests/instance/test_provisioner.py` (match the existing fixture/runner style in that file — a `FakeRunner`/recording callable that returns `stdout` per command):

```python
def test_install_app_adds_repo_to_owner_installation_and_records_binding(tmp_path):
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(product="demo", owner="acme")
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        class R:
            returncode = 0
            stdout = "555\n" if cmd[:2] == ["gh", "api"] and "/repos/" in " ".join(cmd) else ""
        return R()

    prov = InstanceProvisioner(
        spec,
        run=fake_run,
        repo_root=tmp_path,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
        github_app_id="42",
        github_installation_id="9001",
    )
    manifest = prov.apply(execute=True)

    # repo id looked up, then PUT adds it to the single owner installation
    put = next(c for c in calls if c[:3] == ["gh", "api", "--method"])
    assert "PUT" in put
    assert "/user/installations/9001/repositories/555" in " ".join(put)
    assert manifest.github_app is not None
    assert manifest.github_app.app_id == "42"
    assert manifest.github_app.installation_id == "9001"
    assert manifest.github_app.repository_id == 555


def test_install_app_step_is_dry_run_safe(tmp_path):
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(product="demo", owner="acme")
    prov = InstanceProvisioner(spec, repo_root=tmp_path, github_installation_id="9001")
    plan = prov.plan()
    install = next(s for s in plan.steps if s.name == "install_app")
    assert "9001" in install.description
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k install_app -q`
Expected: FAIL — `TypeError: __init__() got an unexpected keyword argument 'owner_keyvault_uri'`.

- [ ] **Step 3: Write minimal implementation**

In `provisioner.py` `__init__`, add the three params and store them:

```python
    def __init__(
        self,
        spec: InstanceSpec,
        *,
        run: Runner | None = None,
        repo_root: Path | None = None,
        sleep: Callable[[float], None] | None = None,
        owner_keyvault_uri: str = "",
        github_app_id: str = "",
        github_installation_id: str = "",
    ) -> None:
        self.spec = spec
        self._run = run or subprocess.run
        self._repo_root = repo_root
        self._sleep = sleep or time.sleep
        self._owner_keyvault_uri = owner_keyvault_uri
        self._github_app_id = github_app_id
        self._github_installation_id = github_installation_id
        self._app_binding: GitHubAppBinding | None = None
```

Import `GitHubAppBinding` at the top of `provisioner.py` (alongside the other `spec` imports).

In `plan()`, insert an `install_app` step immediately after `create_labels` (the repo must exist; labels already require it):

```python
            ProvisionStep(
                name="install_app",
                description=(
                    f"Add {s.github_repo()} to the DSF App installation "
                    f"{self._github_installation_id or '<installation>'}"
                ),
            ),
```

In `_execute_step`, add a branch (before the generic `not execute` fallback):

```python
        elif step.name == "install_app":
            if not execute:
                step.result = "installed (dry-run)"
            else:
                self._app_binding = self._install_app()
                step.executed, step.result = True, "installed"
```

Add the helper method:

```python
    def _install_app(self) -> GitHubAppBinding:
        """Add the product repo to the single owner installation; capture the binding."""
        repo = self.spec.github_repo()
        lookup = self._run(
            ["gh", "api", f"/repos/{repo}", "--jq", ".id"],
            check=True, capture_output=True, text=True,
        )
        repository_id = int(getattr(lookup, "stdout", "0").strip())
        self._run(
            [
                "gh", "api", "--method", "PUT",
                f"/user/installations/{self._github_installation_id}"
                f"/repositories/{repository_id}",
            ],
            check=True,
        )
        return GitHubAppBinding(
            app_id=self._github_app_id,
            installation_id=self._github_installation_id,
            repository_id=repository_id,
        )
```

In `apply()`, thread the binding into the manifest (line ~334):

```python
            manifest = InstanceManifest(
                spec=self.spec, plan=plan, executed=executed,
                azure=azure_result, github_app=self._app_binding,
            )
```

In `factory.py` `_cmd_new`, resolve the owner-KV pointer and read the App id/installation id from it when executing, then pass all three to the provisioner. Add after the `spec = InstanceSpec(...)` block:

```python
    import os

    owner_kv = args.owner_keyvault_uri or os.environ.get("DSF_OWNER_KEYVAULT_URI", "")
    app_id, installation_id = "", ""
    if owner_kv and not args.dry_run:
        app_id, installation_id = _read_owner_app_pointers(owner_kv)
    prov = InstanceProvisioner(
        spec,
        repo_root=root,
        owner_keyvault_uri=owner_kv,
        github_app_id=app_id,
        github_installation_id=installation_id,
    )
```

Replace the old `prov = InstanceProvisioner(spec, repo_root=root)` line. Add the helper near the top of `factory.py`:

```python
def _read_owner_app_pointers(owner_keyvault_uri: str) -> tuple[str, str]:
    """Read the (non-secret) App id + installation id from the owner Key Vault."""
    import subprocess

    name = owner_keyvault_uri.split("//", 1)[-1].split(".", 1)[0]

    def _secret(secret_name: str) -> str:
        res = subprocess.run(
            ["az", "keyvault", "secret", "show", "--vault-name", name,
             "--name", secret_name, "--query", "value", "-o", "tsv"],
            check=True, capture_output=True, text=True,
        )
        return res.stdout.strip()

    return _secret("github-app-id"), _secret("github-app-installation-id")
```

Add the `--owner-keyvault-uri` argument to the `new` subparser (after `--config-root`):

```python
    p_new.add_argument(
        "--owner-keyvault-uri",
        default="",
        help="owner Key Vault holding the DSF App credentials "
        "(default: $DSF_OWNER_KEYVAULT_URI; required to install the App)",
    )
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k install_app -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/src/dsf/cli/factory.py cli/tests/instance/test_provisioner.py
git commit -m "feat(cli): add install_app provisioning step + owner-KV pointer plumbing

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 6: Provisioner `seed_app_key` step + `provision_azure` Bicep params

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py` (`plan()`; `_execute_step`; new `_seed_app_key`; `provision_azure` command)
- Test: `cli/tests/instance/test_provisioner.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `cli/tests/instance/test_provisioner.py`:

```python
def test_seed_app_key_copies_owner_pem_into_product_vault(tmp_path):
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(product="demo", owner="acme")
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        class R:
            returncode = 0
            stdout = "555\n" if "/repos/" in " ".join(cmd) else "-----PEM-----\n"
        return R()

    prov = InstanceProvisioner(
        spec, run=fake_run, repo_root=tmp_path, sleep=lambda _s: None,
        owner_keyvault_uri="https://kv-dsf-app.vault.azure.net/",
        github_app_id="42", github_installation_id="9001",
    )
    # Pretend provision_azure produced a product Key Vault name output.
    prov._seed_app_key(
        _azure_result_with(tmp_path, keyVaultName="kv-demo-xyz")  # helper in this test file
    )

    show = next(c for c in calls if c[:4] == ["az", "keyvault", "secret", "show"])
    assert "kv-dsf-app" in show  # read from owner vault
    setc = next(c for c in calls if c[:4] == ["az", "keyvault", "secret", "set"])
    assert "kv-demo-xyz" in setc and "--file" in setc  # written to product vault from a file


def test_provision_azure_passes_github_app_params(tmp_path):
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(product="demo", owner="acme")
    prov = InstanceProvisioner(
        spec, repo_root=tmp_path, github_app_id="42", github_installation_id="9001"
    )
    azure_step = next(s for s in prov.plan().steps if s.name == "provision_azure")
    joined = " ".join(azure_step.command)
    assert "githubAppId=42" in joined
    assert "githubInstallationId=9001" in joined
```

Add the small `_azure_result_with` helper near the top of the test module if not already present:

```python
def _azure_result_with(tmp_path, **outputs):
    from dsf.instance.spec import AzureProvisionResult

    return AzureProvisionResult(
        resource_group="rg-dsf-demo", deployment_name="dsf-demo",
        location="swedencentral", outputs={k: str(v) for k, v in outputs.items()},
    )
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "seed_app_key or github_app_params" -q`
Expected: FAIL — `AttributeError: 'InstanceProvisioner' object has no attribute '_seed_app_key'` / `githubAppId` not in command.

- [ ] **Step 3: Write minimal implementation**

In `plan()` `provision_azure` command, append the two params before `--no-wait`:

```python
                    f"runtimeImage={s.runtime_image}",
                    f"githubAppId={self._github_app_id}",
                    f"githubInstallationId={self._github_installation_id}",
                    "--no-wait",
```

Add a `seed_app_key` step right after `seed_appconfig` in `plan()`:

```python
            ProvisionStep(
                name="seed_app_key",
                description=(
                    "Seed the DSF App private key from the owner Key Vault into the "
                    f"product Key Vault for {s.product}"
                ),
            ),
```

In `_execute_step`, add a branch:

```python
        elif step.name == "seed_app_key":
            if not execute:
                step.result = "seeded (dry-run)"
            else:
                self._seed_app_key(azure_result)
                step.executed, step.result = True, "seeded"
```

Add the helper method (mirrors `_seed_appconfig`'s retry for the KV RBAC race; reuses `_SEED_MAX_ATTEMPTS`/`_SEED_RETRY_DELAY`). It reads the PEM from the owner vault, writes it to a 0600 temp file, sets it in the product vault, and always unlinks:

```python
    def _seed_app_key(self, azure_result: AzureProvisionResult | None) -> None:
        """Copy the App private key from the owner KV into the product KV (with retry)."""
        if not self._owner_keyvault_uri:
            raise RuntimeError(
                "no owner Key Vault configured; set DSF_OWNER_KEYVAULT_URI or "
                "--owner-keyvault-uri (run `dsf bootstrap` first)"
            )
        product_kv = azure_result.outputs.get("keyVaultName", "") if azure_result else ""
        if not product_kv:
            raise RuntimeError("provision_azure returned no keyVaultName; cannot seed App key")
        owner_kv = self._owner_keyvault_uri.split("//", 1)[-1].split(".", 1)[0]

        pem = self._run(
            ["az", "keyvault", "secret", "show", "--vault-name", owner_kv,
             "--name", "github-app-private-key", "--query", "value", "-o", "tsv"],
            check=True, capture_output=True, text=True,
        )
        fd, pem_path = tempfile.mkstemp(prefix="dsf-app-", suffix=".pem")
        try:
            with open(fd, "w", encoding="utf-8") as fh:
                fh.write(getattr(pem, "stdout", ""))
            Path(pem_path).chmod(0o600)
            cmd = ["az", "keyvault", "secret", "set", "--vault-name", product_kv,
                   "--name", "github-app-private-key", "--file", pem_path]
            last_error: subprocess.CalledProcessError | None = None
            for attempt in range(1, _SEED_MAX_ATTEMPTS + 1):
                try:
                    self._run(cmd, check=True, capture_output=True, text=True)
                    return
                except subprocess.CalledProcessError as exc:
                    last_error = exc
                    if attempt < _SEED_MAX_ATTEMPTS:
                        self._sleep(_SEED_RETRY_DELAY)
            assert last_error is not None
            raise last_error
        finally:
            Path(pem_path).unlink(missing_ok=True)
```

Add `import tempfile` to `provisioner.py`'s imports if not present (`from pathlib import Path` already is).

- [ ] **Step 4: Run tests to verify they pass**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -k "seed_app_key or github_app_params" -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat(cli): seed App private key into product KV + pass App params to Bicep

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 7: Bicep env wiring + runtime settings

**Files:**
- Modify: `infra/main.bicep` (two params + three container env entries)
- Modify: `core/src/dsf/container.py` (`AzureRuntimeSettings` fields)
- Test: `core/tests/test_container.py` (append)

- [ ] **Step 1: Write the failing test**

Append to `core/tests/test_container.py`:

```python
def test_settings_parse_github_app_env():
    from dsf.container import AzureRuntimeSettings

    settings = AzureRuntimeSettings.from_env(
        {
            "DSF_PRODUCT": "demo",
            "GITHUB_APP_ID": "42",
            "GITHUB_INSTALLATION_ID": "9001",
            "GITHUB_APP_PRIVATE_KEY_SECRET": "github-app-private-key",
        }
    )
    assert settings.github_app_id == "42"
    assert settings.github_installation_id == "9001"
    assert settings.github_app_private_key_secret == "github-app-private-key"


def test_settings_github_app_fields_default_blank():
    from dsf.container import AzureRuntimeSettings

    settings = AzureRuntimeSettings.from_env({"DSF_PRODUCT": "demo"})
    assert settings.github_app_id == ""
    assert settings.github_installation_id == ""
```

- [ ] **Step 2: Run test to verify it fails**

Run: `uv run pytest core/tests/test_container.py -k github_app -q`
Expected: FAIL — `AttributeError: 'AzureRuntimeSettings' object has no attribute 'github_app_id'`.

- [ ] **Step 3: Write minimal implementation**

In `core/src/dsf/container.py`, add three fields to `AzureRuntimeSettings`:

```python
    openai_embedding_deployment: str = ""
    github_app_id: str = ""
    github_installation_id: str = ""
    github_app_private_key_secret: str = ""
```

And resolve them in `from_env` (inside the returned `cls(...)`):

```python
            openai_embedding_deployment=(
                env.get("AZURE_OPENAI_EMBEDDING_DEPLOYMENT") or ""
            ).strip(),
            github_app_id=(env.get("GITHUB_APP_ID") or "").strip(),
            github_installation_id=(env.get("GITHUB_INSTALLATION_ID") or "").strip(),
            github_app_private_key_secret=(
                env.get("GITHUB_APP_PRIVATE_KEY_SECRET") or ""
            ).strip(),
```

In `infra/main.bicep`, add the params near `runtimeImage` (line ~36):

```bicep
@description('DSF GitHub App id (owner-level; supplied by `dsf new` from the owner Key Vault).')
param githubAppId string = ''

@description('DSF GitHub App installation id (owner-level single installation).')
param githubInstallationId string = ''
```

And three env entries in the `orchestratorApp` container `env` array (after `AZURE_OPENAI_EMBEDDING_DEPLOYMENT`, line ~375):

```bicep
            { name: 'AZURE_OPENAI_EMBEDDING_DEPLOYMENT', value: embeddingModel }
            { name: 'GITHUB_APP_ID', value: githubAppId }
            { name: 'GITHUB_INSTALLATION_ID', value: githubInstallationId }
            { name: 'GITHUB_APP_PRIVATE_KEY_SECRET', value: 'github-app-private-key' }
```

- [ ] **Step 4: Verify Bicep compiles + tests pass**

Run: `az bicep build --file infra/main.bicep --stdout > /dev/null && echo BICEP_OK`
Expected: `BICEP_OK` (warnings ok, no errors).

Run: `uv run pytest core/tests/test_container.py -k github_app -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add core/src/dsf/container.py core/tests/test_container.py infra/main.bicep
git commit -m "feat: wire GitHub App id/installation id into runtime env + settings

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

### Task 8: Doc refinement + full-suite gate + self-review

**Files:**
- Modify: `docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md` (installation-model paragraph)
- (verification only) whole repo

- [ ] **Step 1: Correct the design doc's installation paragraph**

In the design doc, update the "One DSF GitHub App, installed per product repo" subsection (lines ~63-70) so it matches the locked decision: one owner-level installation; each `dsf new` adds the product repo to that installation via API (no per-product browser consent); the runtime mints repo-scoped tokens; master credentials live in a dedicated owner Key Vault. Replace the sentence "Each `dsf new` then **installs** that App on the new product repo." with:

```markdown
The App is installed **once** on the owner account (one installation). Each `dsf new`
then adds the product repo to that single installation via the API (no per-product
browser consent) and records the shared installation id on the instance manifest.
Installation access tokens are minted **scoped to just the product repo** — short-lived,
revocable, least-privilege. The App id + private key + installation id live in a dedicated
**owner-level Key Vault**; `dsf new` reads the private key from it to seed each product
Key Vault.
```

- [ ] **Step 2: Run the full lint + import + test gate**

Run: `uv run ruff check . && uv run lint-imports && uv run pytest -q`
Expected: `All checks passed!`; import-linter `4 kept, 0 broken`; pytest all green (Stage-1 baseline was 516 passed; this stage adds tests, so the count rises).

- [ ] **Step 3: Self-review against the spec**

Confirm each Stage-2 spec requirement maps to a task:
- `GitHubAppClient` token-minting (injected runner/clock, no live call) → Task 1.
- App bootstrap via manifest flow + persist App id + private key → Tasks 3, 4.
- Install on product repo + capture installation id + seed product KV → Tasks 5, 6.
- Bicep: App-key KV secret + runtime env for App id/installation id → Tasks 6 (post-deploy secret seed), 7 (env).
- Removed squad token seed → already done in Stage 1.
- Manifest carries the App binding → Task 2.

Verify no `create_issue`/assign/branch-protection action methods leaked into `GitHubAppClient` (those are Stage 3). Verify no fakes were added to `src/` (real-only). Verify the App private key is never passed as a process argument anywhere (always `--file`).

- [ ] **Step 4: Final commit (only if Step 1/3 produced edits not already committed)**

```bash
git add docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md
git commit -m "docs: refine App installation model to one owner installation + repo-scoped tokens

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Self-Review (plan author)

**Spec coverage:** identity model (App client + governed token) → Tasks 1,5,6,7; provisioning changes (bootstrap, install, KV seed) → Tasks 3-6; infra changes (KV secret + runtime env) → Tasks 6-7; testing strategy (token-minting units, command-batch assertions, no live calls) → throughout. Branch-protection/S7 explicitly deferred to Stage 3 (design decomposition table). Cosmos/MCP/reflection are Stages 4-6.

**Placeholder scan:** the only intentionally-non-unit-tested code is the interactive `_browser_capture_code` glue (Task 4), which is real and validated by a live bootstrap — stated plainly per ADR 0014, not a TODO. Every other step has concrete code.

**Type consistency:** `GitHubAppClient(app_id, installation_id, private_key_pem, repository_ids, transport, clock)` (Task 1) is consistent wherever referenced. `AppCredentials(app_id, slug, pem, webhook_secret, client_id, client_secret)` is used identically in Tasks 3-4. `GitHubAppBinding(app_id, installation_id, repository_id, private_key_secret)` (Task 2) is constructed in Task 5's `_install_app` and asserted in tests with matching fields. Secret names (`github-app-id`, `github-app-installation-id`, `github-app-private-key`) are identical across `app_bootstrap.py`, the provisioner seed, and the Bicep env. Provisioner constructor params (`owner_keyvault_uri`, `github_app_id`, `github_installation_id`) match between Tasks 5 and 6 and the factory call site.
