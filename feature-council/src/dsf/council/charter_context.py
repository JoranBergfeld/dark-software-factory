"""Charter loading for the council, memoized per run on the working tier.

Re-exports the core untrusted-envelope builder (:func:`charter_context`) and adds
:func:`load_charter`, a per-run memoized loader. S1, the ``value``/
``strategic_fit`` lenses, and the ``scope`` annotation all read the active charter
through this loader so a single run never re-hits Cosmos per lens or round. The
memo is keyed by run id + product, so it refreshes every run (no cross-tick
staleness) and stays correct for multi-product runs.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

from dsf.charter.context import charter_context, load_active_charter
from dsf.contracts.charter import Charter

if TYPE_CHECKING:
    from dsf.container import Services
    from dsf.contracts.models import Run


def _memo_key(run_id: str, product: str | None) -> str:
    """Working-tier key for the per-run charter memo."""
    return f"charter:{run_id}:{product or '_unscoped'}"


def _decode(payload: object) -> Charter | None:
    """Decode a memo payload (``{"charter": <dump|None>}``) back to a Charter."""
    if not isinstance(payload, dict):
        return None
    body = payload.get("charter")
    return Charter.model_validate(body) if body else None


def _encode(charter: Charter | None) -> dict[str, object]:
    """Encode a Charter (or None) into a memo payload."""
    return {"charter": charter.model_dump(mode="json") if charter else None}


async def set_charter_memo(
    services: Services, run: Run, product: str | None, charter: Charter | None
) -> None:
    """Seed the per-run charter memo (S1 calls this from its single Cosmos read)."""
    await services.memory.put_working(_memo_key(run.id, product), _encode(charter))


async def load_charter(services: Services, run: Run, product: str | None) -> Charter | None:
    """Return the active charter for ``product`` in ``run``, memoized per run.

    Reads the run memo first; on a miss it loads from the charter store via
    :func:`dsf.charter.context.load_active_charter`, writes the memo (including the
    "uncharted" ``None`` result), and returns it.
    """
    if product is None:
        return None
    cached = await services.memory.get_working(_memo_key(run.id, product))
    if cached is not None:
        return _decode(cached)
    charter = await load_active_charter(services, product)
    await services.memory.put_working(_memo_key(run.id, product), _encode(charter))
    return charter


__all__ = ["charter_context", "load_charter", "set_charter_memo"]
