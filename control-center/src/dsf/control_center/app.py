"""Control Center web UI (Phase 7) -- the WRITE surface for runtime toggles.

The Grafana dashboard stays read-only; this FastAPI app is where an operator
flips feature flags, pauses triggers, and accepts calibration proposals -- all
of which land in the :class:`~dsf.ports.ConfigStore` and take effect on the next
run (no redeploy).

Authentication
--------------
All write routes (``POST /toggle`` and ``POST /set-value``) require a bearer
token via an ``Authorization: Bearer <token>`` header.  Set the
``CC_BEARER_TOKEN`` environment variable to enable enforcement.  In local mode
(``DSF_MODE=local`` or unset) an empty ``CC_BEARER_TOKEN`` leaves the write
surface open -- intentional for local development and tests.  In any other mode
an empty token raises at startup (fail CLOSED).

CSRF protection
---------------
The ``Authorization: Bearer`` header requirement is the primary CSRF defence:
browsers cannot include custom request headers in cross-site form submissions,
so any cross-origin ``POST /toggle`` or ``POST /set-value`` is rejected with
``401`` before reaching application logic.  As a second layer, every ``GET /``
response also sets a ``cc_csrf`` cookie (``SameSite=Strict``) whose value must
appear as a hidden ``csrf_token`` field in every write form.  Missing or
mismatched tokens return ``403``.

Audit log
---------
Every successful flag change emits a structured ``INFO`` line to the
``dsf.control_center`` logger so operators can track who changed what.

Templates and static assets live alongside this module so the package is
self-contained:

* ``templates/index.html`` rendered via :class:`~fastapi.templating.Jinja2Templates`
  pointed at ``Path(__file__).parent / "templates"``.
* ``static/app.css`` served from ``Path(__file__).parent / "static"`` -- no CDN,
  works fully offline.
"""

from __future__ import annotations

import asyncio
import hmac
import logging
import os
import secrets
from pathlib import Path
from typing import TYPE_CHECKING, Any
from urllib.parse import parse_qs

from fastapi import Depends, FastAPI, HTTPException, status
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from starlette.requests import Request

from dsf.a2a.auth import build_bearer_dependency
from dsf.config import flags
from dsf.container import build_services
from dsf.contracts.enums import SourceKind, TriggerKind

if TYPE_CHECKING:
    from dsf.container import Services

_logger = logging.getLogger("dsf.control_center")

#: Env var for the Control Center bearer token.
CC_BEARER_TOKEN_ENV = "CC_BEARER_TOKEN"

#: Cookie name for the CSRF double-submit token.
_CSRF_COOKIE = "cc_csrf"

#: The seven council critics (design SS5.2 / SS7.1).
CRITICS: tuple[str, ...] = (
    "grounding",
    "value",
    "duplication",
    "feasibility",
    "strategic_fit",
    "cost",
    "security",
)

#: The five source agents.
AGENTS: tuple[str, ...] = tuple(k.value for k in SourceKind)

#: The two trigger kinds whose pause state is toggleable.
TRIGGERS: tuple[str, ...] = tuple(k.value for k in TriggerKind)

#: Package-local template + static directories.
_HERE = Path(__file__).resolve().parent
TEMPLATES_DIR = _HERE / "templates"
STATIC_DIR = _HERE / "static"


def _truthy(value: str) -> bool:
    """Parse an HTML-form string into a bool (checkbox/select friendly)."""
    return str(value).strip().lower() in {"1", "true", "on", "yes"}


async def _form(request: Request) -> dict[str, str]:
    """Parse an ``application/x-www-form-urlencoded`` body without extra deps.

    Starlette's ``request.form()`` hard-requires ``python-multipart``; the body
    here is always url-encoded, so parse it directly to keep the package
    dependency-free.
    """
    body = (await request.body()).decode("utf-8")
    parsed = parse_qs(body, keep_blank_values=True)
    return {key: values[-1] for key, values in parsed.items()}


def _check_csrf(request: Request, form_data: dict[str, str], *, enforce: bool) -> None:
    """Validate the CSRF double-submit cookie against the form field.

    When *enforce* is ``False`` (local/open mode) this is a no-op.  When
    ``True``, both the ``cc_csrf`` cookie and the ``csrf_token`` form field
    must be present and equal (constant-time comparison).
    """
    if not enforce:
        return
    cookie_token = request.cookies.get(_CSRF_COOKIE, "")
    form_token = form_data.get("csrf_token", "")
    if not cookie_token or not form_token or not hmac.compare_digest(cookie_token, form_token):
        raise HTTPException(
            status_code=status.HTTP_403_FORBIDDEN,
            detail="Invalid or missing CSRF token",
        )


def _load_calibration(
    services: Services, current: dict[str, float]
) -> dict[str, float] | None:
    """Return *changed* proposed critic weights, or ``None`` if unavailable.

    The learning loop (Phase 6) is imported lazily and guarded: if the package
    or its calibration entrypoint is absent (or errors), the dashboard simply
    shows current weights with no proposals. Only critics whose proposed weight
    differs from the configured one are surfaced as proposals to accept.
    """
    try:
        from dsf.learning.calibration import proposed_weight_update
    except Exception:
        return None

    try:
        proposed = asyncio.run(proposed_weight_update(services, list(CRITICS)))
    except Exception:
        return None

    if not isinstance(proposed, dict) or not proposed:
        return None

    changed = {
        str(critic): float(weight)
        for critic, weight in proposed.items()
        if abs(float(weight) - float(current.get(critic, weight))) > 1e-9
    }
    return changed or None


def _set_value(services: Services, key: str, value: float) -> None:
    """Write a numeric config value, tolerating ConfigStore variants.

    Prefers an explicit ``set_value`` method; falls back to writing the dotted
    key path into the store's backing data dict (the in-memory implementation).
    """
    cfg = services.config
    setter = getattr(cfg, "set_value", None)
    if callable(setter):
        setter(key, value)
        return

    data = getattr(cfg, "_data", None)
    if isinstance(data, dict):
        parts = key.split(".")
        node: Any = data
        for part in parts[:-1]:
            nxt = node.get(part)
            if not isinstance(nxt, dict):
                nxt = {}
                node[part] = nxt
            node = nxt
        node[parts[-1]] = value


def _state(services: Services) -> dict[str, Any]:
    """Assemble the full view-model the template (and /api/state) render from."""
    cfg = services.config

    # Product list drives the per-product critic capability note.
    products: list[str] = []
    try:
        from dsf.config.registry import load_registry

        products = sorted(load_registry().keys())
    except Exception:
        products = []

    critics = [
        {"name": name, "enabled": flags.critic_enabled(cfg, name)}
        for name in CRITICS
    ]
    agents = [
        {"name": name, "enabled": flags.agent_enabled(cfg, name)}
        for name in AGENTS
    ]
    triggers = [
        {"name": name, "paused": flags.triggers_paused(cfg, name)}
        for name in TRIGGERS
    ]
    weights = flags.weights(cfg, list(CRITICS))
    thresholds = {
        "default": flags.threshold(cfg),
        "products": {p: flags.threshold(cfg, p) for p in products},
    }
    calibration = _load_calibration(services, weights)

    return {
        "critics": critics,
        "agents": agents,
        "triggers": triggers,
        "weights": weights,
        "thresholds": thresholds,
        "products": products,
        "calibration": calibration,
        "snapshot": cfg.snapshot(),
    }


def create_app(services: Services | None = None, *, token: str | None = None) -> FastAPI:
    """Build the Control Center :class:`FastAPI` app.

    Parameters
    ----------
    services:
        Defaults to a lazily built real :func:`dsf.container.build_services`
        bundle. Pass an explicit instance (and hold a reference) to assert
        against the same :class:`~dsf.ports.ConfigStore` after a toggle.
    token:
        Bearer token for write-route authentication.  ``None`` reads
        ``CC_BEARER_TOKEN`` from the environment.  ``""`` disables enforcement
        (local/test mode only -- raises in non-local mode).  A non-empty string
        always enforces.
    """
    svc = services if services is not None else build_services()

    _raw_token = token if token is not None else os.environ.get(CC_BEARER_TOKEN_ENV, "")
    expected_token = (_raw_token or "").strip()

    # build_bearer_dependency raises immediately if token is empty outside local
    # mode -- fail CLOSED at startup.
    auth_dep = build_bearer_dependency(expected_token)
    csrf_required = bool(expected_token)

    app = FastAPI(title="dsf-control-center")
    templates = Jinja2Templates(directory=str(TEMPLATES_DIR))
    if STATIC_DIR.is_dir():
        app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

    @app.get("/", response_class=HTMLResponse)
    def index(request: Request) -> HTMLResponse:
        # Reuse existing CSRF cookie or issue a fresh one.
        csrf_token = request.cookies.get(_CSRF_COOKIE) if csrf_required else ""
        if csrf_required and not csrf_token:
            csrf_token = secrets.token_hex(32)
        response = templates.TemplateResponse(
            request,
            "index.html",
            {"state": _state(svc), "csrf_token": csrf_token},
        )
        if csrf_required and csrf_token:
            response.set_cookie(
                _CSRF_COOKIE,
                csrf_token,
                httponly=True,
                samesite="strict",
                secure=False,  # operator tool; set secure=True behind TLS
            )
        return response

    @app.get("/api/state")
    def api_state() -> JSONResponse:
        return JSONResponse(_state(svc))

    @app.post("/toggle", dependencies=[Depends(auth_dep)])
    async def toggle(request: Request) -> RedirectResponse:
        form = await _form(request)
        _check_csrf(request, form, enforce=csrf_required)
        flag = form["flag"]
        value = _truthy(form["value"])
        prod = form.get("product") or None
        svc.config.set_flag(flag, value, product=prod)
        _logger.info(
            "toggle flag=%r value=%r product=%r remote=%s",
            flag,
            value,
            prod,
            request.client.host if request.client else "unknown",
        )
        return RedirectResponse(url="/", status_code=303)

    @app.post("/set-value", dependencies=[Depends(auth_dep)])
    async def set_value(request: Request) -> RedirectResponse:
        form = await _form(request)
        _check_csrf(request, form, enforce=csrf_required)
        key = form["key"]
        value = float(form["value"])
        _set_value(svc, key, value)
        _logger.info(
            "set-value key=%r value=%r remote=%s",
            key,
            value,
            request.client.host if request.client else "unknown",
        )
        return RedirectResponse(url="/", status_code=303)

    return app


#: Lazily built module-level app for ``uvicorn dsf.control_center.app:app``.
#: Built on first attribute access so importing this module needs no Azure env.
_app: FastAPI | None = None


def __getattr__(name: str) -> object:
    """Build the real module-level ``app`` on first access (PEP 562)."""
    if name == "app":
        global _app
        if _app is None:
            _app = create_app()
        return _app
    raise AttributeError(f"module {__name__!r} has no attribute {name!r}")


def main(argv: list[str] | None = None) -> int:
    """Serve the Control Center web UI via uvicorn (``dsf-control-center`` script)."""
    import argparse

    import uvicorn

    parser = argparse.ArgumentParser(
        prog="dsf-control-center",
        description="Dark Software Factory — Control Center web UI",
    )
    parser.add_argument(
        "--host", default="127.0.0.1", help="bind host (localhost-only by default)"
    )
    parser.add_argument("--port", type=int, default=8081, help="bind port")
    args = parser.parse_args(argv)
    uvicorn.run("dsf.control_center.app:app", host=args.host, port=args.port)
    return 0


__all__ = [
    "AGENTS",
    "CC_BEARER_TOKEN_ENV",
    "CRITICS",
    "TRIGGERS",
    "create_app",
    "main",
]


if __name__ == "__main__":
    raise SystemExit(main())