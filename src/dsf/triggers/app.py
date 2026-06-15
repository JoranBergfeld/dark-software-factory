"""Signal-ingestion HTTP endpoint — ``POST /ingest``.

A webhook / Azure Event Grid-shaped signal arrives as a JSON body. The handler:

1. honors the SIGNAL trigger pause flag -> ``{"status": "paused"}``;
2. debounces repeat signals within the window -> ``{"status": "suppressed"}``;
3. otherwise maps the payload to a :class:`Run` and drives it through the
   conveyor in dry-run -> ``{"run_id", "status"}`` from the final run.

The app builds a module-level local :class:`~dsf.container.Services` at import
time. Tests can override it via FastAPI's dependency system
(``app.dependency_overrides[get_services] = ...``) so each test gets a fresh,
isolated services bundle — keeping the app testable without global state.
"""

from __future__ import annotations

from typing import TYPE_CHECKING, Any

from fastapi import Body, Depends, FastAPI

from dsf.config.flags import triggers_paused
from dsf.container import build_services
from dsf.contracts.enums import TriggerKind
from dsf.orchestrator.conveyor import run_line
from dsf.triggers.debounce import record_signal, should_suppress
from dsf.triggers.ingestion import signal_to_run

if TYPE_CHECKING:
    from dsf.container import Services

#: Module-level local services bundle (fakes), shared across requests by default.
_LOCAL_SERVICES: Services = build_services("local")


def get_services() -> Services:
    """Dependency provider for the request-scoped services bundle.

    Returns the module-level local bundle by default; override in tests via
    ``app.dependency_overrides[get_services]``.
    """
    return _LOCAL_SERVICES


app = FastAPI(title="dsf-ingestion", version="1.0.0")

# Module-level singletons for the dependency defaults so ruff's B008 (no function
# calls in argument defaults) is satisfied while keeping FastAPI injection.
_BODY = Body(default_factory=dict)
_SERVICES = Depends(get_services)


@app.get("/health")
async def health() -> dict[str, str]:
    """Liveness probe."""
    return {"status": "ok"}


@app.post("/ingest")
async def ingest(
    payload: dict[str, Any] = _BODY,
    services: Services = _SERVICES,
) -> dict[str, Any]:
    """Ingest a webhook signal and (unless paused/suppressed) run a dry-run line."""
    if triggers_paused(services.config, TriggerKind.SIGNAL):
        return {"status": "paused"}

    if await should_suppress(payload, services):
        return {"status": "suppressed"}

    # First time we have seen this signal — record it so a repeat is debounced.
    await record_signal(payload, services)

    run = signal_to_run(payload)
    run.dry_run = True  # ingestion always runs the line in dry-run (no filing).
    result = await run_line(run, services)
    return {"run_id": result.id, "status": result.status.value}


__all__ = ["app", "get_services", "ingest"]
