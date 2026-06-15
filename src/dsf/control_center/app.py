"""Control Center web UI (Phase 7) — the WRITE surface for runtime toggles.

The Grafana dashboard stays read-only; this FastAPI app is where an operator
flips feature flags, pauses triggers, throws the global dry-run kill switch, and
accepts calibration proposals — all of which land in the
:class:`~dsf.ports.ConfigStore` and take effect on the next run (no redeploy).

Templates and static assets live alongside this module so the package is
self-contained:

* ``templates/index.html`` rendered via :class:`~fastapi.templating.Jinja2Templates`
  pointed at ``Path(__file__).parent / "templates"``.
* ``static/app.css`` served from ``Path(__file__).parent / "static"`` — no CDN,
  works fully offline.
"""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import TYPE_CHECKING, Any
from urllib.parse import parse_qs

from fastapi import FastAPI
from fastapi.responses import HTMLResponse, JSONResponse, RedirectResponse
from fastapi.staticfiles import StaticFiles
from fastapi.templating import Jinja2Templates
from starlette.requests import Request

from dsf.config import flags
from dsf.container import build_services
from dsf.contracts.enums import SourceKind, TriggerKind

if TYPE_CHECKING:
    from dsf.container import Services

#: The seven council critics (design §5.2 / §7.1).
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
    key path into the store's backing data dict (the in-memory fake).
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
        "dry_run": flags.dry_run_global(cfg),
        "critics": critics,
        "agents": agents,
        "triggers": triggers,
        "weights": weights,
        "thresholds": thresholds,
        "products": products,
        "calibration": calibration,
        "snapshot": cfg.snapshot(),
    }


def create_app(services: Services | None = None) -> FastAPI:
    """Build the Control Center :class:`FastAPI` app.

    ``services`` defaults to :func:`dsf.container.build_services` in ``local``
    mode. Pass an explicit instance (and hold a reference) to assert against the
    same :class:`~dsf.ports.ConfigStore` after a toggle.
    """
    svc = services if services is not None else build_services("local")
    app = FastAPI(title="dsf-control-center")
    templates = Jinja2Templates(directory=str(TEMPLATES_DIR))
    if STATIC_DIR.is_dir():
        app.mount("/static", StaticFiles(directory=str(STATIC_DIR)), name="static")

    @app.get("/", response_class=HTMLResponse)
    def index(request: Request) -> HTMLResponse:
        return templates.TemplateResponse(
            request,
            "index.html",
            {"state": _state(svc)},
        )

    @app.get("/api/state")
    def api_state() -> JSONResponse:
        return JSONResponse(_state(svc))

    @app.post("/toggle")
    async def toggle(request: Request) -> RedirectResponse:
        form = await _form(request)
        prod = form.get("product") or None
        svc.config.set_flag(form["flag"], _truthy(form["value"]), product=prod)
        return RedirectResponse(url="/", status_code=303)

    @app.post("/set-value")
    async def set_value(request: Request) -> RedirectResponse:
        form = await _form(request)
        _set_value(svc, form["key"], float(form["value"]))
        return RedirectResponse(url="/", status_code=303)

    return app


#: Module-level app for ``uvicorn dsf.control_center.app:app``.
app = create_app()


__all__ = ["AGENTS", "CRITICS", "TRIGGERS", "app", "create_app"]
