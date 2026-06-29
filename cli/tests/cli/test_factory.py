"""Tests for the `dsf` factory CLI (`dsf new` and `dsf delete`)."""

from __future__ import annotations

import io

import pytest

from dsf.cli.factory import build_parser, main
from dsf.instance.spec import (
    InstanceManifest,
    InstancePlan,
    ProvisionStep,
    read_manifest,
    write_manifest,
)


def _write_demo_manifest(root) -> None:
    """Write a minimal demo manifest so delete tests have something to read."""
    from dsf.instance.spec import InstanceSpec
    spec = InstanceSpec(product="demo", owner="acme")
    manifest = InstanceManifest(
        spec=spec, plan=InstancePlan(product="demo", steps=[]), executed=True
    )
    write_manifest(manifest, repo_root=root)


def test_new_parser_wiring():
    args = build_parser().parse_args(
        ["new", "--product", "demo", "--owner", "acme", "--name-prefix", "demopfx"]
    )
    assert args.command == "new"
    assert args.product == "demo"
    assert args.owner == "acme"
    assert args.name_prefix == "demopfx"
    assert args.environment == "dev"
    assert args.location == "swedencentral"
    assert args.creation_maturity == "low"
    # Provisioning executes by default; --dry-run is the opt-in preview.
    assert args.dry_run is False
    assert args.write_plan is False



def test_bootstrap_subcommand_is_wired():
    from dsf.cli.factory import build_parser

    args = build_parser().parse_args(
        [
            "bootstrap",
            "--app-name",
            "dsf-acme",
            "--keyvault-name",
            "kv-dsf-app",
            "--resource-group",
            "rg-dsf-app",
            "--appconfig-name",
            "dsf-owner-index",
        ]
    )
    assert args.command == "bootstrap"
    assert args.app_name == "dsf-acme"
    assert args.keyvault_name == "kv-dsf-app"
    assert args.appconfig_name == "dsf-owner-index"


def test_bootstrap_parser_requires_appconfig_name():
    import dsf.cli.factory as factory

    args = factory.build_parser().parse_args(
        [
            "bootstrap",
            "--app-name",
            "DSF",
            "--keyvault-name",
            "kv-dsf",
            "--appconfig-name",
            "dsf-owner-index",
        ]
    )
    assert args.appconfig_name == "dsf-owner-index"
    assert args.func is factory._cmd_bootstrap


def test_charter_next_action_message():
    from dsf.cli.factory import charter_next_action

    msg = charter_next_action("demo")
    assert "charter init" in msg
    assert "demo" in msg


def test_new_creation_maturity_high_flows_into_manifest(tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--creation-maturity", "high",
        "--dry-run", "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert read_manifest("demo", repo_root=tmp_path).spec.creation_maturity == "high"


def test_new_rejects_unknown_creation_maturity():
    with pytest.raises(SystemExit):
        build_parser().parse_args([
            "new", "--product", "demo", "--owner", "acme",
            "--name-prefix", "demopfx", "--creation-maturity", "wild",
        ])


def test_new_owner_and_name_prefix_are_optional():
    # Both are now inferred (owner from gh, name-prefix from the product key) and
    # default to "" when omitted — only --product is required.
    args = build_parser().parse_args(["new", "--product", "demo"])
    assert args.owner == ""
    assert args.name_prefix == ""


def test_new_requires_product():
    with pytest.raises(SystemExit):
        build_parser().parse_args(["new", "--owner", "acme", "--name-prefix", "demopfx"])


def test_new_infers_owner_from_gh_when_owner_omitted(tmp_path, monkeypatch):
    monkeypatch.setattr(
        "dsf.instance.github_identity.resolve_owner",
        lambda supplied, **_: supplied or "octocat",
    )
    rc = main([
        "new", "--product", "demo", "--name-prefix", "demopfx",
        "--dry-run", "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert read_manifest("demo", repo_root=tmp_path).spec.owner == "octocat"


def test_new_derives_name_prefix_from_product_when_omitted(tmp_path, monkeypatch):
    monkeypatch.setattr(
        "dsf.instance.github_identity.resolve_owner",
        lambda supplied, **_: supplied or "octocat",
    )
    rc = main([
        "new", "--product", "microbi", "--owner", "acme",
        "--dry-run", "--write-plan", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    pfx = read_manifest("microbi", repo_root=tmp_path).spec.name_prefix
    assert pfx.startswith("microbi")  # derived from the product key
    assert 3 <= len(pfx) <= 12


def test_new_owner_resolution_failure_exits_nonzero(tmp_path, monkeypatch, capsys):
    from dsf.instance.github_identity import OwnerResolutionError

    def _boom(supplied, **_):
        if supplied:
            return supplied
        raise OwnerResolutionError("gh is not authenticated")

    monkeypatch.setattr("dsf.instance.github_identity.resolve_owner", _boom)
    rc = main(["new", "--product", "demo", "--dry-run", "--config-root", str(tmp_path)])
    out = capsys.readouterr().out
    assert rc == 1
    assert "gh is not authenticated" in out


def test_new_dry_run_prints_plan_without_side_effects(capsys, tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--dry-run", "--config-root", str(tmp_path),
    ])
    out = capsys.readouterr().out
    assert rc == 0
    assert "create_repo" in out
    assert "provision_azure" in out
    assert "seed_product_record" in out
    assert "deploy_sre_agent" in out
    # pure preview: no manifest written even though a config root was provided
    assert not (tmp_path / "config" / "instances" / "demo.json").exists()
    # ...and registration never fires in pure preview (plan() only, no apply)
    assert not (tmp_path / "config" / "products.json").exists()


def test_new_write_plan_writes_manifest(tmp_path):
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--dry-run", "--write-plan",
        "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert (tmp_path / "config" / "instances" / "demo.json").exists()


def test_new_effective_prefix_is_stable_across_runs(tmp_path):
    argv = [
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "acmebase", "--dry-run", "--write-plan",
        "--config-root", str(tmp_path),
    ]
    assert main(argv) == 0
    first = read_manifest("demo", repo_root=tmp_path).spec.name_prefix
    assert main(argv) == 0
    second = read_manifest("demo", repo_root=tmp_path).spec.name_prefix
    assert first == second  # reused, not regenerated
    assert first.startswith("acmebase")
    assert len(first) == 12


def test_factory_parser_accepts_runtime_command():
    # the factory now fronts the runtime ops (subprocessing python -m dsf.runtime.control)
    args = build_parser().parse_args(["sweep", "--product", "pets"])
    assert args.product == "pets"
    assert args.func.__name__ == "_cmd_sweep"


def test_offboard_parser_wiring():
    args = build_parser().parse_args(["offboard", "demo", "--yes", "--purge"])
    assert args.command == "offboard"
    assert args.product == "demo"
    assert args.yes is True
    assert args.purge is True
    assert args.dry_run is False


def test_offboard_dry_run_prints_plan_without_side_effects(capsys, tmp_path, monkeypatch):
    from dsf.instance import provisioner as prov_mod
    from dsf.instance.spec import InstancePlan, ProvisionStep

    class _Offboarder:
        def __init__(self, product, **kwargs):
            self.product = product

        def apply(self, *, execute=False, on_event=None):
            step = ProvisionStep(
                name="remove_sre_rbac",
                description="remove RBAC",
                result="dry-run",
            )
            return InstancePlan(product=self.product, steps=[step])

    monkeypatch.setattr(prov_mod, "InstanceOffboarder", _Offboarder)
    rc = main([
        "offboard", "demo", "--dry-run", "--config-root", str(tmp_path),
    ])
    out = capsys.readouterr().out
    assert rc == 0
    assert "remove_sre_rbac" in out
    assert not (tmp_path / "config" / "products.json").exists()


def test_offboard_threads_owner_appconfig_endpoint_flag(tmp_path, monkeypatch):
    from dsf.instance import provisioner as prov_mod

    captured: dict = {}

    class _CapturingOffboarder:
        def __init__(self, product, **kwargs):
            self.product = product
            captured.update(kwargs)

        def apply(self, *, execute=False, on_event=None):
            step = ProvisionStep(
                name="remove_runtime_index", description="x", result="dry-run"
            )
            return InstancePlan(product=self.product, steps=[step])

    monkeypatch.delenv("DSF_OWNER_APPCONFIG_ENDPOINT", raising=False)
    monkeypatch.setattr(prov_mod, "InstanceOffboarder", _CapturingOffboarder)
    rc = main([
        "offboard", "demo", "--dry-run", "--config-root", str(tmp_path),
        "--owner-appconfig-endpoint", "https://o.azconfig.io",
    ])

    assert rc == 0
    assert captured["owner_appconfig_endpoint"] == "https://o.azconfig.io"


def test_offboard_owner_appconfig_endpoint_falls_back_to_env(tmp_path, monkeypatch):
    from dsf.instance import provisioner as prov_mod

    captured: dict = {}

    class _CapturingOffboarder:
        def __init__(self, product, **kwargs):
            self.product = product
            captured.update(kwargs)

        def apply(self, *, execute=False, on_event=None):
            step = ProvisionStep(
                name="remove_runtime_index", description="x", result="dry-run"
            )
            return InstancePlan(product=self.product, steps=[step])

    monkeypatch.setenv("DSF_OWNER_APPCONFIG_ENDPOINT", "https://env.azconfig.io")
    monkeypatch.setattr(prov_mod, "InstanceOffboarder", _CapturingOffboarder)
    rc = main(["offboard", "demo", "--dry-run", "--config-root", str(tmp_path)])

    assert rc == 0
    assert captured["owner_appconfig_endpoint"] == "https://env.azconfig.io"


def test_delete_threads_owner_appconfig_endpoint_flag(tmp_path, monkeypatch):
    from dsf.instance import deprovisioner as dep_mod
    from dsf.instance.spec import TeardownPlan

    captured: dict = {}

    class _CapturingDeprovisioner:
        def __init__(self, manifest, **kwargs):
            self.manifest = manifest
            self.spec = manifest.spec

        @classmethod
        def from_product(cls, product, **kwargs):
            captured.update(kwargs)
            manifest = read_manifest(product, kwargs.get("repo_root"))
            return cls(manifest, **kwargs)

        def apply(self, *, execute=False, on_event=None):
            step = ProvisionStep(
                name="remove_runtime_index", description="x", result="dry-run"
            )
            return TeardownPlan(product=self.spec.product, steps=[step])

    _write_demo_manifest(tmp_path)
    monkeypatch.delenv("DSF_OWNER_APPCONFIG_ENDPOINT", raising=False)
    monkeypatch.setattr(dep_mod, "InstanceDeprovisioner", _CapturingDeprovisioner)
    rc = main([
        "delete", "demo", "--dry-run", "--config-root", str(tmp_path),
        "--owner-appconfig-endpoint", "https://o.azconfig.io",
    ])

    assert rc == 0
    assert captured["owner_appconfig_endpoint"] == "https://o.azconfig.io"


def test_offboard_execute_surfaces_step_failure_and_exits_nonzero(capsys, tmp_path, monkeypatch):
    from dsf.instance import provisioner as prov_mod
    from dsf.instance.spec import InstancePlan, ProvisionStep

    class _FailingOffboarder:
        def __init__(self, product, **kwargs):
            self.product = product

        def apply(self, *, execute=False, on_event=None):
            step = ProvisionStep(
                name="delete_product_resource_group",
                description="Delete product resource group",
                result="failed",
                error="boom",
            )
            if on_event is not None:
                on_event("start", 3, 6, step, None)
                on_event("error", 3, 6, step, RuntimeError("boom"))
            return InstancePlan(product=self.product, steps=[step])

    monkeypatch.setattr(prov_mod, "InstanceOffboarder", _FailingOffboarder)
    rc = main(["offboard", "demo", "--yes", "--config-root", str(tmp_path)])
    out = capsys.readouterr().out
    assert rc == 1
    assert "FAILED" in out
    assert "offboard STOPPED at 'delete_product_resource_group'" in out


def test_offboard_execute_requires_confirmation(capsys, monkeypatch, tmp_path):
    monkeypatch.setattr("builtins.input", lambda _prompt: "nope")
    rc = main(["offboard", "demo", "--config-root", str(tmp_path)])
    out = capsys.readouterr().out
    assert rc == 1
    assert "aborted" in out


def test_new_execute_surfaces_step_failure_and_exits_nonzero(capsys, tmp_path, monkeypatch):
    # An executing run whose provisioner reports a failed step must surface the
    # error (not a traceback) and return a non-zero exit code.
    from dsf.instance import provisioner as prov_mod
    from dsf.instance.spec import InstanceManifest, InstancePlan, ProvisionStep

    class _FailingProvisioner:
        def __init__(self, spec, repo_root=None, **kwargs):
            self.spec = spec

        def apply(self, *, execute=False, on_event=None, on_progress=None):
            step = ProvisionStep(
                name="provision_azure",
                description="deploy backing services",
                result="failed",
                error="az deployment failed: boom",
            )
            if on_event is not None:
                on_event("start", 5, 11, step, None)
                on_event("error", 5, 11, step, RuntimeError("boom"))
            plan = InstancePlan(product=self.spec.product, steps=[step])
            return InstanceManifest(spec=self.spec, plan=plan, executed=True)

    monkeypatch.setattr(prov_mod, "InstanceProvisioner", _FailingProvisioner)
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--config-root", str(tmp_path),
    ])
    out = capsys.readouterr().out
    assert rc == 1
    assert "FAILED" in out  # live per-step error line
    assert "STOPPED at 'provision_azure'" in out  # final surfaced summary
    assert "boom" in out


def test_new_execute_indents_provision_progress(capsys, tmp_path, monkeypatch):
    # provision_azure progress lines are indented under the step line.
    from dsf.instance import provisioner as prov_mod
    from dsf.instance.spec import InstanceManifest, InstancePlan, ProvisionStep

    class _ProgressProvisioner:
        def __init__(self, spec, repo_root=None, **kwargs):
            self.spec = spec

        def apply(self, *, execute=False, on_event=None, on_progress=None):
            step = ProvisionStep(
                name="provision_azure", description="deploy backing services", result="executed"
            )
            if on_event is not None:
                on_event("start", 5, 11, step, None)
            if on_progress is not None:
                on_progress(
                    "· Microsoft.App/containerApps dsf-orchestrator: ✓ Succeeded (1m04s)"
                )
            if on_event is not None:
                on_event("done", 5, 11, step, None)
            plan = InstancePlan(product=self.spec.product, steps=[step])
            return InstanceManifest(spec=self.spec, plan=plan, executed=True)

    monkeypatch.setattr(prov_mod, "InstanceProvisioner", _ProgressProvisioner)
    rc = main([
        "new", "--product", "demo", "--owner", "acme",
        "--name-prefix", "demopfx", "--config-root", str(tmp_path),
    ])
    out = capsys.readouterr().out
    assert rc == 0
    assert (
        "[dsf]     · Microsoft.App/containerApps dsf-orchestrator: ✓ Succeeded" in out
    )


# ---------------------------------------------------------------------------
# dsf delete — parser
# ---------------------------------------------------------------------------


def test_delete_parser_wiring():
    args = build_parser().parse_args(["delete", "demo"])
    assert args.command == "delete"
    assert args.product == "demo"
    assert args.yes is False
    assert args.dry_run is False
    assert args.purge is False
    assert args.config_root is None


def test_delete_parser_accepts_yes_flag():
    args = build_parser().parse_args(["delete", "demo", "--yes"])
    assert args.yes is True


def test_delete_parser_accepts_dry_run_flag():
    args = build_parser().parse_args(["delete", "demo", "--dry-run"])
    assert args.dry_run is True


def test_delete_parser_accepts_purge_flag():
    args = build_parser().parse_args(["delete", "demo", "--purge"])
    assert args.purge is True


def test_delete_parser_accepts_config_root():
    args = build_parser().parse_args(["delete", "demo", "--config-root", "/tmp/x"])
    assert args.config_root == "/tmp/x"


def test_delete_requires_product_name():
    with pytest.raises(SystemExit):
        build_parser().parse_args(["delete"])


# ---------------------------------------------------------------------------
# dsf delete — behavior
# ---------------------------------------------------------------------------


def test_delete_dry_run_prints_teardown_plan_no_side_effects(capsys, tmp_path):
    _write_demo_manifest(tmp_path)
    rc = main(["delete", "demo", "--dry-run", "--config-root", str(tmp_path)])
    out = capsys.readouterr().out
    assert rc == 0
    assert "delete_sre_agent" in out
    assert "delete_resource_group" in out
    assert "remove_runtime_index" in out
    assert "delete_config" in out
    assert "delete_repo" in out
    assert "DRY-RUN" in out


def test_delete_dry_run_does_not_delete_manifest(capsys, tmp_path):
    _write_demo_manifest(tmp_path)
    main(["delete", "demo", "--dry-run", "--config-root", str(tmp_path)])
    from dsf.instance.spec import manifest_path
    assert manifest_path("demo", tmp_path).exists()


def test_delete_missing_manifest_returns_nonzero(capsys, tmp_path):
    rc = main(["delete", "nonexistent", "--dry-run", "--config-root", str(tmp_path)])
    assert rc == 1
    err = capsys.readouterr().err
    assert "no manifest found" in err


def test_delete_execute_with_yes_bypasses_confirmation(capsys, tmp_path, monkeypatch):
    from dsf.instance import deprovisioner as dep_mod

    class _FakeDeprovisioner:
        def __init__(self, manifest, **kwargs):
            self.manifest = manifest
            self.spec = manifest.spec

        def apply(self, *, execute=False, on_event=None):
            from dsf.instance.spec import TeardownPlan
            step = ProvisionStep(
                name="delete_sre_agent",
                description="delete sre agent",
                result="executed",
            )
            return TeardownPlan(product=self.spec.product, steps=[step])

        @classmethod
        def from_product(cls, product, **kwargs):
            from dsf.instance.spec import read_manifest
            repo_root = kwargs.get("repo_root")
            manifest = read_manifest(product, repo_root)
            return cls(manifest, **kwargs)

    _write_demo_manifest(tmp_path)
    monkeypatch.setattr(dep_mod, "InstanceDeprovisioner", _FakeDeprovisioner)
    rc = main(["delete", "demo", "--yes", "--config-root", str(tmp_path)])
    assert rc == 0


def test_delete_execute_noninteractive_without_yes_returns_error(tmp_path, monkeypatch):
    """In a non-interactive context, --yes is required."""
    _write_demo_manifest(tmp_path)
    # Simulate non-interactive stdin (isatty() -> False).
    import io
    monkeypatch.setattr("sys.stdin", io.StringIO(""))
    rc = main(["delete", "demo", "--config-root", str(tmp_path)])
    assert rc == 1


def test_delete_confirm_ctrl_c_aborts_cleanly(capsys, tmp_path, monkeypatch):
    """Ctrl-C / EOF at the confirm prompt aborts cleanly with no traceback."""
    _write_demo_manifest(tmp_path)
    monkeypatch.setattr("sys.stdin.isatty", lambda: True)

    def _raise(_prompt):
        raise KeyboardInterrupt

    monkeypatch.setattr("builtins.input", _raise)
    rc = main(["delete", "demo", "--config-root", str(tmp_path)])
    assert rc == 1
    err = capsys.readouterr().err
    assert "cancelled" in err
    # Manifest must survive an aborted delete.
    from dsf.instance.spec import manifest_path
    assert manifest_path("demo", tmp_path).exists()


def test_delete_confirm_eof_aborts_cleanly(tmp_path, monkeypatch):
    """EOFError (closed stdin) at the confirm prompt aborts cleanly."""
    _write_demo_manifest(tmp_path)
    monkeypatch.setattr("sys.stdin.isatty", lambda: True)

    def _raise(_prompt):
        raise EOFError

    monkeypatch.setattr("builtins.input", _raise)
    rc = main(["delete", "demo", "--config-root", str(tmp_path)])
    assert rc == 1


def test_delete_execute_failure_exits_nonzero(capsys, tmp_path, monkeypatch):
    from dsf.instance import deprovisioner as dep_mod

    class _FailingDeprovisioner:
        def __init__(self, manifest, **kwargs):
            self.manifest = manifest
            self.spec = manifest.spec

        def apply(self, *, execute=False, on_event=None):
            from dsf.instance.spec import TeardownPlan
            step = ProvisionStep(
                name="delete_resource_group",
                description="delete product rg",
                result="failed",
                error="AuthorizationFailed: insufficient permissions",
            )
            if on_event:
                on_event("start", 1, 1, step, None)
                on_event("error", 1, 1, step, RuntimeError("boom"))
            return TeardownPlan(product=self.spec.product, steps=[step])

        @classmethod
        def from_product(cls, product, **kwargs):
            from dsf.instance.spec import read_manifest
            repo_root = kwargs.get("repo_root")
            manifest = read_manifest(product, repo_root)
            return cls(manifest, **kwargs)

    _write_demo_manifest(tmp_path)
    monkeypatch.setattr(dep_mod, "InstanceDeprovisioner", _FailingDeprovisioner)
    rc = main(["delete", "demo", "--yes", "--config-root", str(tmp_path)])
    out = capsys.readouterr().out
    assert rc == 1
    assert "FAILED" in out
    assert "STOPPED at 'delete_resource_group'" in out


def test_new_parser_has_owner_keyvault_uri():
    args = build_parser().parse_args(
        [
            "new", "--product", "demo", "--owner", "acme",
            "--owner-keyvault-uri", "https://kv-dsf-app.vault.azure.net/",
        ]
    )
    assert args.owner_keyvault_uri == "https://kv-dsf-app.vault.azure.net/"


def test_new_parser_has_admin_principal_id():
    args = build_parser().parse_args(
        ["new", "--product", "demo", "--owner", "acme", "--admin-principal-id", "oid-123"]
    )
    assert args.admin_principal_id == "oid-123"
    # Defaults to empty so CI / service-principal runs skip the human grants.
    default = build_parser().parse_args(["new", "--product", "demo", "--owner", "acme"])
    assert default.admin_principal_id == ""


class _CapturingProvisioner:
    """Records the kwargs `dsf new` threads into InstanceProvisioner."""

    last_kwargs: dict = {}
    last_spec = None

    def __init__(self, spec, repo_root=None, **kwargs):
        type(self).last_kwargs = kwargs
        type(self).last_spec = spec
        self.spec = spec

    def plan(self):
        return InstancePlan(product=self.spec.product, steps=[])


def test_new_threads_admin_principal_id_from_flag(tmp_path, monkeypatch):
    from dsf.instance import provisioner as prov_mod

    monkeypatch.delenv("DSF_ADMIN_PRINCIPAL_ID", raising=False)
    monkeypatch.setattr(prov_mod, "InstanceProvisioner", _CapturingProvisioner)
    rc = main([
        "new", "--product", "demo", "--owner", "acme", "--name-prefix", "demopfx",
        "--dry-run", "--config-root", str(tmp_path), "--admin-principal-id", "oid-flag",
    ])
    assert rc == 0
    assert _CapturingProvisioner.last_kwargs["admin_principal_id"] == "oid-flag"


def test_new_admin_principal_id_falls_back_to_env(tmp_path, monkeypatch):
    from dsf.instance import provisioner as prov_mod

    monkeypatch.setenv("DSF_ADMIN_PRINCIPAL_ID", "oid-env")
    monkeypatch.setattr(prov_mod, "InstanceProvisioner", _CapturingProvisioner)
    rc = main([
        "new", "--product", "demo", "--owner", "acme", "--name-prefix", "demopfx",
        "--dry-run", "--config-root", str(tmp_path),
    ])
    assert rc == 0
    assert _CapturingProvisioner.last_kwargs["admin_principal_id"] == "oid-env"


def test_read_owner_app_pointers_reads_id_and_installation(monkeypatch):
    import subprocess

    from dsf.cli.factory import _read_owner_app_pointers

    seen: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        seen.append(cmd)
        from unittest.mock import MagicMock
        value = "42" if cmd[cmd.index("--name") + 1] == "github-app-id" else "9001"
        return MagicMock(returncode=0, stdout=value + "\n")

    monkeypatch.setattr(subprocess, "run", fake_run)
    app_id, installation_id = _read_owner_app_pointers("https://kv-dsf-app.vault.azure.net/")
    assert app_id == "42"
    assert installation_id == "9001"
    # vault name parsed from the URI host
    assert any("kv-dsf-app" in c for c in seen[0])


# ---------------------------------------------------------------------------
# dsf new — post-provision charter seeding (issue #86)
# ---------------------------------------------------------------------------


class _TtyStdin(io.StringIO):
    """stdin double that reports as an interactive terminal."""

    def isatty(self) -> bool:
        return True


def _patch_success_provisioner(monkeypatch):
    """Replace the real provisioner with one that 'executes' a single OK step."""
    from dsf.instance import provisioner as prov_mod

    class _OkProvisioner:
        def __init__(self, spec, repo_root=None, **kwargs):
            self.spec = spec

        def apply(self, *, execute=False, on_event=None, on_progress=None):
            step = ProvisionStep(
                name="seed_product_record", description="seed product", result="executed"
            )
            plan = InstancePlan(product=self.spec.product, steps=[step])
            return InstanceManifest(spec=self.spec, plan=plan, executed=True)

    monkeypatch.setattr(prov_mod, "InstanceProvisioner", _OkProvisioner)


def _patch_charter_app(monkeypatch, client):
    """Wire the charter App seams to a shared recording client (greenfield by default)."""
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: "org/demo")
    monkeypatch.setattr("dsf.cli.charter._app_settings", lambda product, **_: None)
    monkeypatch.setattr("dsf.cli.charter.build_repo_app_client", lambda s: client)


def _interview_model():
    """A model client whose interview finishes immediately with a valid draft."""
    from dsf.charter.interview import InterviewerTurn
    from dsf.contracts.charter import Charter
    from dsf_testing.model import DeterministicModelClient

    draft = Charter(
        product="demo",
        vision="V",
        target_users="U",
        goals=["g"],
        success_metrics=["m"],
        source_sha="abc123",
        source_ref="main",
    )
    model = DeterministicModelClient()
    model.register(
        "[charter-interview]",
        lambda s, p: InterviewerTurn(message="drafted", done=True, draft=draft),
    )
    return model


def _new_argv(tmp_path, *extra):
    return [
        "new", "--product", "demo", "--owner", "acme", "--name-prefix", "demopfx",
        "--config-root", str(tmp_path), *extra,
    ]


def test_new_parser_accepts_no_charter():
    args = build_parser().parse_args(
        ["new", "--product", "demo", "--no-charter"]
    )
    assert args.no_charter is True
    assert build_parser().parse_args(["new", "--product", "demo"]).no_charter is False


def test_new_no_charter_prints_next_step_without_prompting(monkeypatch, capsys, tmp_path):
    _patch_success_provisioner(monkeypatch)
    prompted = {"asked": False}
    monkeypatch.setattr(
        "builtins.input", lambda *a: prompted.__setitem__("asked", True) or "y"
    )
    rc = main(_new_argv(tmp_path, "--no-charter"))
    out = capsys.readouterr().out
    assert rc == 0
    assert "uv run dsf charter init --product demo" in out
    assert prompted["asked"] is False


def test_new_greenfield_interactive_yes_chains_into_interview(monkeypatch, capsys, tmp_path):
    from dsf.charter.sync import CHARTER_PATH
    from dsf_testing.config import InMemoryConfigStore
    from dsf_testing.github import RecordingRepoClient

    _patch_success_provisioner(monkeypatch)
    client = RecordingRepoClient({})  # no charter on main, no charter PR -> greenfield
    _patch_charter_app(monkeypatch, client)
    monkeypatch.setattr("dsf.cli.charter.build_model_client", lambda s: _interview_model())
    monkeypatch.setattr(
        "dsf.cli.charter.build_config_store", lambda s: InMemoryConfigStore.from_defaults()
    )
    monkeypatch.setattr("sys.stdin", _TtyStdin())
    prompts: list[str] = []
    monkeypatch.setattr("builtins.input", lambda prompt="": (prompts.append(prompt), "y")[1])

    rc = main(_new_argv(tmp_path))
    out = capsys.readouterr().out
    assert rc == 0
    assert any("Seed its charter now?" in p for p in prompts)
    assert "opened charter PR" in out
    assert "review & MERGE" in out
    assert len(client.prs) == 1 and client.prs[0]["path"] == CHARTER_PATH


def test_new_greenfield_interactive_no_prints_hint_and_opens_no_pr(monkeypatch, capsys, tmp_path):
    from dsf_testing.github import RecordingRepoClient

    _patch_success_provisioner(monkeypatch)
    client = RecordingRepoClient({})
    _patch_charter_app(monkeypatch, client)
    monkeypatch.setattr("sys.stdin", _TtyStdin())
    prompts: list[str] = []
    monkeypatch.setattr("builtins.input", lambda prompt="": (prompts.append(prompt), "n")[1])

    rc = main(_new_argv(tmp_path))
    out = capsys.readouterr().out
    assert rc == 0
    assert any("Seed its charter now?" in p for p in prompts)
    assert "uv run dsf charter init --product demo" in out
    assert client.prs == []


def test_new_noninteractive_greenfield_prints_hint_without_prompting(monkeypatch, capsys, tmp_path):
    from dsf_testing.github import RecordingRepoClient

    _patch_success_provisioner(monkeypatch)
    client = RecordingRepoClient({})
    _patch_charter_app(monkeypatch, client)
    monkeypatch.setattr("sys.stdin", io.StringIO(""))  # not a TTY
    prompted = {"asked": False}
    monkeypatch.setattr(
        "builtins.input", lambda *a: prompted.__setitem__("asked", True) or "y"
    )

    rc = main(_new_argv(tmp_path))
    out = capsys.readouterr().out
    assert rc == 0
    assert "your factory has no intent yet" in out
    assert "uv run dsf charter init --product demo" in out
    assert prompted["asked"] is False
    assert client.prs == []


def test_new_does_not_nag_when_charter_already_on_main(monkeypatch, capsys, tmp_path):
    from dsf.charter.sync import CHARTER_PATH
    from dsf_testing.github import RecordingRepoClient

    _patch_success_provisioner(monkeypatch)
    client = RecordingRepoClient({CHARTER_PATH: ("# charter", "sha123")})
    _patch_charter_app(monkeypatch, client)
    monkeypatch.setattr("sys.stdin", _TtyStdin())
    monkeypatch.setattr("builtins.input", lambda *a: "y")

    rc = main(_new_argv(tmp_path))
    out = capsys.readouterr().out
    assert rc == 0
    assert "already has a product charter" in out
    assert "no intent yet" not in out
    assert client.prs == []


def test_new_does_not_nag_when_charter_pr_already_open(monkeypatch, capsys, tmp_path):
    from datetime import UTC, datetime

    from dsf_testing.github import RecordingRepoClient, _AmendmentPr

    _patch_success_provisioner(monkeypatch)
    pr = _AmendmentPr(
        html_url="https://github.com/org/demo/pull/1",
        state="open",
        created_at=datetime.now(UTC),
        head_ref="charter/init-deadbeef",
    )
    client = RecordingRepoClient({}, prs=[pr])
    _patch_charter_app(monkeypatch, client)
    monkeypatch.setattr("sys.stdin", _TtyStdin())
    monkeypatch.setattr("builtins.input", lambda *a: "y")

    rc = main(_new_argv(tmp_path))
    out = capsys.readouterr().out
    assert rc == 0
    assert "already has a product charter" in out
    assert client.prs == []


def test_new_prints_hint_when_charter_state_undeterminable(monkeypatch, capsys, tmp_path):
    _patch_success_provisioner(monkeypatch)
    # Repo can't be resolved -> charter state is "unknown" -> print the hint, never prompt.
    monkeypatch.setattr("dsf.cli.charter._resolve_repo", lambda product: None)
    monkeypatch.setattr("sys.stdin", _TtyStdin())
    prompted = {"asked": False}
    monkeypatch.setattr(
        "builtins.input", lambda *a: prompted.__setitem__("asked", True) or "y"
    )

    rc = main(_new_argv(tmp_path))
    out = capsys.readouterr().out
    assert rc == 0
    assert "uv run dsf charter init --product demo" in out
    assert prompted["asked"] is False


def test_new_dry_run_does_not_seed_or_prompt(monkeypatch, capsys, tmp_path):
    prompted = {"asked": False}
    monkeypatch.setattr(
        "builtins.input", lambda *a: prompted.__setitem__("asked", True) or "y"
    )
    rc = main(_new_argv(tmp_path, "--dry-run"))
    out = capsys.readouterr().out
    assert rc == 0
    assert prompted["asked"] is False
    assert "Seed its charter now?" not in out
    assert "no intent yet" not in out
