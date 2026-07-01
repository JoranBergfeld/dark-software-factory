"""In-memory :class:`~dsf.ports.CharterStore` double for tests."""

from __future__ import annotations

from dsf.contracts.charter import StoredCharter


class InMemoryCharterStore:
    """Dict-backed charter store: one :class:`StoredCharter` per product."""

    def __init__(self, seed: dict[str, StoredCharter] | None = None) -> None:
        self._by_product: dict[str, StoredCharter] = dict(seed or {})

    async def get_charter(self, product: str) -> StoredCharter | None:
        return self._by_product.get(product)

    async def put_charter(self, stored: StoredCharter) -> None:
        self._by_product[stored.product] = stored

    async def aclose(self) -> None:
        """No-op: the dict-backed store holds no resources."""
        return None


__all__ = ["InMemoryCharterStore"]
