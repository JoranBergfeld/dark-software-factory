"""Semantic (embedding cosine) ranking in query_similar.

These tests prove the store ranks by vector cosine similarity when an embedder
is present -- not by lexical token overlap -- which is what makes dark-mode
deduplication catch reworded duplicates.
"""

from __future__ import annotations

from dsf.memory.azure_store import CosmosMemoryStore
from dsf.memory.store import InMemoryMemoryStore
from dsf_testing.azure_doubles import InMemoryCosmosGateway, RecordingEmbeddingsGateway

# Query is lexically closest to the "tutorial" record (shares submit/checkout)
# but semantically closest to the "payment" record. Vectors encode the meaning.
_QUERY = "Checkout fails on submit"
_PAYMENT = "Users cannot complete purchase at payment step"
_TUTORIAL = "Submit a checkout form tutorial"
_VECTORS = {
    _QUERY: [1.0, 0.0],
    _PAYMENT: [0.99, 0.14],  # cosine with query ~0.99
    _TUTORIAL: [0.0, 1.0],  # cosine with query 0.0
}


async def test_query_similar_ranks_by_semantic_cosine_not_lexical():
    embedder = RecordingEmbeddingsGateway(_VECTORS, dim=2)
    store = InMemoryMemoryStore(embedder=embedder)
    await store.put_record({"kind": "issue", "text": _PAYMENT})
    await store.put_record({"kind": "issue", "text": _TUTORIAL})

    hits = await store.query_similar(_QUERY, "issue", k=2)

    # Semantic match wins despite zero lexical overlap with the query.
    assert hits[0]["text"] == _PAYMENT
    assert hits[0]["similarity"] > 0.9
    # The lexically-overlapping record ranks last (semantically distant).
    assert hits[1]["text"] == _TUTORIAL
    assert hits[1]["similarity"] < 0.5


async def test_query_similar_falls_back_to_lexical_without_embedder():
    store = InMemoryMemoryStore()  # no embedder configured (offline)
    await store.put_record({"kind": "issue", "text": _TUTORIAL})

    hits = await store.query_similar(_QUERY, "issue", k=1)

    # Lexical overlap still functions as the offline fallback.
    assert hits[0]["text"] == _TUTORIAL
    assert hits[0]["similarity"] > 0.0


async def test_cosmos_query_similar_ranks_by_semantic_cosine():
    embedder = RecordingEmbeddingsGateway(_VECTORS, dim=2)
    store = CosmosMemoryStore(InMemoryCosmosGateway(), embedder=embedder)
    await store.put_record({"kind": "issue", "text": _PAYMENT})
    await store.put_record({"kind": "issue", "text": _TUTORIAL})

    hits = await store.query_similar(_QUERY, "issue", k=2)

    assert hits[0]["text"] == _PAYMENT
    assert hits[0]["similarity"] > 0.9
    assert hits[1]["text"] == _TUTORIAL
