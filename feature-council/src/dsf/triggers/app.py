"""Signal-ingestion HTTP endpoint - ``POST /ingest`` and ``POST /file``.

``POST /ingest`` is the inbound webhook / Azure Event Grid handler. It is
enqueue-only (the governed pull of ADR 0011): an inbound source cannot drive the
council's cadence.

1. honours the SIGNAL trigger pause flag -> ``{"status": "paused"}``;
2. debounces repeat signals within the TTL window -> ``{"status": "suppressed"}``;
3. otherwise records the signal and enqueues it on the
   :class:`~dsf.ports.SignalBuffer` -> ``{"status": "queued"}``. The scheduled
   worker drains the buffer on the council's own schedule (see
   :func:`dsf.triggers.scheduler.drain_signals`).

``POST /file`` is the deliberate filing path for human or scheduled invocation.
It does **not** debounce and does **not** hard-code ``dry_run=True``, so when the
:class:`~dsf.container.Services` bundle is wired with a real
:class:`~dsf.github_client.RealGitHubClient` (e.g. ``--mode gh``) and the
global ``dry_run`` config flag is off, the run will actually file a GitHub issue.

The app builds a module-level local :class:`~dsf.container.Services` at import
time. Tests can override it via FastAPI's dependency system
(``app.dependency_overrides[get_services] = ...``) so each test gets a fresh,
isolated services bundle - keeping the app testable without global state.
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

#: Module-level local services bundle, shared across requests by default.
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
    """Enqueue a webhook signal for the scheduled council drain (governed pull).

    The synchronous push-into-the-pipeline path is gone (ADR 0011): an inbound
    source can no longer set the council's cadence. The signal is debounced and,
    if new, recorded and enqueued; the scheduled worker drains the buffer on the
    council's own schedule.
    """
    if triggers_paused(services.config, TriggerKind.SIGNAL):
        return {"status": "paused"}

    if await should_suppress(payload, services):
        return {"status": "suppressed"}

    # First time we have seen this signal: record it so a repeat is debounced,
    # then enqueue it for the scheduled drain.
    await record_signal(payload, services)
    await services.signals.enqueue(payload)
    return {"status": "queued"}


@app.post("/file")
async def file_signal(
    payload: dict[str, Any] = _BODY,
    services: Services = _SERVICES,
) -> dict[str, Any]:
    """Deliberately run the pipeline with filing enabled.

    Unlike ``/ingest`` this endpoint does **not** hard-code ``dry_run=True`` and
    does **not** apply debounce suppression — it is an explicit, intentional
    invocation.  Filing a real GitHub issue additionally requires:

    * The services bundle to carry a real :class:`~dsf.github_client.RealGitHubClient`
      (use ``--mode gh`` or override ``get_services``).
    * The global ``dry_run`` config flag to be off (it defaults to ``True`` for
      safety in local mode).
    """
    if triggers_paused(services.config, TriggerKind.SIGNAL):
        return {"status": "paused"}

    run = signal_to_run(payload)
    run.dry_run = False  # caller has explicitly chosen to file.
    result = await run_line(run, services)
    return {"run_id": result.id, "status": result.status.value}


__all__ = ["app", "file_signal", "get_services", "ingest"]
