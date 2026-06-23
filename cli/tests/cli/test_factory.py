"""Tests for the `dsf` factory CLI (`dsf new` and `dsf delete`)."""

from __future__ import annotations

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
        ]
    )
    assert args.command == "bootstrap"
    assert args.app_name == "dsf-acme"
    assert args.keyvault_name == "kv-dsf-app"


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
    assert "register_product" in out
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


def test_factory_parser_rejects_runtime_command():
    # the factory CLI must NOT expose runtime ops — those live in dsfctl
    with pytest.raises(SystemExit):
        build_parser().parse_args(["run"])


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
                    "· Microsoft.App/containerApps dsf-orchestrator-demo: ✓ Succeeded (1m04s)"
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
        "[dsf]     · Microsoft.App/containerApps dsf-orchestrator-demo: ✓ Succeeded" in out
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
    assert "deregister_product" in out
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
        def __init__(self, manifest, *, run=None, repo_root=None, purge=False, delete_repo=True):
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
        def from_product(cls, product, *, run=None, repo_root=None, purge=False, delete_repo=True):
            from dsf.instance.spec import read_manifest
            manifest = read_manifest(product, repo_root)
            return cls(manifest, repo_root=repo_root, purge=purge)

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


def test_delete_execute_failure_exits_nonzero(capsys, tmp_path, monkeypatch):
    from dsf.instance import deprovisioner as dep_mod

    class _FailingDeprovisioner:
        def __init__(self, manifest, *, run=None, repo_root=None, purge=False, delete_repo=True):
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
        def from_product(cls, product, *, run=None, repo_root=None, purge=False, delete_repo=True):
            from dsf.instance.spec import read_manifest
            manifest = read_manifest(product, repo_root)
            return cls(manifest, repo_root=repo_root, purge=purge)

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
