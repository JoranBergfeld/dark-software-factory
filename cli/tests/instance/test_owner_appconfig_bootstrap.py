"""Owner App Configuration provisioning during `dsf bootstrap`."""

from __future__ import annotations

from dsf.instance.app_bootstrap import _APPCONFIG_DATA_OWNER, owner_appconfig_ensure_commands


def test_owner_appconfig_ensure_commands_shape():
    cmds = owner_appconfig_ensure_commands(
        resource_group="rg-dsf-app",
        appconfig_name="dsf-owner-index",
        location="swedencentral",
        operator_object_id="oid-123",
    )

    create = next(c for c in cmds if c[:3] == ["az", "appconfig", "create"])
    assert "--name" in create and "dsf-owner-index" in create
    assert "--sku" in create and "Standard" in create
    assert "--disable-local-auth" in create and "true" in create

    grant = next(c for c in cmds if c[:4] == ["az", "role", "assignment", "create"])
    assert "App Configuration Data Owner" in grant
    assert "oid-123" in grant
    scope = grant[grant.index("--scope") + 1]
    assert scope.endswith(
        "/providers/Microsoft.AppConfiguration/configurationStores/dsf-owner-index"
    )
    assert "{subscription}" in scope  # substituted by bootstrap_app


def test_bootstrap_app_creates_owner_appconfig_and_returns_endpoint():
    from dataclasses import dataclass
    from pathlib import Path

    from dsf.instance.app_bootstrap import BootstrapConfig, bootstrap_app

    @dataclass
    class _Creds:
        app_id: str = "app-1"
        pem: str = "-----BEGIN-----\nx\n-----END-----"

    pem_path = Path("cli/tests/instance/.owner-appconfig-bootstrap.pem")
    calls: list[list[str]] = []

    def fake_run(cmd, *a, **k):
        calls.append(cmd)
        from types import SimpleNamespace

        if cmd[:3] == ["az", "account", "show"]:
            return SimpleNamespace(stdout="sub-9", returncode=0)
        if cmd[:2] == ["az", "ad"]:
            return SimpleNamespace(stdout="oid-7", returncode=0)
        return SimpleNamespace(stdout="", returncode=0)

    cfg = BootstrapConfig(
        app_name="DSF",
        resource_group="rg-dsf-app",
        keyvault_name="kv-dsf",
        appconfig_name="dsf-owner-index",
    )
    result = bootstrap_app(
        cfg,
        run=fake_run,
        capture_code=lambda manifest: "code",
        exchange=lambda code: _Creds(),
        discover=lambda creds: "install-2",
        write_pem=lambda pem: str(pem_path),
        sleep=lambda s: None,
    )

    assert result.appconfig_endpoint == "https://dsf-owner-index.azconfig.io"
    assert any(c[:3] == ["az", "appconfig", "create"] for c in calls)
    grant = next(
        c
        for c in calls
        if c[:4] == ["az", "role", "assignment", "create"] and _APPCONFIG_DATA_OWNER in c
    )
    scope = grant[grant.index("--scope") + 1]
    assert "/subscriptions/sub-9/" in scope
