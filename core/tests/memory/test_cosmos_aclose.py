"""_SdkCosmosGateway.aclose() closes the aio client + credential exactly once."""

from __future__ import annotations

import pytest

from dsf.charter.cosmos_store import CosmosCharterStore
from dsf.memory.azure_store import CosmosMemoryStore, _SdkCosmosGateway


class _Closable:
    def __init__(self) -> None:
        self.closed = 0

    async def close(self) -> None:
        self.closed += 1


async def test_sdk_gateway_aclose_closes_client_and_credential():
    gw = _SdkCosmosGateway("https://example.documents.azure.com", "prod")
    client, cred = _Closable(), _Closable()
    gw._client, gw._cred = client, cred
    await gw.aclose()
    assert client.closed == 1 and cred.closed == 1


async def test_sdk_gateway_aclose_before_first_use_is_noop():
    gw = _SdkCosmosGateway("https://example.documents.azure.com", "prod")
    await gw.aclose()  # no client built yet -> must not raise


async def test_sdk_gateway_aclose_closes_credential_even_if_client_close_raises():
    class _Boom:
        async def close(self) -> None:
            raise RuntimeError("client close failed")

    gw = _SdkCosmosGateway("https://example.documents.azure.com", "prod")
    client, cred = _Boom(), _Closable()
    gw._client, gw._cred = client, cred
    with pytest.raises(RuntimeError, match="client close failed"):
        await gw.aclose()
    assert cred.closed == 1
    assert gw._client is None
    assert gw._cred is None


async def test_stores_delegate_aclose_to_gateway():
    class _Gw:
        def __init__(self) -> None:
            self.closed = 0

        async def aclose(self) -> None:
            self.closed += 1

    gw = _Gw()
    await CosmosMemoryStore(gw).aclose()
    assert gw.closed == 1
    gw2 = _Gw()
    await CosmosCharterStore(gw2).aclose()
    assert gw2.closed == 1
