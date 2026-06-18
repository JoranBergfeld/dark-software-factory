"""Azure Cosmos DB-backed MemoryStore (real ``azure`` mode adapter).

Talks to a narrow :class:`CosmosGateway` (``upsert`` + single-field equality
``query``). The default gateway wraps ``azure-cosmos`` (aio) and is built
lazily. Ranking for ``query_similar`` reuses the same token-overlap scorer as
``InMemoryMemoryStore`` (native vector search is deferred -- see ADR 0006).
"""

from __future__ import annotations

from typing import Any, Protocol

from dsf.memory.store import _overlap

_WORKING = "working"
_RECORDS = "records"
_LESSONS = "lessons"


class CosmosGateway(Protocol):
    """Narrow async seam over Cosmos data-plane operations."""

    async def upsert(self, container: str, item: dict) -> None: ...
    async def query(self, container: str, field: str, value: Any) -> list[dict]: ...


class CosmosMemoryStore:
    """:class:`~dsf.ports.MemoryStore` backed by Cosmos DB (one DB per product)."""

    def __init__(self, gateway: CosmosGateway) -> None:
        self._gw = gateway
        self._seq = 0

    @classmethod
    def from_endpoint(cls, endpoint: str, *, database: str) -> CosmosMemoryStore:
        """Build a store backed by the real Cosmos SDK gateway."""
        return cls(_SdkCosmosGateway(endpoint, database))

    def _next_seq(self) -> int:
        self._seq += 1
        return self._seq

    async def put_working(self, key: str, value: Any, ttl: float | None = None) -> None:
        item: dict[str, Any] = {"id": key, "key": key, "value": value}
        if ttl is not None:
            item["ttl"] = int(ttl)
        await self._gw.upsert(_WORKING, item)

    async def get_working(self, key: str) -> Any | None:
        rows = await self._gw.query(_WORKING, "key", key)
        return rows[0]["value"] if rows else None

    async def put_record(self, record: dict, ttl: float | None = None) -> None:
        seq = self._next_seq()
        item = dict(record)
        item.setdefault("id", f"rec-{seq}")
        item["_seq"] = seq
        if ttl is not None:
            item["ttl"] = int(ttl)
        await self._gw.upsert(_RECORDS, item)

    async def query_similar(self, text: str, kind: str, k: int = 5) -> list[dict]:
        rows = await self._gw.query(_RECORDS, "kind", kind)
        scored = sorted(
            ((_overlap(text, str(r.get("text", ""))), r) for r in rows),
            key=lambda pair: pair[0],
            reverse=True,
        )
        return [
            {rk: rv for rk, rv in r.items() if not rk.startswith("_") and rk != "ttl"}
            | {"similarity": sim}
            for sim, r in scored[:k]
        ]

    async def put_lesson(self, lesson: dict) -> None:
        seq = self._next_seq()
        item = dict(lesson)
        item.setdefault("id", f"lesson-{seq}")
        item["_seq"] = seq
        await self._gw.upsert(_LESSONS, item)

    async def get_lessons(self, product: str, k: int = 5) -> list[dict]:
        rows = await self._gw.query(_LESSONS, "product", product)
        rows.sort(key=lambda r: r.get("_seq", 0))
        return [
            {rk: rv for rk, rv in r.items() if not rk.startswith("_")}
            for r in rows[-k:][::-1]
        ]


class _SdkCosmosGateway:
    """Real gateway wrapping ``azure-cosmos`` aio (lazy import).

    ``field`` is always an internal constant (``key``/``kind``/``product``), so
    the f-string query is not user-controlled.
    """

    def __init__(self, endpoint: str, database: str) -> None:
        self._endpoint = endpoint
        self._database = database
        self._client: Any = None

    def _container(self, name: str) -> Any:  # pragma: no cover - requires azure extra
        if self._client is None:
            try:
                from azure.cosmos.aio import CosmosClient
                from azure.identity.aio import DefaultAzureCredential
            except ImportError as exc:
                raise RuntimeError(
                    "azure extra not installed; run: uv pip install -e '.[azure]'"
                ) from exc
            self._client = CosmosClient(
                self._endpoint, credential=DefaultAzureCredential()
            )
        return self._client.get_database_client(self._database).get_container_client(name)

    async def upsert(self, container: str, item: dict) -> None:  # pragma: no cover
        await self._container(container).upsert_item(item)

    async def query(
        self, container: str, field: str, value: Any
    ) -> list[dict]:  # pragma: no cover
        cont = self._container(container)
        query = f"SELECT * FROM c WHERE c.{field} = @value"
        params = [{"name": "@value", "value": value}]
        return [item async for item in cont.query_items(query=query, parameters=params)]
