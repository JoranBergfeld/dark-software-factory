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
import contextlib
import sys
import time
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
    pointer from that owner vault and resolve the product repo from the owner App
    Configuration index —
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
    """Resolve ``product`` to its ``owner/name`` repo via the owner App Config index."""
    import os

    from dsf.config.owner_index import OWNER_APPCONFIG_ENV, repo_for_product

    endpoint = (os.environ.get(OWNER_APPCONFIG_ENV) or "").strip()
    return repo_for_product(endpoint, product)


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
            return None, (
                f"cannot resolve repo for product {product!r} from the owner App "
                "Config index (is DSF_OWNER_APPCONFIG_ENDPOINT set?)"
            )
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


@contextlib.asynccontextmanager
async def _charter_store(product: str):
    """Yield a charter store for ``product`` and close it (aio client) on exit."""
    store = build_charter_store(_settings(product))
    try:
        yield store
    finally:
        await store.aclose()


def _cmd_charter_status(args: argparse.Namespace) -> int:
    """Print the stored charter status and its drift vs the file/ref."""
    product = args.product

    async def _run():
        async with _charter_store(product) as store:
            return await store.get_charter(product)

    try:
        stored = asyncio.run(_run())
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1
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

    if args.ref is not None:
        repo_full = _resolve_repo(product)
        if not repo_full:
            print(
                f"[dsf] error: cannot resolve repo for product {product!r} from the "
                "owner App Config index (is DSF_OWNER_APPCONFIG_ENDPOINT set?)",
                file=sys.stderr,
            )
            return 1
    else:
        repo_full = None
        path = Path(args.file or CHARTER_PATH)
        try:
            data = path.read_bytes()
        except OSError as exc:
            print(f"[dsf] error: cannot read {path}: {exc}", file=sys.stderr)
            return 1

    async def _run():
        async with _charter_store(product) as store:
            if repo_full is not None:
                app = build_repo_app_client(_app_settings(product))
                return await sync_charter(
                    store, app, product=product, repo=repo_full, ref=args.ref
                )
            return await sync_charter_text(
                store,
                product=product,
                text=data.decode("utf-8"),
                source_sha=git_blob_sha(data),
                source_ref=f"file:{path}",
            )

    try:
        stored = asyncio.run(_run())
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
        print(
            f"[dsf] error: cannot resolve repo for product {product!r} from the "
            "owner App Config index (is DSF_OWNER_APPCONFIG_ENDPOINT set?)",
            file=sys.stderr,
        )
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


#: GraphQL login of the Copilot coding agent bot (mirrors github_app_client).
_COPILOT_LOGIN = "copilot-swe-agent"

# GitHub rejects assigning the coding agent with a GitHub App installation token, so
# `dsf charter implement` performs just the assignment with the operator's locally
# authenticated `gh` user token. `gh` has no native "assign to Copilot" command, so we
# run the same GraphQL the App client uses via `gh api graphql` (which supplies the
# user token). The mutation takes a single `$actorId` wrapped in an array literal
# because `gh -f` cannot pass list-typed variables.
_GH_SUGGESTED_ACTORS_QUERY = (
    "query($owner:String!,$name:String!){"
    "repository(owner:$owner,name:$name){"
    "suggestedActors(capabilities:[CAN_BE_ASSIGNED],first:100){"
    "nodes{login __typename ... on Bot{id}}}}}"
)
_GH_REPLACE_ACTORS_MUTATION = (
    "mutation($assignableId:ID!,$actorId:ID!){"
    "replaceActorsForAssignable(input:{assignableId:$assignableId,actorIds:[$actorId]}){"
    "assignable{__typename}}}"
)
#: GraphQL: PR nodes linked to an issue (the coding agent opens one per issue).
_GH_ISSUE_TIMELINE_QUERY = (
    "query($owner:String!,$name:String!,$num:Int!){"
    "repository(owner:$owner,name:$name){issue(number:$num){"
    "timelineItems(itemTypes:[CONNECTED_EVENT,CROSS_REFERENCED_EVENT],first:50){"
    "nodes{__typename "
    "... on ConnectedEvent{subject{__typename ... on PullRequest{"
    "number url isDraft state author{login}}}} "
    "... on CrossReferencedEvent{source{__typename ... on PullRequest{"
    "number url isDraft state author{login}}}}}}}}}"
)
_GH_PR_REVIEWERS_QUERY = (
    "query($owner:String!,$name:String!,$num:Int!){"
    "repository(owner:$owner,name:$name){pullRequest(number:$num){"
    "reviewRequests(first:20){nodes{requestedReviewer{__typename "
    "... on Bot{login} ... on User{login}}}}}}}"
)
_DEFAULT_WATCH_POLL_INTERVAL = 20.0
_MIN_WATCH_POLL_INTERVAL = 1.0
_DEFAULT_WATCH_TIMEOUT = 1800.0
_WATCH_POLL_ENV = "DSF_WATCH_POLL_INTERVAL"


def _resolve_watch_poll_interval(explicit: float | None) -> float:
    """Poll cadence: explicit flag (floored 1s) > ``DSF_WATCH_POLL_INTERVAL`` > 20s."""
    import os

    if explicit is not None:
        return max(_MIN_WATCH_POLL_INTERVAL, explicit)
    raw = os.environ.get(_WATCH_POLL_ENV, "").strip()
    if raw:
        try:
            return max(_MIN_WATCH_POLL_INTERVAL, float(raw))
        except ValueError:
            pass
    return _DEFAULT_WATCH_POLL_INTERVAL


def _resolve_watch_timeout(explicit: float | None) -> float | None:
    """Timeout seconds: explicit flag > 1800s; ``0`` (or negative) means unbounded."""
    seconds = _DEFAULT_WATCH_TIMEOUT if explicit is None else explicit
    return None if seconds <= 0 else seconds


def _gh_graphql(
    query: str, *, int_vars: dict[str, int] | None = None, **variables: str
) -> dict:
    """Run a GraphQL ``query`` via ``gh api graphql`` and return its ``data``.

    String variables go through ``-f``; ``int_vars`` go through ``-F`` so GraphQL
    ``Int!`` variables are typed correctly. Uses the operator's ``gh`` user token.
    Raises ``CalledProcessError`` when ``gh`` fails and ``RuntimeError`` on a
    GraphQL error.
    """
    import json
    import subprocess

    argv = ["gh", "api", "graphql", "-f", f"query={query}"]
    for key, value in variables.items():
        argv += ["-f", f"{key}={value}"]
    for key, ivalue in (int_vars or {}).items():
        argv += ["-F", f"{key}={ivalue}"]
    result = subprocess.run(argv, check=True, capture_output=True, text=True)
    payload = json.loads(result.stdout)
    if payload.get("errors"):
        raise RuntimeError(f"GraphQL error: {payload['errors']}")
    return payload["data"]


def _find_agent_pr(repo: str, issue_number: int) -> dict | None:
    """Return the Copilot coding agent's PR linked to ``issue_number``, or None.

    Scans the issue timeline for connected/cross-referenced PRs and picks the one
    authored by ``copilot-swe-agent`` (GraphQL bot logins have no ``app/`` prefix),
    preferring an OPEN one and, among those, the highest number. Returns
    ``{"number", "url", "is_draft", "state"}``.
    """
    owner, _, name = repo.partition("/")
    data = _gh_graphql(
        _GH_ISSUE_TIMELINE_QUERY, int_vars={"num": issue_number}, owner=owner, name=name
    )
    nodes = data["repository"]["issue"]["timelineItems"]["nodes"]
    prs: list[dict] = []
    for node in nodes:
        pr = node.get("subject") or node.get("source") or {}
        if pr.get("__typename") != "PullRequest":
            continue
        login = (pr.get("author") or {}).get("login", "")
        if login.split("/")[-1] != _COPILOT_LOGIN:
            continue
        prs.append(
            {
                "number": pr["number"],
                "url": pr["url"],
                "is_draft": bool(pr["isDraft"]),
                "state": pr["state"],
            }
        )
    if not prs:
        return None
    prs.sort(key=lambda p: (p["state"] == "OPEN", p["number"]), reverse=True)
    return prs[0]


def _issue_number_from_url(url: str) -> int:
    """Parse the trailing issue number from an issue URL/ref (e.g. .../issues/7)."""
    return int(url.rstrip("/").rsplit("/", 1)[-1])


def _pr_has_copilot_reviewer(repo: str, number: int) -> bool:
    """True when Copilot code review is already requested on the PR."""
    owner, _, name = repo.partition("/")
    data = _gh_graphql(
        _GH_PR_REVIEWERS_QUERY, int_vars={"num": number}, owner=owner, name=name
    )
    nodes = data["repository"]["pullRequest"]["reviewRequests"]["nodes"]
    for node in nodes:
        login = (node.get("requestedReviewer") or {}).get("login", "")
        if "copilot" in login.lower():
            return True
    return False


def _request_copilot_review(repo: str, pr_url: str) -> None:
    """Request GitHub Copilot code review on ``pr_url`` via the operator's gh token.

    gh 2.x supports the ``@copilot`` reviewer value natively; this runs under a
    user token, sidestepping the App-installation-token restriction. Raises
    ``CalledProcessError`` if gh fails.
    """
    import subprocess

    subprocess.run(
        ["gh", "pr", "edit", pr_url, "--repo", repo, "--add-reviewer", "@copilot"],
        check=True,
        capture_output=True,
        text=True,
    )


def _agent_work_finished(repo: str, pr_number: int) -> bool:
    """True when timeline says Copilot finished; draft PRs stay draft by design."""
    import json
    import subprocess

    owner, _, name = repo.partition("/")
    result = subprocess.run(
        [
            "gh",
            "api",
            "--paginate",
            f"repos/{owner}/{name}/issues/{pr_number}/timeline",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    latest: str | None = None
    for item in json.loads(result.stdout):
        if not isinstance(item, dict):
            continue
        event = item.get("event")
        if event in ("copilot_work_started", "copilot_work_finished"):
            latest = event
    return latest == "copilot_work_finished"


def _mark_pr_ready(repo: str, pr_number: int) -> None:
    """Mark a draft PR ready for review."""
    import subprocess

    subprocess.run(
        ["gh", "pr", "ready", str(pr_number), "--repo", repo],
        check=True,
        capture_output=True,
        text=True,
    )


def _watch_and_request_review(
    repo: str,
    issue_number: int,
    *,
    timeout: float | None,
    poll_interval: float,
    sleep=time.sleep,
    clock=time.monotonic,
    out=print,
) -> int:
    """Poll the coding agent's PR; request Copilot review once its work is done.

    Returns ``0`` on success (review requested, already requested, or the PR
    reached a terminal non-reviewable state) and ``2`` on timeout (resumable).
    Transient GitHub/network errors are logged and retried until the timeout so
    a single blip does not abort a long build watch. A null or malformed GitHub
    response (for example, the issue was deleted mid-watch) is treated the same way.
    Completion is detected via the ``copilot_work_finished`` timeline event; draft
    PRs are marked ready before Copilot review is requested.
    """
    import json
    import subprocess

    transient = (
        subprocess.CalledProcessError,
        RuntimeError,
        json.JSONDecodeError,
        KeyError,
        TypeError,
    )
    start = clock()
    last_status = ""

    def _emit(status: str) -> None:
        nonlocal last_status
        if status != last_status:
            out(f"[dsf] {status}")
            last_status = status

    def _hand_off(pr) -> int:
        if _pr_has_copilot_reviewer(repo, pr["number"]):
            out(f"[dsf] Copilot review already requested: {pr['url']}")
            return 0
        _request_copilot_review(repo, pr["url"])
        out(f"[dsf] requested Copilot review: {pr['url']}")
        return 0

    while True:
        try:
            pr = _find_agent_pr(repo, issue_number)
            if pr is None:
                _emit("waiting for the coding agent to open its PR...")
            elif pr["state"] in ("MERGED", "CLOSED"):
                out(f"[dsf] {repo}#{pr['number']} is {pr['state'].lower()}; nothing to review.")
                return 0
            elif not pr["is_draft"]:
                return _hand_off(pr)
            elif _agent_work_finished(repo, pr["number"]):
                _mark_pr_ready(repo, pr["number"])
                out(f"[dsf] {repo}#{pr['number']} marked ready for review")
                return _hand_off(pr)
            else:
                _emit(f"{repo}#{pr['number']} building (draft)...")
        except transient as exc:
            _emit(f"transient GitHub error ({exc.__class__.__name__}); retrying...")

        if timeout is not None and clock() - start >= timeout:
            out(
                f"[dsf] still building after {int(timeout)}s; re-run "
                f"`dsf charter watch --product <product>` to resume."
            )
            return 2
        sleep(poll_interval)


def _newest_handoff_issue(repo: str) -> int | None:
    """Newest OPEN issue carrying the handoff label (the bootstrap issue), or None."""
    import json
    import subprocess

    try:
        result = subprocess.run(
            [
                "gh", "issue", "list", "--repo", repo, "--label", HANDOFF_LABEL,
                "--state", "open", "--limit", "20", "--json", "number",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        rows = json.loads(result.stdout)
    except (OSError, subprocess.CalledProcessError, json.JSONDecodeError):
        return None
    numbers = [row["number"] for row in rows if "number" in row]
    return max(numbers) if numbers else None


def _assign_copilot_via_gh(repo: str, issue_node_id: str) -> bool:
    """Assign the Copilot coding agent to an issue via the operator's ``gh`` token.

    Resolve ``copilot-swe-agent``'s node id from the repo's suggested actors, then run
    ``replaceActorsForAssignable``. Returns ``True`` on success; ``False`` (never
    raises) when ``gh`` is missing/unauthenticated, a GraphQL call fails, or Copilot
    is not an assignable actor — so the caller can print a manual-assignment hint.
    """
    import json
    import subprocess

    owner, _, name = repo.partition("/")
    try:
        actors = _gh_graphql(_GH_SUGGESTED_ACTORS_QUERY, owner=owner, name=name)
        nodes = actors["repository"]["suggestedActors"]["nodes"]
        bot_id = next((n["id"] for n in nodes if n.get("login") == _COPILOT_LOGIN), None)
        if bot_id is None:
            return False
        _gh_graphql(_GH_REPLACE_ACTORS_MUTATION, assignableId=issue_node_id, actorId=bot_id)
    except (OSError, subprocess.CalledProcessError, RuntimeError, KeyError, json.JSONDecodeError):
        return False
    return True


async def _implement_async(
    product: str, repo_full: str, args: argparse.Namespace
) -> tuple[int, str | None, bool]:
    """Sync charter, open the constitution PR, file+assign the bootstrap issue.

    Returns ``(rc, issue_url, assigned)``: ``rc`` non-zero on a hard failure,
    ``issue_url`` of the filed bootstrap issue (or ``None``), and whether the
    Copilot coding agent was successfully assigned (so the caller can decide
    whether it is worth watching the build).
    """
    async with _charter_store(product) as store:
        app = build_repo_app_client(_app_settings(product))

        stored = await sync_charter(
            store, app, product=product, repo=repo_full, ref="main"
        )
        print(f"[dsf] synced charter for {product} from main: {stored.status.value}")
        if stored.status != CharterStatus.OK or stored.charter is None:
            print(
                f"[dsf] error: charter for {product} on main is "
                f"{stored.status.value.lower()}; merge the charter PR "
                "(and fix any errors) before implementing.",
                file=sys.stderr,
            )
            if stored.last_error:
                print(f"[dsf]   note: {stored.last_error}", file=sys.stderr)
            return 1, None, False

        charter = stored.charter
        constitution = render_constitution(charter)
        branch = f"charter/constitution-{uuid.uuid4().hex[:8]}"
        pr_url = await app.open_file_pr(
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
        print(f"[dsf] opened constitution PR (auto-merge requested): {pr_url}")

        title, body = render_bootstrap_issue(charter)
        try:
            issue_url = await app.create_issue(repo_full, title, body, [HANDOFF_LABEL])
            print(f"[dsf] filed bootstrap issue + assigned Copilot: {issue_url}")
            return 0, issue_url, True
        except CodingAgentAssignmentError as exc:
            # GitHub forbids assigning the coding agent with a GitHub App
            # installation token, so fall back to the operator's local `gh` user
            # token for just the assignment step (see _assign_copilot_via_gh).
            if _assign_copilot_via_gh(repo_full, exc.issue_node_id):
                print(
                    f"[dsf] filed bootstrap issue + assigned Copilot via gh: "
                    f"{exc.issue_url}"
                )
                return 0, exc.issue_url, True
            print(f"[dsf] filed bootstrap issue: {exc.issue_url}")
            print(
                "[dsf] warning: could not assign the Copilot coding agent; assign it "
                "manually (ensure `gh auth login` and that the Copilot coding agent is "
                "enabled for the repo).",
                file=sys.stderr,
            )
            return 0, exc.issue_url, False


def _cmd_charter_implement(args: argparse.Namespace) -> int:
    """Seed the Spec Kit build from an accepted charter, then watch the build.

    Pulls the charter from ``main`` into Cosmos first (like ``dsf charter sync
    --ref main``), renders the constitution via an auto-merged PR, and files one
    ``creation:ready`` bootstrap issue assigned to the Copilot Coding Agent.
    Unless ``--no-wait`` is given, it then blocks watching the coding agent's PR
    and requests Copilot code review once it is ready.
    """
    product = args.product
    repo_full = _resolve_repo(product)
    if not repo_full:
        print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
        return 1

    try:
        rc, issue_url, assigned = asyncio.run(
            _implement_async(product, repo_full, args)
        )
    except ValueError as exc:
        print(f"[dsf] error: {exc}", file=sys.stderr)
        return 1
    if rc != 0 or not assigned or issue_url is None:
        return rc
    if args.no_wait:
        print(
            f"[dsf] not waiting; run `dsf charter watch --product {product}` to hand "
            "off to Copilot review once the build is ready."
        )
        return 0
    return _watch_and_request_review(
        repo_full,
        _issue_number_from_url(issue_url),
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )


def _cmd_charter_watch(args: argparse.Namespace) -> int:
    """Watch the coding agent's build for a product and hand it off to review."""
    product = args.product
    repo_full = _resolve_repo(product)
    if not repo_full:
        print(f"[dsf] error: product {product!r} is not in registry", file=sys.stderr)
        return 1

    issue_number = args.issue if args.issue is not None else _newest_handoff_issue(repo_full)
    if issue_number is None:
        print(
            f"[dsf] error: no open {HANDOFF_LABEL!r} issue found for {product}; pass "
            "--issue N.",
            file=sys.stderr,
        )
        return 1

    return _watch_and_request_review(
        repo_full,
        issue_number,
        timeout=_resolve_watch_timeout(args.timeout),
        poll_interval=_resolve_watch_poll_interval(args.poll_interval),
    )


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
        help="render the constitution + file the Spec Kit bootstrap issue, then "
        "watch the build and request Copilot review",
    )
    implement_parser.add_argument("--product", required=True, help="product key")
    implement_parser.add_argument(
        "--no-wait",
        action="store_true",
        dest="no_wait",
        help="file + assign only; do not watch the build or request review",
    )
    implement_parser.add_argument(
        "--timeout",
        type=float,
        default=None,
        help="max seconds to watch the build (default 1800; 0 = unbounded)",
    )
    implement_parser.add_argument(
        "--poll-interval",
        type=float,
        default=None,
        dest="poll_interval",
        help=f"seconds between polls (default 20; env {_WATCH_POLL_ENV})",
    )
    implement_parser.set_defaults(func=_cmd_charter_implement)

    watch_parser = charter_sub.add_parser(
        "watch",
        help="watch the coding agent's build and request Copilot review when ready",
    )
    watch_parser.add_argument("--product", required=True, help="product key")
    watch_parser.add_argument(
        "--issue", type=int, default=None,
        help="bootstrap issue number (default: newest open handoff issue)",
    )
    watch_parser.add_argument(
        "--timeout", type=float, default=None,
        help="max seconds to watch (default 1800; 0 = unbounded)",
    )
    watch_parser.add_argument(
        "--poll-interval", type=float, default=None, dest="poll_interval",
        help=f"seconds between polls (default 20; env {_WATCH_POLL_ENV})",
    )
    watch_parser.set_defaults(func=_cmd_charter_watch)

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
