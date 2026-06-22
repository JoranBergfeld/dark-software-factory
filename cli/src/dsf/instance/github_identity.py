"""Resolve the GitHub owner for a new product instance.

``dsf new`` no longer forces ``--owner``: when it is omitted we fall back to the
account the local ``gh`` CLI is authenticated as (``gh api user --jq .login``).
External CLIs are invoked through an injectable ``run`` callable (defaults to
:func:`subprocess.run`) so tests stay offline, mirroring
:class:`dsf.instance.provisioner.InstanceProvisioner`.
"""

from __future__ import annotations

import subprocess
import sys
from collections.abc import Callable
from typing import Any

Runner = Callable[..., Any]


class OwnerResolutionError(RuntimeError):
    """Raised when no ``--owner`` was given and one cannot be resolved from gh."""


def _gh_login(run: Runner) -> str:
    """Return the login of the gh-authenticated account, or raise."""
    try:
        proc = run(
            ["gh", "api", "user", "--jq", ".login"],
            capture_output=True,
            text=True,
            check=True,
        )
    except FileNotFoundError as exc:
        raise OwnerResolutionError(
            "gh CLI not found: install GitHub CLI and run 'gh auth login', "
            "or pass --owner explicitly."
        ) from exc
    except subprocess.CalledProcessError as exc:
        raise OwnerResolutionError(
            "gh is not authenticated: run 'gh auth login', or pass --owner explicitly."
        ) from exc
    login = (getattr(proc, "stdout", "") or "").strip()
    if not login:
        raise OwnerResolutionError(
            "could not determine a GitHub account from gh; pass --owner explicitly."
        )
    return login


def resolve_owner(
    supplied: str,
    *,
    run: Runner = subprocess.run,
    interactive: bool | None = None,
    prompt: Callable[[str], str] = input,
) -> str:
    """Resolve the GitHub owner/org under which to create the product repo.

    If ``supplied`` is non-empty it is returned unchanged. Otherwise the login of
    the gh-authenticated account is used: a warning is always emitted, and when
    running interactively (a TTY, unless ``interactive`` overrides) the user must
    confirm before the repo is created under that account. Raises
    :class:`OwnerResolutionError` if gh cannot supply a login or the user declines.
    """
    if supplied:
        return supplied

    login = _gh_login(run)
    print(
        f"[dsf] warning: --owner not set; creating the product repo under "
        f"gh-authenticated account '{login}'.",
        file=sys.stderr,
    )

    if interactive is None:
        interactive = sys.stdin.isatty()
    if interactive:
        answer = prompt(f"[dsf] Create the product repo under '{login}'? [y/N] ")
        if answer.strip().lower() not in {"y", "yes"}:
            raise OwnerResolutionError(
                "aborted: pass --owner explicitly to create the repo under a "
                "different account."
            )
    return login
