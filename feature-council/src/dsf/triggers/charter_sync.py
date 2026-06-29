"""Runtime-pull charter sync — refresh the product charter on each sweep.

Before the conveyor runs, pull the product's ``.dsf/charter.md`` via the GitHub
App and reconcile it into Cosmos through the core idempotent
:func:`dsf.charter.sync.sync_charter`. Every outcome is recorded as an audit line
on the sweep run and every error is swallowed, so a charter problem never tears
down the sweep (mirrors the conveyor's per-station error discipline). DSF is
pull-only, so this is the only charter writer at runtime.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.charter.sync import sync_charter
from dsf.config.flags import product_record
from dsf.contracts.models import AuditRecord

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Run

STATION = "trigger:charter-sync"


def _audit(run: Run, message: str) -> None:
    """Append a charter-sync audit line to the sweep run."""
    run.audit.append(AuditRecord(station=STATION, message=message))


async def sync_charter_on_sweep(services: Services, run: Run) -> None:
    """Pull + reconcile the product charter; audit the outcome, never raise."""
    product = services.product
    if not product:
        return
    if services.repo is None:
        _audit(run, "charter sync skipped: no GitHub App configured")
        return
    try:
        record = product_record(services.config, product)
        stored = await sync_charter(
            services.charter, services.repo, product=product, repo=record.github_repo
        )
    except Exception as exc:  # noqa: BLE001 - charter problems never crash the sweep
        _audit(run, f"charter sync error (ignored): {exc}")
        return
    _audit(run, f"charter sync: status={stored.status.value}")


__all__ = ["STATION", "sync_charter_on_sweep"]
