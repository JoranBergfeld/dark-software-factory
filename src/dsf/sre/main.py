"""SRE sweep entrypoint — run one observe -> fix-forward -> reflect cycle."""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.sre.wiring import build_sre_agent

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.ports import SourceBackend
    from dsf.sre.models import SreSweepResult


async def run_sweep(
    services: Services,
    scope: dict | None = None,
    *,
    dry_run: bool = False,
    backends: list[SourceBackend] | None = None,
) -> SreSweepResult:
    """Run a single SRE sweep over ``services``.

    When ``scope`` is omitted it defaults to the bundle's product scope
    (``{"products": [services.product]}``) so an azure-mode runtime only
    fix-forwards incidents for the product it is scoped to.
    """
    if scope is None:
        scope = {"products": [services.product]} if services.product else {}
    agent = build_sre_agent(services, backends=backends)
    return await agent.sweep(scope, dry_run=dry_run)


__all__ = ["run_sweep"]
