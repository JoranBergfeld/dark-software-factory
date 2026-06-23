"""Real Cosmos-backed :class:`~dsf.ports.CharterStore`.

Mirrors :class:`dsf.memory.azure_store.CosmosMemoryStore`: it talks to the same
narrow :class:`~dsf.memory.azure_store.CosmosGateway` seam (so it runs offline
against ``InMemoryCosmosGateway`` in tests) and reuses the lazy SDK gateway, so
importing this module pulls in no Azure SDK.
"""

from __future__ import annotations

from dsf.contracts.charter import StoredCharter
from dsf.memory.azure_store import CosmosGateway, _SdkCosmosGateway

#: Cosmos container holding one charter document per product.
_CHARTERS = "charters"


class CosmosCharterStore:
    """Charter store backed by a single Cosmos container (``id == product``)."""

    def __init__(self, gateway: CosmosGateway) -> None:
        self._gw = gateway

    @classmethod
    def from_endpoint(cls, endpoint: str, *, database: str) -> CosmosCharterStore:
        """Build a store talking to the real Cosmos account at ``endpoint``."""
        return cls(_SdkCosmosGateway(endpoint, database))

    async def get_charter(self, product: str) -> StoredCharter | None:
        rows = await self._gw.query(_CHARTERS, "product", product)
        if not rows:
            return None
        return StoredCharter.model_validate(rows[0]["stored"])

    async def put_charter(self, stored: StoredCharter) -> None:
        item = {
            "id": stored.product,
            "product": stored.product,
            "stored": stored.model_dump(mode="json"),
        }
        await self._gw.upsert(_CHARTERS, item)


__all__ = ["CosmosCharterStore"]
