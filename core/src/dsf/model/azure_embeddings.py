"""Azure OpenAI-backed EmbeddingClient (real ``azure`` mode adapter).

Talks to a narrow :class:`EmbeddingsGateway` (one ``embed`` coroutine). The
default gateway wraps the ``openai`` ``AsyncAzureOpenAI`` client and is built
lazily, so importing this module never requires the ``openai`` package. Used to
power semantic deduplication: record texts are embedded on write and ranked by
cosine similarity in the memory store.
"""

from __future__ import annotations

from typing import Any, Protocol


class EmbeddingsGateway(Protocol):
    """Narrow async seam over a batch embedding call."""

    async def embed(self, texts: list[str]) -> list[list[float]]: ...


class AzureOpenAIEmbeddingClient:
    """:class:`~dsf.ports.EmbeddingClient` backed by Azure OpenAI embeddings."""

    def __init__(self, gateway: EmbeddingsGateway) -> None:
        self._gw = gateway

    @classmethod
    def from_endpoint(
        cls, endpoint: str, *, deployment: str, api_version: str = "2024-08-01-preview"
    ) -> AzureOpenAIEmbeddingClient:
        """Build a client backed by the real Azure OpenAI SDK gateway."""
        return cls(_SdkEmbeddingsGateway(endpoint, deployment, api_version))

    async def embed(self, texts: list[str]) -> list[list[float]]:
        """Return one embedding vector per input text (order-preserving)."""
        if not texts:
            return []
        return await self._gw.embed(list(texts))


class _SdkEmbeddingsGateway:
    """Real gateway wrapping ``openai.AsyncAzureOpenAI`` embeddings (lazy import)."""

    def __init__(self, endpoint: str, deployment: str, api_version: str) -> None:
        self._endpoint = endpoint
        self._deployment = deployment
        self._api_version = api_version
        self._client: Any = None

    def _ensure(self) -> Any:  # pragma: no cover - requires azure extra
        if self._client is None:
            try:
                from azure.identity.aio import (
                    DefaultAzureCredential,
                    get_bearer_token_provider,
                )
                from openai import AsyncAzureOpenAI
            except ImportError as exc:
                raise RuntimeError(
                    "azure extra not installed; run: uv pip install -e '.[azure]'"
                ) from exc
            token_provider = get_bearer_token_provider(
                DefaultAzureCredential(),
                "https://cognitiveservices.azure.com/.default",
            )
            self._client = AsyncAzureOpenAI(
                azure_endpoint=self._endpoint,
                api_version=self._api_version,
                azure_ad_token_provider=token_provider,
            )
        return self._client

    async def embed(self, texts: list[str]) -> list[list[float]]:  # pragma: no cover
        client = self._ensure()
        resp = await client.embeddings.create(model=self._deployment, input=texts)
        return [item.embedding for item in resp.data]


__all__ = ["AzureOpenAIEmbeddingClient", "EmbeddingsGateway"]
