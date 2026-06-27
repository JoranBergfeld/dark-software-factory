"""S2 — Investigation (agentic workcell).

Dispatch the run's enabled source agents in parallel, in-process, over the A2A
ASGI transport, and collect their structured evidence onto the run. A degraded
or disabled source contributes no evidence and is explicitly audited — coverage
is never fabricated.
"""

from __future__ import annotations

import asyncio
from typing import TYPE_CHECKING

import httpx

from dsf.a2a import client as a2a_client
from dsf.config.flags import agent_enabled, product_record
from dsf.contracts.enums import RunStatus
from dsf.observability.tracing import span_attrs_for_run
from dsf.orchestrator.agent_registry import build_agents

if TYPE_CHECKING:
    from dsf.agents.base import SourceAgent
    from dsf.container import Services
    from dsf.contracts.enums import SourceKind
    from dsf.contracts.models import EvidenceItem, Run

STATION = "S2:investigation"


def _run_scope(run: Run, services: Services) -> dict:
    """Serialize the subset of the run that source agents need.

    Threads the factory's own product record (from its per-product App Config)
    under ``product_registry`` so live source backends scope their queries to the
    product. The run is always scoped to its product; a missing record raises
    (the station then audits an ERROR) rather than sweeping unscoped.
    """
    scope = {
        "run_id": run.id,
        "product_hints": list(run.scope_product_hints),
        "source_kinds": [k.value for k in run.source_kinds],
        "signal_payload": dict(run.signal_payload),
    }
    record = product_record(services.config, services.product)
    scope["product_registry"] = record.model_dump()
    return scope


async def _gather_one(
    kind: SourceKind,
    agent: SourceAgent,
    scope: dict,
) -> tuple[SourceKind, list[EvidenceItem], bool, str | None]:
    """Call one agent in-process; return (kind, evidence, degraded, error)."""
    transport = httpx.ASGITransport(app=agent.make_app(token=""))
    resp = await a2a_client.gather(endpoint=None, scope=scope, transport=transport)
    return kind, list(resp.evidence), resp.degraded, resp.error


async def run(run: Run, services: Services) -> Run:
    """Gather evidence from enabled source agents in parallel."""
    with services.tracer.span("s2_investigation", **span_attrs_for_run(run)):
        run.status = RunStatus.INVESTIGATING

        requested = list(run.source_kinds)
        enabled = [k for k in requested if agent_enabled(services.config, k)]
        skipped = [k for k in requested if k not in enabled]

        for kind in skipped:
            run.audit.append(_audit(f"source {kind.value} disabled — skipped"))

        agents = build_agents(enabled, services.config)
        scope = _run_scope(run, services)

        results = await asyncio.gather(
            *(_gather_one(kind, agents[kind], scope) for kind in enabled if kind in agents)
        )

        collected = 0
        for kind, evidence, degraded, error in results:
            if degraded:
                run.audit.append(
                    _audit(f"source {kind.value} degraded: {error or 'unknown error'}")
                )
            if evidence:
                run.evidence.extend(evidence)
                collected += len(evidence)

        run.audit.append(
            _audit(
                f"investigation complete: {collected} evidence item(s) "
                f"from {len(enabled)} enabled source(s)"
            )
        )
        return run


def _audit(message: str):
    """Construct an audit record for this station."""
    from dsf.contracts.models import AuditRecord

    return AuditRecord(station=STATION, message=message)


__all__ = ["STATION", "run"]
