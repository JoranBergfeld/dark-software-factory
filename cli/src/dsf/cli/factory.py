"""``dsf`` — create and manage product factory instances from the template.

``dsf new`` provisions an isolated product factory: its own GitHub repo + Coding
Squad, a dedicated Azure resource group, and the per-product feature-council runtime
deployed to Azure Container Apps. ``dsf offboard`` is the inverse teardown that keeps
the repo but removes Azure/runtime/registry artifacts.
"""

from __future__ import annotations

import argparse
from pathlib import Path


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


def _print_step_event(phase, index, total, step, error) -> None:
    """Live per-step progress for an executing lifecycle run."""
    if phase == "start":
        print(f"[dsf] ▶ [{index}/{total}] {step.name}: {step.description}", flush=True)
    elif phase == "done":
        print(f"[dsf]   ✓ {step.name}: {step.result}", flush=True)
    elif phase == "error":
        print(f"[dsf]   ✗ {step.name} FAILED: {error}", flush=True)


def _cmd_new(args: argparse.Namespace) -> int:
    """Create (or preview) a new isolated product factory instance."""
    from dsf.instance.naming import make_name_prefix
    from dsf.instance.provisioner import InstanceProvisioner
    from dsf.instance.spec import InstanceSpec, manifest_path, read_manifest

    root = Path(args.config_root) if args.config_root else None
    # Idempotent effective prefix: reuse the persisted one if the instance exists,
    # otherwise derive a fresh randomized prefix from the supplied base.
    if manifest_path(args.product, root).exists():
        name_prefix = read_manifest(args.product, repo_root=root).spec.name_prefix
    else:
        name_prefix = make_name_prefix(args.name_prefix)

    spec = InstanceSpec(
        product=args.product,
        owner=args.owner,
        repo=args.repo or "",
        visibility=args.visibility,
        runtime_target=args.runtime_target,
        name_prefix=name_prefix,
        environment=args.environment,
        location=args.location,
        squad_maturity=args.squad_maturity,
    )
    prov = InstanceProvisioner(spec, repo_root=root)
    execute = not args.dry_run
    if execute:
        plan = prov.apply(execute=True, on_event=_print_step_event).plan
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
    return 0


def _cmd_offboard(args: argparse.Namespace) -> int:
    """Remove Azure/runtime/registry artifacts for one product."""
    from dsf.instance.provisioner import InstanceOffboarder

    root = Path(args.config_root) if args.config_root else None
    execute = not args.dry_run
    if execute and not args.yes:
        confirmation = input(
            f"[dsf] Offboard '{args.product}'? This deletes Azure resources and local instance "
            f"artifacts but keeps the GitHub repo. Type '{args.product}' to confirm: "
        )
        if confirmation.strip() != args.product:
            print("[dsf] offboard aborted (confirmation mismatch)")
            return 1

    offboarder = InstanceOffboarder(
        args.product,
        repo_root=root,
        purge=args.purge,
    )
    plan = offboarder.apply(execute=execute, on_event=_print_step_event)
    _print_plan(plan, execute=execute)
    failed = next((s for s in plan.steps if s.result == "failed"), None)
    if failed:
        print(f"[dsf] offboard STOPPED at '{failed.name}': {failed.error}")
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
    p_new.add_argument("--owner", required=True, help="GitHub owner/org for the product repo")
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
        required=True,
        help="base Azure resource name prefix (sanitized + randomized to <=12 lowercase chars)",
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
        "--squad-maturity",
        default="low",
        choices=["low", "high"],
        help="coding-squad autonomy: 'low' routes every PR to a human, "
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
    p_new.set_defaults(func=_cmd_new)

    p_offboard = sub.add_parser("offboard", help="remove Azure/runtime artifacts for a product")
    p_offboard.add_argument("product", help="product key to offboard (e.g. 'microbi')")
    p_offboard.add_argument(
        "--dry-run",
        action="store_true",
        help="preview only: print the teardown plan without side effects",
    )
    p_offboard.add_argument(
        "--yes",
        action="store_true",
        help="skip interactive confirmation for destructive delete steps",
    )
    p_offboard.add_argument(
        "--purge",
        action="store_true",
        help="also purge soft-deleted Key Vault/Foundry resources for name reuse",
    )
    p_offboard.add_argument(
        "--config-root",
        default=None,
        help="override repo root where config/instances/ and config/products.json live",
    )
    p_offboard.set_defaults(func=_cmd_offboard)

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
