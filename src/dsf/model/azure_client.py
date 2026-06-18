"""Azure OpenAI-backed ModelClient (real ``azure`` mode adapter).

Talks to a narrow :class:`ChatGateway` (one ``complete`` coroutine). The
default gateway wraps the ``openai`` ``AsyncAzureOpenAI`` client and is built
lazily, so importing this module never requires the ``openai`` package. When a
``schema`` is given the adapter requests a strict ``json_schema`` response and
validates the reply into the model; otherwise it returns prose.
"""

from __future__ import annotations

from typing import Any, Protocol

from pydantic import BaseModel


class ChatGateway(Protocol):
    """Narrow async seam over a single chat completion."""

    async def complete(
        self, system: str, prompt: str, json_schema: dict | None
    ) -> str: ...


class AzureOpenAIModelClient:
    """:class:`~dsf.ports.ModelClient` backed by Azure OpenAI."""

    def __init__(self, gateway: ChatGateway) -> None:
        self._gw = gateway

    @classmethod
    def from_endpoint(
        cls, endpoint: str, *, deployment: str, api_version: str = "2024-08-01-preview"
    ) -> AzureOpenAIModelClient:
        """Build a client backed by the real Azure OpenAI SDK gateway."""
        return cls(_SdkChatGateway(endpoint, deployment, api_version))

    async def complete(
        self,
        system: str,
        prompt: str,
        schema: type[BaseModel] | None = None,
    ) -> BaseModel | str:
        json_schema = None
        if schema is not None:
            json_schema = {
                "name": schema.__name__,
                "schema": schema.model_json_schema(),
                "strict": True,
            }
        content = await self._gw.complete(system, prompt, json_schema)
        if schema is not None:
            return schema.model_validate_json(content)
        return content


class _SdkChatGateway:
    """Real gateway wrapping ``openai.AsyncAzureOpenAI`` (lazy import)."""

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

    async def complete(
        self, system: str, prompt: str, json_schema: dict | None
    ) -> str:  # pragma: no cover - requires azure extra
        client = self._ensure()
        kwargs: dict[str, Any] = {
            "model": self._deployment,
            "messages": [
                {"role": "system", "content": system},
                {"role": "user", "content": prompt},
            ],
        }
        if json_schema is not None:
            kwargs["response_format"] = {
                "type": "json_schema",
                "json_schema": json_schema,
            }
        resp = await client.chat.completions.create(**kwargs)
        return resp.choices[0].message.content or ""
