"""``dsf charter`` — operate a product's human-owned charter (.dsf/charter.md).

``init`` interviews the owner and opens a PR adding the file; ``sync`` pulls the
file (local working copy by default, or a repo ref via the App) into Cosmos;
``status`` reports drift between the file and the stored charter. Each command
builds **only** the real ports it needs (ADR 0014 — no fakes), so e.g.
``sync``/``status`` from a local file need only the Cosmos endpoint.
"""

from __future__ import annotations

import argparse
import asyncio
import sys
import uuid
from collections.abc import Callable
from pathlib import Path

from dsf.charter.constitution import CONSTITUTION_PATH, render_constitution
from dsf.charter.interview import (
    DEFAULT_MAX_TURNS,
    MAX_TURNS_KEY,
    CharterInterviewer,
    InterviewerTurn,
)
from dsf.charter.markdown import git_blob_sha, render_charter
from dsf.charter.sync import CHARTER_PATH, sync_charter, sync_charter_text
from dsf.container import (
    AzureRuntimeSettings,
    build_charter_store,
    build_config_store,
    build_model_client,
    build_repo_app_client,
)
from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.instance.bootstrap_issue import render_bootstrap_issue
from dsf.instance.runtime_render import runtime_endpoint_env
from dsf.instance.spec import read_manifest
from dsf.ports import CodingAgentAssignmentError


def _manifest_runtime_env(product: str) -> dict[str, str]:
    """Azure backing-service endpoints for ``product`` from its instance manifest.

    ``dsf new`` persists each product's Azure deployment outputs to
    ``config/instances/<product>.json``. Translate them to the ``AZURE_*`` env the
    charter clients read, so a freshly-provisioned product works without
    re-exporting endpoints. A missing or unreadable manifest yields ``{}`` (the
    operator can still export the env explicitly).
    """
    try:
        manifest = read_manifest(product)
    except (OSError, ValueError):
        return {}
    outputs = manifest.azure.outputs if manifest.azure else {}
    return runtime_endpoint_env(outputs)


def _base_env(product: str) -> dict[str, str]:
    """``os.environ`` layered over manifest-derived endpoints, product forced last.

    Manifest values only fill gaps; an explicitly exported env var always wins.
    """
    import os

    return {**_manifest_runtime_env(product), **os.environ, "DSF_PRODUCT": product}


def _settings(product: str) -> AzureRuntimeSettings:
    """Runtime settings with the operator's ``--product`` as the active product."""
    return AzureRuntimeSettings.from_env(_base_env(product))


# The one master DSF GitHub App identity is seeded into the owner Key Vault by
# ``dsf bootstrap`` under these secret names (see dsf.instance.app_bootstrap).
_OWNER_KV_ENV = "DSF_OWNER_KEYVAULT_URI"
_APP_ID_SECRET = "github-app-id"
_INSTALLATION_SECRET = "github-app-installation-id"
_PRIVATE_KEY_SECRET = "github-app-private-key"


def _read_owner_secret(owner_keyvault_uri: str, secret_name: str) -> str:
    """Read a secret's value from the owner Key Vault via the ``az`` CLI."""
    import subprocess

    name = owner_keyvault_uri.split("//", 1)[-1].split(".", 1)[0]
    res = subprocess.run(
        [
            "az", "keyvault", "secret", "show",
            "--vault-name", name,
            "--name", secret_name,
            "--query", "value", "-o", "tsv",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    return res.stdout.strip()


def _app_settings(
    product: str,
    *,
    secret_reader: Callable[[str, str], str] = _read_owner_secret,
) -> AzureRuntimeSettings:
    """Settings for the App-backed charter paths (``init`` and ``--ref``).

    Backing-service endpoints (App Configuration, Cosmos, Azure OpenAI) are
    gap-filled from the product's instance manifest via :func:`_base_env`. The
    GitHub App credentials are derived from the owner Key Vault: when the operator
    has not set the App env explicitly but ``DSF_OWNER_KEYVAULT_URI`` is exported
    (as it is for ``dsf new``), read the App id, installation id and private-key
    pointer from that owner vault and resolve the product repo from the registry —
    so ``dsf charter init`` works straight after provisioning. Explicit env always
    wins, and the owner Key Vault always backs the App private key.
    """
    import os

    env = _base_env(product)
    already_set = all(
        os.environ.get(name)
        for name in (
            "GITHUB_APP_ID",
            "GITHUB_INSTALLATION_ID",
            "GITHUB_APP_PRIVATE_KEY_SECRET",
            "AZURE_KEYVAULT_URI",
        )
    )
    owner_kv = (os.environ.get(_OWNER_KV_ENV) or "").strip()
    if owner_kv and not already_set:
        env["AZURE_KEYVAULT_URI"] = owner_kv
        env["GITHUB_APP_PRIVATE_KEY_SECRET"] = _PRIVATE_KEY_SECRET
        env["GITHUB_APP_ID"] = secret_reader(owner_kv, _APP_ID_SECRET)
        env["GITHUB_INSTALLATION_ID"] = secret_reader(owner_kv, _INSTALLATION_SECRET)
        if not env.get("GITHUB_REPOSITORY"):
            env["GITHUB_REPOSITORY"] = _resolve_repo(product) or ""
    return AzureRuntimeSettings.from_env(env)


def _resolve_repo(product: str) -> str | None:
    """Resolve ``product`` to its ``owner/name`` repo via the product registry."""
    from dsf.config.registry import load_registry, route_product

    match = route_product([product], load_registry())
    return match.github_repo if match else None


async def _run_interview(
    interviewer: CharterInterviewer,
    *,
    reader: Callable[[str], str] = input,
    writer: Callable[..., None] = print,
) -> Charter:
    """Drive the interviewer to a final draft, reading/writing via the given I/O."""
    turn: InterviewerTurn = await interviewer.start()
    while not turn.done:
        writer(f"\n[interviewer] {turn.message}")
        turn = await interviewer.respond(reader("[you] "))
    writer(f"\n[interviewer] {turn.message}")
    if turn.draft is None:
        raise RuntimeError("interview finished without a draft")
    return turn.draft


def _live_blob_sha(args: argparse.Namespace, product: str) -> tuple[str | None, str | None]:
    """Return the live charter blob SHA, plus a note when it cannot be read.

    From the repo at ``--ref`` (via the App) or a local ``--file`` (default
    ``.dsf/charter.md``). A ``None`` SHA means the file is absent / unreadable.
    """
    if args.ref is not None:
        repo_full = _resolve_repo(product)
        if not repo_full:
            return None, f"product {product!r} is not in registry"
        try:
            app = build_repo_app_client(_app_settings(product))
        except ValueError as exc:
            return None, str(exc)
        file = asyncio.run(app.read_file(repo_full, CHARTER_PATH, ref=args.ref))
        if file is None:
            return None, f"{CHARTER_PATH} not found on {args.ref}"
        return file.sha, None

    path = Path(args.file or CHARTER_PATH)
    try:
        data = path.read_bytes()
    except OSError:
        return None, f"no local charter file at {path}"
    return git_blob_sha(data), None


def _status_label(stored: StoredCharter | None, live_sha: str | None) -> str:
    """Classify drift between the stored charter and the live file/ref."""
    if live_sha is None:
        return "missing"
    if stored is None or stored.charter is None:
        return "stale"  # file present but nothing good stored yet -> run sync
    if stored.status == CharterStatus.INVALID:
        return "invalid"
    if stored.charter.source_sha != live_sha:
        return "stale"
    return "ok"


def _cmd_charter_status(args: argparse.Namespace) -> int:
    """Print the stored charter status and its drift vs the file/ref."""
    product = args.product
    try:
        store = build_charter_store(_settings(product))
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    stored = asyncio.run(store.get_charter(product))
    live_sha, note = _live_blob_sha(args, product)
    print(f"[dsf] charter {product}: {_status_label(stored, live_sha)}")
    if stored is not None:
        if stored.last_synced_at is not None:
            print(f"[dsf]   last_synced_at={stored.last_synced_at.isoformat()}")
        if stored.charter is not None and stored.charter.source_sha:
            print(
                f"[dsf]   stored_sha={stored.charter.source_sha} "
                f"ref={stored.charter.source_ref}"
            )
        if stored.last_error:
            print(f"[dsf]   last_error={stored.last_error}")
    if note:
        print(f"[dsf]   note: {note}")
    elif live_sha is not None:
        print(f"[dsf]   file_sha={live_sha}")
    return 0


def _cmd_charter_sync(args: argparse.Namespace) -> int:
    """Pull the charter into Cosmos from a local file (default) or a repo ref."""
    product = args.product
    try:
        store = build_charter_store(_settings(product))
        if args.ref is not None:
            repo_full = _resolve_repo(product)
            if not repo_full:
                print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
                return 1
            app = build_repo_app_client(_app_settings(product))
            stored = asyncio.run(
                sync_charter(store, app, product=product, repo=repo_full, ref=args.ref)
            )
        else:
            path = Path(args.file or CHARTER_PATH)
            try:
                data = path.read_bytes()
            except OSError as exc:
                print(f"[dsf] error: cannot read {path}: {exc}", file=sys.stderr)
                return 1
            stored = asyncio.run(
                sync_charter_text(
                    store,
                    product=product,
                    text=data.decode("utf-8"),
                    source_sha=git_blob_sha(data),
                    source_ref=f"file:{path}",
                )
            )
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    print(f"[dsf] synced charter for {product}: {stored.status.value}")
    if stored.last_error:
        print(f"[dsf]   {stored.last_error}")
    return 1 if stored.status == CharterStatus.INVALID else 0


def _cmd_charter_init(args: argparse.Namespace) -> int:
    """Interview the owner to draft a charter, then open a PR adding .dsf/charter.md."""
    product = args.product
    repo_full = _resolve_repo(product)
    if not repo_full:
        print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
        return 1

    settings = _app_settings(product)
    try:
        app = build_repo_app_client(settings)
        model = build_model_client(settings)
        config = build_config_store(settings)
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        if "GitHub App" in str(exc):
            print(
                "[dsf] hint: export DSF_OWNER_KEYVAULT_URI=<owner Key Vault uri> "
                "(printed by `dsf bootstrap`) to derive the App credentials "
                "automatically.",
                file=sys.stderr,
            )
        return 1

    max_turns = int(config.get_value(MAX_TURNS_KEY, DEFAULT_MAX_TURNS))
    interviewer = CharterInterviewer(model, product, max_turns=max_turns)
    draft = asyncio.run(_run_interview(interviewer))
    markdown = render_charter(draft)
    print("\n[dsf] proposed charter:\n")
    print(markdown)

    branch = f"charter/init-{uuid.uuid4().hex[:8]}"
    url = asyncio.run(
        app.open_file_pr(
            repo_full,
            path=CHARTER_PATH,
            content=markdown,
            branch=branch,
            title=f"Add product charter for {product}",
            body=(
                "Human-owned Product Charter drafted via `dsf charter init`. "
                "Review, edit, and merge to make it authoritative; the factory "
                "never edits it."
            ),
            message=f"docs: add product charter for {product}",
        )
    )
    print(f"[dsf] opened charter PR: {url}")
    return 0


def _cmd_charter_implement(args: argparse.Namespace) -> int:
    """Seed the Spec Kit build from an accepted charter.

    Renders the constitution from the synced charter and lands it via an
    auto-merged PR (``main`` is branch-protected), then files one ``creation:ready``
    bootstrap issue assigned to the Copilot Coding Agent. Refuses unless the charter
    is present and synced OK against ``main`` (reusing the ``status`` drift logic),
    so we never seed from a non-accepted charter.
    """
    product = args.product
    repo_full = _resolve_repo(product)
    if not repo_full:
        print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
        return 1

    try:
        store = build_charter_store(_settings(product))
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    stored = asyncio.run(store.get_charter(product))
    live_sha, note = _live_blob_sha(argparse.Namespace(ref="main", file=None), product)
    label = _status_label(stored, live_sha)
    if label != "ok":
        print(
            f"[dsf] error: charter for {product} is {label}; merge it and run "
            "`dsf charter sync --product "
            f"{product} --ref main` before implementing.",
            file=sys.stderr,
        )
        if note:
            print(f"[dsf]   note: {note}", file=sys.stderr)
        return 1

    charter = stored.charter  # non-None when label == "ok"
    try:
        app = build_repo_app_client(_app_settings(product))
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1

    constitution = render_constitution(charter)
    branch = f"charter/constitution-{uuid.uuid4().hex[:8]}"
    pr_url = asyncio.run(
        app.open_file_pr(
            repo_full,
            path=CONSTITUTION_PATH,
            content=constitution,
            branch=branch,
            title=f"Add Spec Kit constitution for {product}",
            body=(
                "Constitution derived from the product charter by "
                "`dsf charter implement`. Auto-merge is requested: on repos where "
                "it is enabled this merges once the `ci` check is green, otherwise "
                "it awaits a human review. (Creation-maturity gating is future "
                "scope.)"
            ),
            message=f"docs: add spec kit constitution for {product}",
            enable_auto_merge=True,
        )
    )
    print(f"[dsf] opened constitution PR (auto-merge requested): {pr_url}")

    title, body = render_bootstrap_issue(charter)
    try:
        issue_url = asyncio.run(app.create_issue(repo_full, title, body, [HANDOFF_LABEL]))
        print(f"[dsf] filed bootstrap issue + assigned Copilot: {issue_url}")
    except CodingAgentAssignmentError as exc:
        print(f"[dsf] filed bootstrap issue: {exc.issue_url}")
        print(
            f"[dsf] warning: Copilot coding agent assignment FAILED ({exc}); "
            "assign Copilot manually once enabled.",
            file=sys.stderr,
        )
    return 0


def charter_init(product: str) -> int:
    """Run the charter interview and open the PR for ``product``.

    Public entry point so ``dsf new`` can chain straight into charter seeding
    after provisioning without reaching for a private command handler.
    """
    return _cmd_charter_init(argparse.Namespace(product=product))


def add_charter_subcommands(sub: argparse._SubParsersAction) -> None:
    """Register the ``charter`` command (init/sync/status) on ``sub``."""
    parser = sub.add_parser("charter", help="manage the product charter (.dsf/charter.md)")
    charter_sub = parser.add_subparsers(dest="charter_command", required=True)

    init_parser = charter_sub.add_parser(
        "init", help="interview to draft a charter and open a PR"
    )
    init_parser.add_argument("--product", required=True, help="product key")
    init_parser.set_defaults(func=_cmd_charter_init)

    implement_parser = charter_sub.add_parser(
        "implement",
        help="render the constitution + file the Spec Kit bootstrap issue",
    )
    implement_parser.add_argument("--product", required=True, help="product key")
    implement_parser.set_defaults(func=_cmd_charter_implement)

    for name, func, help_text in (
        ("sync", _cmd_charter_sync, "pull .dsf/charter.md (local file or --ref) into Cosmos"),
        ("status", _cmd_charter_status, "show the stored charter status + drift"),
    ):
        command_parser = charter_sub.add_parser(name, help=help_text)
        command_parser.add_argument("--product", required=True, help="product key")
        source = command_parser.add_mutually_exclusive_group()
        source.add_argument("--file", help="path to a local charter file (default .dsf/charter.md)")
        source.add_argument("--ref", help="read the charter from this repo ref via the GitHub App")
        command_parser.set_defaults(func=func)


__all__ = ["add_charter_subcommands", "charter_init"]
