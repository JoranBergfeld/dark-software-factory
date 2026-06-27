"""Living-charter reflection on the sweep — propose amendments as governance PRs.

After the pull-only charter *sync* (which refreshes the last-known-good charter
into Cosmos), the sweep optionally *reflects*: if the product opted in and the
accumulated lessons warrant it, the factory opens a human-gated governance PR
amending ``.dsf/charter.md`` (core
:func:`dsf.charter.amendment.propose_charter_amendment`). Every guardrail and the
final decision are recorded as an audit line, and every error is swallowed, so a
charter-reflection problem never tears down the sweep — mirroring the conveyor's
per-station error discipline. The factory never writes the charter store; only a
merge (via the sync above) does.
"""

from __future__ import annotations

from datetime import UTC, datetime
from typing import TYPE_CHECKING

from dsf.charter.amendment import AmendmentReason, propose_charter_amendment
from dsf.config.flags import product_record
from dsf.contracts.models import AuditRecord

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Run

STATION = "trigger:charter-amend"


def _audit(run: Run, message: str) -> None:
    """Append a charter-amendment audit line to the sweep run."""
    run.audit.append(AuditRecord(station=STATION, message=message))


async def propose_amendment_on_sweep(services: Services, run: Run) -> None:
    """Reflect on lessons and maybe open an amendment PR; audit, never raise."""
    product = services.product
    if not product:
        return
    if services.repo is None:
        _audit(run, "charter amendment skipped: no GitHub App configured")
        return
    try:
        record = product_record(services.config, product)
        outcome = await propose_charter_amendment(
            charter_store=services.charter,
            memory=services.memory,
            model=services.model,
            repo_client=services.repo,
            product=product,
            repo=record.github_repo,
            config=services.config,
            now=datetime.now(UTC),
        )
    except Exception as exc:  # noqa: BLE001 - reflection problems never crash the sweep
        _audit(run, f"charter amendment error (ignored): {exc}")
        return
    if outcome.reason == AmendmentReason.PROPOSED:
        _audit(run, f"charter amendment: proposed PR {outcome.pr_url}")
    else:
        suffix = f" ({outcome.detail})" if outcome.detail else ""
        _audit(run, f"charter amendment: {outcome.reason}{suffix}")


__all__ = ["STATION", "propose_amendment_on_sweep"]
