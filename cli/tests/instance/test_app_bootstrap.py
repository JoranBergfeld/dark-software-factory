from __future__ import annotations

import json
import subprocess
from datetime import UTC, datetime

import httpx
import jwt
import pytest
from cryptography.hazmat.primitives import serialization
from cryptography.hazmat.primitives.asymmetric import rsa

from dsf.instance.app_bootstrap import (
    _OWNER_KV_ARM_TEMPLATE,
    AppCredentials,
    BootstrapConfig,
    BootstrapResult,
    app_manifest,
    bootstrap_app,
    discover_installation_id,
    exchange_manifest_code,
    owner_kv_ensure_commands,
    owner_kv_store_commands,
    read_owner_kv_credentials,
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
        "contents": "write",
        "administration": "write",
    }
    assert "default_events" not in manifest
    assert "hook_attributes" not in manifest


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


def test_app_credentials_repr_hides_secrets():
    creds = AppCredentials(
        app_id="42",
        slug="s",
        pem="-----BEGIN PRIVATE KEY-----",
        webhook_secret="whs",
        client_id="cid",
        client_secret="csec",
    )
    text = repr(creds)
    assert "BEGIN PRIVATE KEY" not in text
    assert "whs" not in text
    assert "csec" not in text
    assert "42" in text


def test_discover_installation_id_polls_until_present():
    creds = AppCredentials(
        app_id="42",
        slug="s",
        pem=_rsa_pem(),
        webhook_secret="",
        client_id="",
        client_secret="",
    )
    responses = iter([
        httpx.Response(200, json=[]),
        httpx.Response(200, json=[{"id": 9001}]),
    ])

    def handler(request: httpx.Request) -> httpx.Response:
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
        clock=lambda: datetime(2026, 6, 22, tzinfo=UTC),
        sleep=slept.append,
        max_attempts=5,
    )
    assert got == "9001"
    assert slept == [pytest.approx(5.0)]


def test_owner_kv_ensure_commands_create_rbac_vault_and_grant_operator():
    cmds = owner_kv_ensure_commands(
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf-app",
        location="swedencentral",
        operator_object_id="oid-1",
    )
    assert [
        "az",
        "group",
        "create",
        "--name",
        "rg-dsf-app",
        "--location",
        "swedencentral",
    ] in cmds
    deploy = next(c for c in cmds if c[:4] == ["az", "deployment", "group", "create"])
    assert "--template" in deploy
    assert "--parameters" in deploy
    template = json.loads(deploy[deploy.index("--template") + 1])
    assert template["resources"][0]["properties"]["enableRbacAuthorization"] is True
    grant = next(c for c in cmds if c[:4] == ["az", "role", "assignment", "create"])
    assert "Key Vault Secrets Officer" in grant
    assert "oid-1" in grant


def test_owner_kv_ensure_commands_uses_arm_deployment_for_policy_compliance():
    cmds = owner_kv_ensure_commands(
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf-app",
        location="swedencentral",
        operator_object_id="oid-1",
    )
    assert [
        "az",
        "group",
        "create",
        "--name",
        "rg-dsf-app",
        "--location",
        "swedencentral",
    ] in cmds
    assert not any(c[:3] == ["az", "keyvault", "create"] for c in cmds)
    deploy = next(c for c in cmds if c[:4] == ["az", "deployment", "group", "create"])
    assert "--template" in deploy
    template_idx = deploy.index("--template")
    template = json.loads(deploy[template_idx + 1])
    props = template["resources"][0]["properties"]
    assert props["enableSoftDelete"] is True
    assert props["enablePurgeProtection"] is True
    assert props["softDeleteRetentionInDays"] == 90
    assert props["enableRbacAuthorization"] is True
    assert template == _OWNER_KV_ARM_TEMPLATE
    grant = next(c for c in cmds if c[:4] == ["az", "role", "assignment", "create"])
    assert "Key Vault Secrets Officer" in grant
    assert "oid-1" in grant


def test_owner_kv_store_commands_set_id_installation_and_keyfile(tmp_path):
    pem_path = str(tmp_path / "app.pem")
    cmds = owner_kv_store_commands(
        keyvault_name="kv-dsf-app",
        app_id="42",
        installation_id="9001",
        pem_path=pem_path,
    )
    assert [
        "az",
        "keyvault",
        "secret",
        "set",
        "--vault-name",
        "kv-dsf-app",
        "--name",
        "github-app-id",
        "--value",
        "42",
        "-o",
        "none",
    ] in cmds
    assert [
        "az",
        "keyvault",
        "secret",
        "set",
        "--vault-name",
        "kv-dsf-app",
        "--name",
        "github-app-installation-id",
        "--value",
        "9001",
        "-o",
        "none",
    ] in cmds
    keyset = next(c for c in cmds if "github-app-private-key" in c)
    assert "--file" in keyset and pem_path in keyset
    assert "--value" not in keyset
    assert keyset[-2:] == ["-o", "none"]


def test_read_owner_kv_credentials_returns_existing_values():
    vault_secrets = {
        "github-app-id": "99",
        "github-app-installation-id": "8000",
        "github-app-private-key": "EXISTINGPEM",
    }

    def fake_run(cmd, **kwargs):
        class R:
            stdout = ""
            returncode = 0

        if cmd[:4] == ["az", "keyvault", "secret", "show"]:
            name = cmd[cmd.index("--name") + 1]
            R.stdout = vault_secrets.get(name, "") + "\n"
        return R()

    result = read_owner_kv_credentials("kv-dsf-app", run=fake_run)
    assert result == BootstrapResult(
        app_id="99",
        installation_id="8000",
        keyvault_name="kv-dsf-app",
        keyvault_uri="https://kv-dsf-app.vault.azure.net/",
    )


def test_bootstrap_app_runs_ensure_then_store_with_resolved_scope(tmp_path):
    creds = AppCredentials(
        app_id="42",
        slug="dsf-acme",
        pem="PEMDATA",
        webhook_secret="",
        client_id="",
        client_secret="",
    )
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
        appconfig_name="dsf-owner-index",
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
    assert "kv-dsf-app" in result.keyvault_uri
    grant = next(c for c in calls if c[:4] == ["az", "role", "assignment", "create"])
    assert "/subscriptions/sub-123/resourceGroups/rg-dsf-app/" in " ".join(grant)
    keyset = next(
        c
        for c in calls
        if c[:4] == ["az", "keyvault", "secret", "set"] and "github-app-private-key" in c
    )
    assert "--file" in keyset and "--value" not in keyset


def test_bootstrap_app_returns_existing_credentials_if_vault_already_has_them():
    vault_secrets = {
        "github-app-id": "99",
        "github-app-installation-id": "8000",
        "github-app-private-key": "EXISTINGPEM",
    }

    def fake_run(cmd, **kwargs):
        class R:
            stdout = ""
            returncode = 0

        if cmd[:4] == ["az", "keyvault", "secret", "show"]:
            name = cmd[cmd.index("--name") + 1]
            R.stdout = vault_secrets.get(name, "") + "\n"
        return R()

    capture_called: list[int] = []
    result = bootstrap_app(
        BootstrapConfig(app_name="dsf-acme", resource_group="rg", keyvault_name="kv-dsf-app"),
        run=fake_run,
        capture_code=lambda manifest: capture_called.append(1) or "unused",
    )
    assert result.app_id == "99"
    assert result.installation_id == "8000"
    assert "kv-dsf-app" in result.keyvault_uri
    assert len(capture_called) == 0


def test_bootstrap_app_retries_secret_writes_on_rbac_propagation(tmp_path):
    creds = AppCredentials(
        app_id="42",
        slug="s",
        pem="PEMDATA",
        webhook_secret="",
        client_id="",
        client_secret="",
    )
    failures = {"left": 2}

    def fake_run(cmd, **kwargs):
        class R:
            stdout = ""
            returncode = 0

        if cmd[:3] == ["az", "account", "show"]:
            R.stdout = "sub-123\n"
        if cmd[:4] == ["az", "ad", "signed-in-user", "show"]:
            R.stdout = "oid-1\n"
        if cmd[:4] == ["az", "keyvault", "secret", "show"]:
            raise subprocess.CalledProcessError(1, cmd)
        if "github-app-private-key" in cmd and failures["left"] > 0:
            failures["left"] -= 1
            raise subprocess.CalledProcessError(1, cmd)
        return R()

    slept: list[float] = []
    result = bootstrap_app(
        BootstrapConfig(app_name="s", resource_group="rg", keyvault_name="kv", appconfig_name="ac"),
        run=fake_run,
        capture_code=lambda manifest: "tempcode",
        exchange=lambda code, **_: creds,
        discover=lambda c, **_: "9001",
        write_pem=lambda pem: str(tmp_path / "app.pem"),
        sleep=slept.append,
    )
    assert result.installation_id == "9001"
    assert failures["left"] == 0
    assert len(slept) == 2


def test_bootstrap_app_writes_recovery_file_and_cleans_up_on_success(tmp_path, monkeypatch):
    from dsf.instance import app_bootstrap

    monkeypatch.setattr(app_bootstrap, "_recovery_file", lambda name: tmp_path / f"rec-{name}.json")

    creds = AppCredentials(
        app_id="42",
        slug="s",
        pem="PEMDATA",
        webhook_secret="",
        client_id="",
        client_secret="",
    )

    def fake_run(cmd, **kwargs):
        class R:
            stdout = ""
            returncode = 0

        if cmd[:3] == ["az", "account", "show"]:
            R.stdout = "sub-123\n"
        if cmd[:4] == ["az", "ad", "signed-in-user", "show"]:
            R.stdout = "oid-1\n"
        if cmd[:4] == ["az", "keyvault", "secret", "show"]:
            raise subprocess.CalledProcessError(1, cmd)
        return R()

    result = bootstrap_app(
        BootstrapConfig(app_name="dsf-acme", resource_group="rg", keyvault_name="kv"),
        run=fake_run,
        capture_code=lambda manifest: "tempcode",
        exchange=lambda code, **_: creds,
        discover=lambda c, **_: "9001",
        write_pem=lambda pem: str(tmp_path / "app.pem"),
    )
    rec = tmp_path / "rec-dsf-acme.json"
    assert not rec.exists()
    assert result.app_id == "42"


def test_bootstrap_app_leaves_recovery_file_when_store_fails(tmp_path, monkeypatch):
    from dsf.instance import app_bootstrap

    monkeypatch.setattr(app_bootstrap, "_recovery_file", lambda name: tmp_path / f"rec-{name}.json")

    creds = AppCredentials(
        app_id="42",
        slug="s",
        pem="PEMDATA",
        webhook_secret="",
        client_id="",
        client_secret="",
    )

    def fake_run(cmd, **kwargs):
        class R:
            stdout = ""
            returncode = 0

        if cmd[:3] == ["az", "account", "show"]:
            R.stdout = "sub-123\n"
        if cmd[:4] == ["az", "ad", "signed-in-user", "show"]:
            R.stdout = "oid-1\n"
        if cmd[:4] == ["az", "keyvault", "secret", "show"]:
            raise subprocess.CalledProcessError(1, cmd)
        if cmd[:4] == ["az", "keyvault", "secret", "set"]:
            raise subprocess.CalledProcessError(1, cmd)
        return R()

    with pytest.raises(subprocess.CalledProcessError):
        bootstrap_app(
            BootstrapConfig(app_name="dsf-acme", resource_group="rg", keyvault_name="kv"),
            run=fake_run,
            capture_code=lambda manifest: "tempcode",
            exchange=lambda code, **_: creds,
            discover=lambda c, **_: "9001",
            write_pem=lambda pem: str(tmp_path / "app.pem"),
            sleep=lambda _: None,
        )

    rec = tmp_path / "rec-dsf-acme.json"
    assert rec.exists()
    assert json.loads(rec.read_text(encoding="utf-8")) == {
        "app_id": "42",
        "pem": "PEMDATA",
        "installation_id": "9001",
    }
