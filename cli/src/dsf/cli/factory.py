"""``dsf`` — create and manage product factory instances from the template.

``dsf new`` provisions an isolated product factory: its own GitHub repo + Coding
Squad, a dedicated Azure resource group, and the per-product feature-council runtime
deployed to Azure Container Apps. ``dsf offboard`` is the inverse teardown that keeps
the repo but removes Azure/runtime/registry artifacts.
"""

from __future__ import annotations

import argparse
import subprocess
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


def _owner_app_wiring_warnings(
    owner_keyvault_uri: str, owner_appconfig_endpoint: str
) -> list[str]:
    """Return warnings for owner-level App wiring inputs missing from ``dsf new``."""
    if not owner_keyvault_uri:
        return [
            "[dsf] WARNING: DSF_OWNER_KEYVAULT_URI is unset and --owner-keyvault-uri "
            "was not passed.",
            "[dsf] WARNING: install_app, seed_app_key, seed_webiq_key, "
            "publish_runtime_index will be SKIPPED.",
            "[dsf] WARNING: the GitHub App won't be wired; `dsf charter init` and "
            "runtime GitHub access will fail.",
            "[dsf] WARNING: fix: run `dsf bootstrap` once, then export "
            "DSF_OWNER_KEYVAULT_URI and DSF_OWNER_APPCONFIG_ENDPOINT, then re-run "
            "`dsf new`.",
        ]
    if not owner_appconfig_endpoint:
        return [
            "[dsf] note: DSF_OWNER_APPCONFIG_ENDPOINT is unset and "
            "--owner-appconfig-endpoint was not passed; publish_runtime_index will "
            "be SKIPPED.",
        ]
    return []


def charter_next_action(product: str) -> str:
    """The bare, copy-pasteable command that seeds a factory's product charter."""
    return f"uv run dsf charter init --product {product}"


def charter_guidance(product: str) -> list[str]:
    """Framed next-step guidance for a factory that has no intent (charter) yet.

    Names the full path to a *charted* factory so the operator knows provisioning
    alone is not enough: provisioning -> charter PR -> merge -> ``dsf sweep``.
    """
    return [
        "[dsf] your factory has no intent yet — seed it with a product charter:",
        f"[dsf]   {charter_next_action(product)}",
        "[dsf] then review & MERGE the charter PR; the next `dsf sweep` makes it "
        "authoritative.",
    ]


def _charter_state(product: str) -> str:
    """Classify a factory's charter readiness via the master DSF GitHub App.

    Returns ``"seeded"`` when ``.dsf/charter.md`` is already on ``main`` or a
    ``charter/*`` PR exists, ``"greenfield"`` when neither does, or ``"unknown"``
    when the App client can't be built, the repo can't be resolved, or the lookup
    errors. The check is best-effort: any failure degrades to ``"unknown"`` so
    ``dsf new`` never crashes on it.
    """
    import asyncio

    from dsf.cli import charter as charter_cli

    try:
        repo_full = charter_cli._resolve_repo(product)
        if not repo_full:
            return "unknown"
        app = charter_cli.build_repo_app_client(charter_cli._app_settings(product))

        async def _lookup() -> bool:
            on_main = await app.read_file(repo_full, charter_cli.CHARTER_PATH, ref="main")
            if on_main is not None:
                return True
            pr = await app.latest_pr_with_head_prefix(repo_full, head_prefix="charter/")
            return pr is not None

        seeded = asyncio.run(_lookup())
    except Exception:
        return "unknown"
    return "seeded" if seeded else "greenfield"


def _maybe_seed_charter(product: str, *, no_charter: bool) -> None:
    """Guide the operator into seeding the new factory's charter.

    Layered on top of a successful provisioning run — best-effort and never
    affecting ``dsf new``'s exit code. On a greenfield factory with an interactive
    TTY it offers to chain straight into ``dsf charter init``; otherwise it prints
    a clear, copy-pasteable next step. Skips entirely (apart from the hint) when
    ``--no-charter`` is passed, stdin isn't a TTY, or the factory already has a
    charter (PR open or on ``main``).
    """
    if no_charter:
        for line in charter_guidance(product):
            print(line)
        return

    state = _charter_state(product)
    if state == "seeded":
        print(
            "[dsf] factory already has a product charter (PR open or on main); "
            "nothing to seed."
        )
        return

    # Non-interactive or undeterminable greenfield: never block on a prompt, never
    # run the interview blind — just point the way.
    if state == "unknown" or not sys.stdin.isatty():
        for line in charter_guidance(product):
            print(line)
        return

    answer = input(
        "[dsf] Your factory has no intent yet. Seed its charter now? [Y/n] "
    ).strip().lower()
    if answer not in ("", "y", "yes"):
        for line in charter_guidance(product):
            print(line)
        return

    from dsf.cli.charter import charter_init

    rc = charter_init(product)
    if rc == 0:
        print(
            "[dsf] charter PR opened — review & MERGE it; the next `dsf sweep` "
            "makes it authoritative."
        )
    else:
        print(
            "[dsf] charter seeding did not complete; retry later with: "
            f"{charter_next_action(product)}"
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
    owner_appconfig = args.owner_appconfig_endpoint or os.environ.get(
        "DSF_OWNER_APPCONFIG_ENDPOINT", ""
    )
    for warning in _owner_app_wiring_warnings(owner_kv, owner_appconfig):
        print(warning)
    admin_principal_id = args.admin_principal_id or os.environ.get(
        "DSF_ADMIN_PRINCIPAL_ID", ""
    )
    app_id, installation_id = "", ""
    if owner_kv and not args.dry_run:
        app_id, installation_id = _read_owner_app_pointers(owner_kv)
    prov = InstanceProvisioner(
        spec,
        repo_root=root,
        owner_keyvault_uri=owner_kv,
        owner_appconfig_endpoint=owner_appconfig,
        github_app_id=app_id,
        github_installation_id=installation_id,
        admin_principal_id=admin_principal_id,
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
        _maybe_seed_charter(args.product, no_charter=args.no_charter)
    return 0


def _cmd_bootstrap(args: argparse.Namespace) -> int:
    """Create the one-time owner-level DSF GitHub App and store it in the owner KV."""
    from dsf.instance.app_bootstrap import BootstrapConfig, _browser_capture_code, bootstrap_app

    cfg = BootstrapConfig(
        app_name=args.app_name,
        resource_group=args.resource_group,
        keyvault_name=args.keyvault_name,
        appconfig_name=args.appconfig_name,
        location=args.location,
    )
    result = bootstrap_app(cfg, capture_code=_browser_capture_code)
    print(
        f"[dsf] DSF GitHub App created: app_id={result.app_id} "
        f"installation_id={result.installation_id}"
    )
    print(f"[dsf] master credentials stored in owner Key Vault {result.keyvault_name}")
    print(f"[dsf] owner App Configuration index ready: {result.appconfig_name}")
    print(f"[dsf] now export DSF_OWNER_KEYVAULT_URI={result.keyvault_uri} for `dsf new`")
    print(
        f"[dsf] and  export DSF_OWNER_APPCONFIG_ENDPOINT={result.appconfig_endpoint} "
        "for `dsf new` / `dsf sweep --product`"
    )
    return 0


def _cmd_offboard(args: argparse.Namespace) -> int:
    """Remove Azure/runtime/registry artifacts for one product."""
    import os

    from dsf.instance.provisioner import InstanceOffboarder

    root = Path(args.config_root) if args.config_root else None
    owner_appconfig = args.owner_appconfig_endpoint or os.environ.get(
        "DSF_OWNER_APPCONFIG_ENDPOINT", ""
    )
    execute = not args.dry_run
    if execute and not args.yes:
        try:
            confirmation = input(
                f"[dsf] Offboard '{args.product}'? This deletes Azure resources and local "
                f"instance artifacts but keeps the GitHub repo. "
                f"Type '{args.product}' to confirm: "
            )
        except (KeyboardInterrupt, EOFError):
            print("\n[dsf] offboard aborted.")
            return 1
        if confirmation.strip() != args.product:
            print("[dsf] offboard aborted (confirmation mismatch)")
            return 1

    offboarder = InstanceOffboarder(
        args.product,
        repo_root=root,
        purge=args.purge,
        owner_appconfig_endpoint=owner_appconfig,
    )
    plan = offboarder.apply(execute=execute, on_event=_print_step_event)
    _print_plan(plan, execute=execute)
    failed = next((s for s in plan.steps if s.result == "failed"), None)
    if failed:
        print(f"[dsf] offboard STOPPED at '{failed.name}': {failed.error}")
        return 1
    return 0


def _cmd_delete(args: argparse.Namespace) -> int:
    """Tear down (or preview tearing down) an existing product factory instance.

    Reads the persisted manifest to resolve all resource names, then runs the
    teardown in safe order: Azure resources first, GitHub repo last.
    """
    import os

    from dsf.instance.deprovisioner import InstanceDeprovisioner
    from dsf.instance.spec import manifest_path

    root = Path(args.config_root) if args.config_root else None
    owner_appconfig = args.owner_appconfig_endpoint or os.environ.get(
        "DSF_OWNER_APPCONFIG_ENDPOINT", ""
    )

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
        try:
            confirm = input(
                f"[dsf] DANGER: this will permanently destroy '{args.product}' "
                f"including the GitHub repo.\n"
                f"[dsf] Type the product name to confirm: "
            )
        except (KeyboardInterrupt, EOFError):
            print("\n[dsf] deletion cancelled.", file=sys.stderr)
            return 1
        if confirm.strip() != args.product:
            print("[dsf] deletion cancelled (product name did not match).", file=sys.stderr)
            return 1

    try:
        deprv = InstanceDeprovisioner.from_product(
            args.product,
            repo_root=root,
            purge=args.purge,
            owner_appconfig_endpoint=owner_appconfig,
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


_RUNTIME_MODULE = "dsf.runtime.control"


def _forward_to_runtime(forward: list[str]) -> int:
    """Run a feature-council runtime verb in a subprocess and pass through its code.

    The cli member must not import the runtime (import-linter forbids it), so the
    front-door run/sweep/serve verbs shell out to
    ``python -m dsf.runtime.control <forward...>``.
    """
    completed = subprocess.run([sys.executable, "-m", _RUNTIME_MODULE, *forward])
    return completed.returncode


def _cmd_run(args: argparse.Namespace) -> int:
    forward = ["run"]
    if args.signal:
        forward += ["--signal", args.signal]
    if args.dry_run:
        forward.append("--dry-run")
    if args.product:
        forward += ["--product", args.product]
    return _forward_to_runtime(forward)


def _cmd_sweep(args: argparse.Namespace) -> int:
    forward = ["sweep"]
    if args.product:
        forward += ["--product", args.product]
    return _forward_to_runtime(forward)


def _cmd_serve_orchestrator(args: argparse.Namespace) -> int:
    forward = ["serve-orchestrator"]
    if args.loop:
        forward.append("--loop")
    if args.interval is not None:
        forward += ["--interval", str(args.interval)]
    if args.product:
        forward += ["--product", args.product]
    return _forward_to_runtime(forward)


def _cmd_serve_agent(args: argparse.Namespace) -> int:
    forward = [
        "serve-agent", "--kind", args.kind, "--host", args.host, "--port", str(args.port)
    ]
    return _forward_to_runtime(forward)


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
        "--no-charter",
        action="store_true",
        help="skip the post-provision charter prompt; just print the next step "
        "(`dsf charter init`). Always implied when stdin isn't a TTY.",
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
    p_new.add_argument(
        "--owner-appconfig-endpoint",
        default=None,
        help="owner App Configuration endpoint to publish this product's runtime env "
        "into (default: DSF_OWNER_APPCONFIG_ENDPOINT)",
    )
    p_new.add_argument(
        "--admin-principal-id",
        default="",
        help="object id of the human owner/governance principal to grant data-plane "
        "admin (App Config / Key Vault) + Reader on the SRE agent RG "
        "(default: $DSF_ADMIN_PRINCIPAL_ID, else the signed-in user; leave unset in "
        "CI / service-principal runs to skip the human grants)",
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
        help="override repo root where config/instances/ lives",
    )
    p_offboard.add_argument(
        "--owner-appconfig-endpoint",
        default=None,
        help="owner App Configuration endpoint to remove this product's runtime env "
        "from (default: DSF_OWNER_APPCONFIG_ENDPOINT)",
    )
    p_offboard.set_defaults(func=_cmd_offboard)
    p_boot = sub.add_parser(
        "bootstrap",
        help="one-time: create the DSF GitHub App and store it in the owner Key Vault",
    )
    p_boot.add_argument("--app-name", required=True, help="GitHub App name (globally unique)")
    p_boot.add_argument(
        "--keyvault-name", required=True, help="owner Key Vault name for App credentials"
    )
    p_boot.add_argument(
        "--appconfig-name",
        required=True,
        help="owner App Configuration store name for the runtime-config index",
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
    p_delete.add_argument(
        "--owner-appconfig-endpoint",
        default=None,
        help="owner App Configuration endpoint to remove this product's runtime env "
        "from (default: DSF_OWNER_APPCONFIG_ENDPOINT)",
    )
    p_delete.set_defaults(func=_cmd_delete)

    p_run = sub.add_parser("run", help="run the intake line for one signal (runtime)")
    p_run.add_argument("--signal", help="path to a signal JSON file")
    p_run.add_argument("--dry-run", action="store_true", help="run the line but skip filing")
    p_run.add_argument("--product", help="resolve runtime env for this product")
    p_run.set_defaults(func=_cmd_run)

    p_sweep = sub.add_parser("sweep", help="sweep enabled source agents once (runtime)")
    p_sweep.add_argument("--product", help="resolve runtime env for this product")
    p_sweep.set_defaults(func=_cmd_sweep)

    p_orch = sub.add_parser(
        "serve-orchestrator", help="run the orchestrator worker (runtime)"
    )
    p_orch.add_argument("--loop", action="store_true", help="sweep continuously")
    p_orch.add_argument("--interval", type=int, default=None, help="seconds between sweeps")
    p_orch.add_argument("--product", help="resolve runtime env for this product")
    p_orch.set_defaults(func=_cmd_serve_orchestrator)

    p_serve = sub.add_parser("serve-agent", help="serve a source agent over A2A (runtime)")
    p_serve.add_argument("--kind", default="sentry", help="source agent kind")
    p_serve.add_argument("--host", default="0.0.0.0", help="bind host")
    p_serve.add_argument("--port", type=int, default=8080, help="bind port")
    p_serve.set_defaults(func=_cmd_serve_agent)

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
