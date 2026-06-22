"""Engine for ``dsf bootstrap`` — create the owner-level DSF GitHub App once and
store its credentials in the owner Key Vault.

The unit-testable seams live here (manifest generation, the ``code`` -> credentials
exchange, installation discovery, owner-KV command builders). The interactive driver
that opens a browser + runs the local callback server is added in a later task; its
glue is validated by a real bootstrap, not the unit suite (ADR 0014 framing).
"""

from __future__ import annotations

import http.server
import json
import subprocess
import tempfile
import time
import urllib.parse
import webbrowser
from collections.abc import Callable
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from pathlib import Path

import httpx
import jwt

_GITHUB_API = "https://api.github.com"
_PRIVATE_KEY_SECRET = "github-app-private-key"
_APP_ID_SECRET = "github-app-id"
_INSTALLATION_SECRET = "github-app-installation-id"
_JWT_TTL = timedelta(minutes=9)
_SECRETS_OFFICER = "Key Vault Secrets Officer"
_SEED_ATTEMPTS = 8
_SEED_RETRY_DELAY = 15.0


@dataclass(frozen=True)
class AppCredentials:
    """Credentials returned by the App-manifest conversion."""

    app_id: str
    slug: str
    pem: str = field(repr=False)
    webhook_secret: str = field(repr=False)
    client_id: str
    client_secret: str = field(repr=False)


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
    clock: Callable[[], datetime] = lambda: datetime.now(UTC),
    sleep: Callable[[float], None] = time.sleep,
    max_attempts: int = 60,
    poll_seconds: float = 5.0,
) -> str:
    """Poll ``GET /app/installations`` until the App installation appears."""
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
    """Build `az` commands creating the owner RG, RBAC Key Vault, and role grant."""
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
    """Build `az keyvault secret set` commands persisting the App credentials.

    Every set runs with ``-o none``: ``az keyvault secret set`` echoes the stored
    secret bundle (including the plaintext ``value``) to stdout by default, which
    would leak the App private key into the terminal / CI logs.
    """
    return [
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _APP_ID_SECRET, "--value", app_id, "-o", "none"],
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _INSTALLATION_SECRET, "--value", installation_id, "-o", "none"],
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _PRIVATE_KEY_SECRET, "--file", pem_path, "-o", "none"],
    ]


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
    sleep: Callable[[float], None] = time.sleep,
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
    store_cmds = owner_kv_store_commands(
        keyvault_name=cfg.keyvault_name,
        app_id=creds.app_id,
        installation_id=installation_id,
        pem_path=pem_path,
    )
    try:
        # The owner just granted itself Secrets Officer above; the data-plane RBAC
        # assignment can take tens of seconds to propagate, so retry the (idempotent)
        # secret writes to absorb the initial 403s.
        for attempt in range(1, _SEED_ATTEMPTS + 1):
            try:
                for cmd in store_cmds:
                    runner(cmd, check=True)
                break
            except subprocess.CalledProcessError:
                if attempt == _SEED_ATTEMPTS:
                    raise
                sleep(_SEED_RETRY_DELAY)
    finally:
        Path(pem_path).unlink(missing_ok=True)

    return BootstrapResult(
        app_id=creds.app_id,
        installation_id=installation_id,
        keyvault_name=cfg.keyvault_name,
        keyvault_uri=f"https://{cfg.keyvault_name}.vault.azure.net/",
    )


def _browser_capture_code(manifest: dict) -> str:
    """Open GitHub's App-create page and capture the redirect code."""
    captured: dict[str, str] = {}

    class _Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):  # noqa: N802 - stdlib signature
            query = urllib.parse.urlparse(self.path).query
            captured["code"] = urllib.parse.parse_qs(query).get("code", [""])[0]
            self.send_response(200)
            self.end_headers()
            self.wfile.write(b"DSF App created. You can close this tab.")

        def log_message(self, *_a):
            pass

    server = http.server.HTTPServer(("127.0.0.1", 8765), _Handler)
    html_path: Path | None = None
    try:
        page = (
            "<form action='https://github.com/settings/apps/new' method='post'>"
            f"<input type='hidden' name='manifest' value='{json.dumps(manifest)}'>"
            "</form><script>document.forms[0].submit()</script>"
        )
        fd, html_name = tempfile.mkstemp(prefix="dsf-app-", suffix=".html")
        with open(fd, "w", encoding="utf-8") as fh:
            fh.write(page)
        html_path = Path(html_name)
        webbrowser.open(html_path.as_uri())
        server.handle_request()  # one callback
    finally:
        server.server_close()
        if html_path is not None:
            html_path.unlink(missing_ok=True)
    if not captured.get("code"):
        raise RuntimeError("App-manifest callback returned no code")
    return captured["code"]
