"""AzureOpenAIEmbeddingClient — Azure OpenAI embeddings adapter, exercised offline."""

import sys

from dsf.model.azure_embeddings import AzureOpenAIEmbeddingClient
from dsf_testing.azure_doubles import RecordingEmbeddingsGateway


async def test_embed_returns_vectors_from_gateway():
    gw = RecordingEmbeddingsGateway(vectors=[[0.1, 0.2], [0.3, 0.4]])
    client = AzureOpenAIEmbeddingClient(gw)

    out = await client.embed(["alpha", "beta"])

    assert out == [[0.1, 0.2], [0.3, 0.4]]
    assert gw.calls[0]["texts"] == ["alpha", "beta"]


def test_module_import_is_sdk_free():
    assert "openai" not in sys.modules
