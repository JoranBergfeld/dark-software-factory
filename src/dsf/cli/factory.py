"""``dsf`` — create and manage product factory instances from the template.

``dsf new`` provisions an isolated product factory: its own GitHub repo + Coding
Squad, a dedicated Azure resource group, and the per-product feature-council runtime
rendered as a homelab compose bundle. Future lifecycle verbs (``status``/``upgrade``/
``destroy``, SP7) will live here too.
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
        workload_principal_id=args.workload_principal_id,
    )
    prov = InstanceProvisioner(spec, repo_root=root)
    if args.execute:
        plan = prov.apply(execute=True).plan
    elif args.write_plan:
        plan = prov.apply(execute=False).plan
    else:
        plan = prov.plan()
    _print_plan(plan, execute=args.execute)
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
        default="homelab",
        choices=["homelab", "aca"],
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
        "--workload-principal-id",
        default="",
        help="object id granted data-plane roles (empty = provision-only)",
    )
    p_new.add_argument(
        "--execute",
        action="store_true",
        help="run executable steps (gh/squad/az + council bring-up); SRE deploy stays deferred",
    )
    p_new.add_argument(
        "--write-plan",
        action="store_true",
        help="dry-run, but write the instance manifest to config/instances/",
    )
    p_new.add_argument(
        "--config-root",
        default=None,
        help="override repo root where config/instances/ is written (tests/CI)",
    )
    p_new.set_defaults(func=_cmd_new)

    return parser


def main(argv: list[str] | None = None) -> int:
    """Parse args and dispatch. Returns a process exit code."""
    parser = build_parser()
    args = parser.parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    raise SystemExit(main())
