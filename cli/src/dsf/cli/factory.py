"""``dsf`` — create and manage product factory instances from the template.

``dsf new`` provisions an isolated product factory: its own GitHub repo + Coding
Squad, a dedicated Azure resource group, and the per-product feature-council runtime
deployed to Azure Container Apps. Future lifecycle verbs (``status``/``upgrade``/
``destroy``, SP7) will live here too.
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path


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


def _print_plan(plan, *, execute: bool = False) -> None:
    """Print an instance provisioning plan in a compact, readable form."""
    mode = "EXECUTE" if execute else "DRY-RUN"
    print(f"[dsf] instance plan for product={plan.product} ({mode})")
    for i, step in enumerate(plan.steps, 1):
        status = step.result or ("deferred" if step.deferred else "planned")
        print(f"[dsf]  {i}. {step.name:14s} [{status}] {step.description}")
        if step.command:
            print(f"[dsf]       $ {' '.join(step.command)}")
        if step.error:
            print(f"[dsf]       ! {step.error}")


def _print_teardown_plan(plan, *, execute: bool = False) -> None:
    """Print a teardown plan in a compact, readable form."""
    mode = "EXECUTE" if execute else "DRY-RUN"
    print(f"[dsf] teardown plan for product={plan.product} ({mode})")
    for i, step in enumerate(plan.steps, 1):
        status = step.result or "planned"
        print(f"[dsf]  {i}. {step.name:20s} [{status}] {step.description}")
        if step.command:
            print(f"[dsf]       $ {' '.join(step.command)}")
        if step.error:
            print(f"[dsf]       ! {step.error}")


def _print_step_event(phase, index, total, step, error) -> None:
    """Live per-step progress for an executing ``dsf`` run."""
    if phase == "start":
        print(f"[dsf] ▶ [{index}/{total}] {step.name}: {step.description}", flush=True)
    elif phase == "done":
        print(f"[dsf]   ✓ {step.name}: {step.result}", flush=True)
    elif phase == "error":
        print(f"[dsf]   ✗ {step.name} FAILED: {error}", flush=True)


def _print_step_progress(line: str) -> None:
    """Live per-resource progress under an executing step (indented)."""
    print(f"[dsf]     {line}", flush=True)


def charter_next_action(product: str) -> str:
    """The post-provision hint pointing the operator at the charter workflow."""
    return (
        f"[dsf] next: run `dsf charter init --product {product}` to author the "
        "product charter (opens a PR for review)"
    )


def _cmd_new(args: argparse.Namespace) -> int:
    """Create (or preview) a new isolated product factory instance."""
    import os

    from dsf.instance.github_identity import OwnerResolutionError, resolve_owner
    from dsf.instance.naming import make_name_prefix
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec, manifest_path, read_manifest

    try:
        owner = resolve_owner(args.owner)
    except OwnerResolutionError as exc:
        print(f"[dsf] error: {exc}")
        return 1

    root = Path(args.config_root) if args.config_root else None
    # Idempotent effective prefix: reuse the persisted one if the instance exists,
    # otherwise derive a fresh randomized prefix. The base defaults to the product
    # key when --name-prefix is omitted.
    if manifest_path(args.product, root).exists():
        name_prefix = read_manifest(args.product, repo_root=root).spec.name_prefix
    else:
        prefix_base = args.name_prefix or args.product
        try:
            name_prefix = make_name_prefix(prefix_base)
        except ValueError as exc:
            print(
                f"[dsf] error: cannot derive an Azure name prefix from "
                f"{prefix_base!r}: {exc} Pass --name-prefix explicitly."
            )
            return 1

    spec = InstanceSpec(
        product=args.product,
        owner=owner,
        repo=args.repo or "",
        visibility=args.visibility,
        runtime_target=args.runtime_target,
        name_prefix=name_prefix,
        environment=args.environment,
        location=args.location,
        creation_maturity=args.creation_maturity,
    )
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
    execute = not args.dry_run
    if execute:
        plan = prov.apply(
            execute=True, on_event=_print_step_event, on_progress=_print_step_progress
        ).plan
        print()  # separate the live progress from the final summary
    elif args.write_plan:
        plan = prov.apply(execute=False).plan
    else:
        plan = prov.plan()
    _print_plan(plan, execute=execute)
    failed = next((s for s in plan.steps if s.result == "failed"), None)
    if failed:
        print(f"[dsf] provisioning STOPPED at '{failed.name}': {failed.error}")
        return 1
    if execute:
        print(charter_next_action(args.product))
    return 0


def _cmd_bootstrap(args: argparse.Namespace) -> int:
    """Create the one-time owner-level DSF GitHub App and store it in the owner KV."""
    from dsf.instance.app_bootstrap import BootstrapConfig, _browser_capture_code, bootstrap_app

    cfg = BootstrapConfig(
        app_name=args.app_name,
        resource_group=args.resource_group,
        keyvault_name=args.keyvault_name,
        location=args.location,
    )
    result = bootstrap_app(cfg, capture_code=_browser_capture_code)
    print(
        f"[dsf] DSF GitHub App created: app_id={result.app_id} "
        f"installation_id={result.installation_id}"
    )
    print(f"[dsf] master credentials stored in owner Key Vault {result.keyvault_name}")
    print(f"[dsf] now export DSF_OWNER_KEYVAULT_URI={result.keyvault_uri} for `dsf new`")
    return 0


def _cmd_delete(args: argparse.Namespace) -> int:
    """Tear down (or preview tearing down) an existing product factory instance.

    Reads the persisted manifest to resolve all resource names, then runs the
    teardown in safe order: Azure resources first, GitHub repo last.
    """
    from dsf.instance.deprovisioner import InstanceDeprovisioner
    from dsf.instance.spec import manifest_path

    root = Path(args.config_root) if args.config_root else None

    if not manifest_path(args.product, root).exists():
        print(
            f"[dsf] error: no manifest found for product '{args.product}'. "
            "Run 'dsf new' first or check the product name.",
            file=sys.stderr,
        )
        return 1

    execute = not args.dry_run

    # Safety guard: repo deletion is irreversible — require explicit confirmation
    # unless --yes is passed. In non-interactive contexts --yes is mandatory.
    if execute and not args.yes:
        if not sys.stdin.isatty():
            print(
                "[dsf] error: '--yes' is required to delete in non-interactive mode.",
                file=sys.stderr,
            )
            return 1
        confirm = input(
            f"[dsf] DANGER: this will permanently destroy '{args.product}' "
            f"including the GitHub repo.\n"
            f"[dsf] Type the product name to confirm: "
        )
        if confirm.strip() != args.product:
            print("[dsf] deletion cancelled (product name did not match).", file=sys.stderr)
            return 1

    try:
        deprv = InstanceDeprovisioner.from_product(
            args.product,
            repo_root=root,
            purge=args.purge,
        )
    except (FileNotFoundError, OSError, ValueError) as exc:
        print(f"[dsf] error: could not load manifest: {exc}", file=sys.stderr)
        return 1

    if execute:
        plan = deprv.apply(execute=True, on_event=_print_step_event)
        print()  # separate live progress from final summary
    else:
        plan = deprv.apply(execute=False)

    _print_teardown_plan(plan, execute=execute)

    failed = next((s for s in plan.steps if s.result == "failed"), None)
    if failed:
        print(f"[dsf] teardown STOPPED at '{failed.name}': {failed.error}")
        return 1
    return 0


def build_parser() -> argparse.ArgumentParser:
    """Build the ``dsf`` parser with the instance-lifecycle subcommands."""
    parser = argparse.ArgumentParser(
        prog="dsf",
        description="Dark Software Factory — factory CLI (create product instances)",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    p_new = sub.add_parser("new", help="create a new isolated product factory instance")
    p_new.add_argument("--product", required=True, help="product key (e.g. 'microbi')")
    p_new.add_argument(
        "--owner",
        default="",
        help="GitHub owner/org for the product repo "
        "(default: the gh-authenticated account, resolved via `gh api user`)",
    )
    p_new.add_argument("--repo", default="", help="repo name (defaults to product key)")
    p_new.add_argument(
        "--visibility",
        default="private",
        choices=["private", "public", "internal"],
        help="product repo visibility",
    )
    p_new.add_argument(
        "--runtime-target",
        default="aca",
        choices=["aca"],
        help="where the factory runtime is hosted",
    )
    p_new.add_argument(
        "--name-prefix",
        default="",
        help="base Azure resource name prefix, sanitized + randomized to <=12 "
        "lowercase chars (default: derived from --product)",
    )
    p_new.add_argument(
        "--environment",
        default="dev",
        help="Azure environment moniker (Bicep environmentName)",
    )
    p_new.add_argument(
        "--location",
        default="swedencentral",
        help="Azure region for the resource group and resources",
    )
    p_new.add_argument(
        "--creation-maturity",
        default="low",
        choices=["low", "high"],
        help="creation-phase autonomy: 'low' routes every PR to a human, "
        "'high' auto-merges on green CI",
    )
    p_new.add_argument(
        "--dry-run",
        action="store_true",
        help="preview only: print the what-if plan without running any steps "
        "(provisioning executes by default)",
    )
    p_new.add_argument(
        "--write-plan",
        action="store_true",
        help="with --dry-run, still write the instance manifest to config/instances/",
    )
    p_new.add_argument(
        "--config-root",
        default=None,
        help="override repo root where config/instances/ is written (tests/CI)",
    )
    p_new.add_argument(
        "--owner-keyvault-uri",
        default="",
        help="owner Key Vault holding the DSF App credentials "
        "(default: $DSF_OWNER_KEYVAULT_URI; required to install the App)",
    )
    p_new.set_defaults(func=_cmd_new)

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

    p_delete = sub.add_parser(
        "delete",
        help="permanently destroy a product factory instance (full inverse of 'new')",
    )
    p_delete.add_argument("product", help="product key to destroy (e.g. 'microbi')")
    p_delete.add_argument(
        "--yes",
        action="store_true",
        help="skip the interactive confirmation prompt (required in non-interactive mode)",
    )
    p_delete.add_argument(
        "--dry-run",
        action="store_true",
        help="preview only: print the full teardown plan without running any steps",
    )
    p_delete.add_argument(
        "--purge",
        action="store_true",
        help="purge soft-deleted Key Vault after the resource group is deleted "
        "(frees the name for immediate reuse)",
    )
    p_delete.add_argument(
        "--config-root",
        default=None,
        help="override repo root where config/instances/ is read from (tests/CI)",
    )
    p_delete.set_defaults(func=_cmd_delete)

    from dsf.cli.charter import add_charter_subcommands

    add_charter_subcommands(sub)

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
