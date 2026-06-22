"""Engine for ``dsf bootstrap`` — create the owner-level DSF GitHub App once and
store its credentials in the owner Key Vault.

The unit-testable seams live here (manifest generation, the ``code`` -> credentials
exchange, installation discovery, owner-KV command builders). The interactive driver
that opens a browser + runs the local callback server is added in a later task; its
glue is validated by a real bootstrap, not the unit suite (ADR 0014 framing).
"""

from __future__ import annotations

from collections.abc import Callable
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta

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
    clock: Callable[[], datetime] = lambda: datetime.now(UTC),
    sleep: Callable[[float], None] = lambda _s: None,
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
    """Build `az keyvault secret set` commands persisting the App credentials."""
    return [
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _APP_ID_SECRET, "--value", app_id],
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _INSTALLATION_SECRET, "--value", installation_id],
        ["az", "keyvault", "secret", "set", "--vault-name", keyvault_name,
         "--name", _PRIVATE_KEY_SECRET, "--file", pem_path],
    ]
