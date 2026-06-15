"""Tests for the in-memory fakes (plan Task 0.3)."""

from __future__ import annotations

from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance
from dsf.fakes import (
    FakeConfigStore,
    FakeGitHubClient,
    FakeMemoryStore,
    FakeModelClient,
    FakeSourceBackend,
    FakeTracer,
)
from dsf.ports import (
    ConfigStore,
    GitHubClient,
    MemoryStore,
    ModelClient,
    SourceBackend,
    Tracer,
)


def test_fakes_satisfy_protocols():
    assert isinstance(FakeModelClient(), ModelClient)
    assert isinstance(FakeMemoryStore(), MemoryStore)
    assert isinstance(FakeConfigStore.from_defaults(), ConfigStore)
    assert isinstance(FakeGitHubClient(), GitHubClient)
    assert isinstance(FakeSourceBackend(), SourceBackend)
    assert isinstance(FakeTracer(), Tracer)


async def test_model_client_handler_keyed_on_tag():
    client = FakeModelClient()
    client.register("##SYNTH##", lambda s, p: "synthesized")
    out = await client.complete("sys", "please ##SYNTH## now")
    assert out == "synthesized"
    # Deterministic: same call, same result.
    again = await client.complete("sys", "please ##SYNTH## now")
    assert again == "synthesized"
    # Unmatched prompt falls back to deterministic echo.
    miss = await client.complete("sys", "nothing here")
    assert miss.startswith("[fake-model]")


async def test_memory_store_working_and_records():
    mem = FakeMemoryStore()
    await mem.put_working("k", {"x": 1})
    assert await mem.get_working("k") == {"x": 1}
    assert await mem.get_working("missing") is None

    await mem.put_record({"kind": "proposal", "text": "add retry logic to client"})
    await mem.put_record({"kind": "proposal", "text": "fix unrelated typo"})
    await mem.put_record({"kind": "other", "text": "add retry logic to client"})

    hits = await mem.query_similar("add retry logic to the http client", "proposal", k=5)
    assert hits  # only 'proposal' kind, ranked by overlap
    assert all(h["kind"] == "proposal" for h in hits)
    assert hits[0]["similarity"] >= hits[-1]["similarity"]
    assert "retry" in hits[0]["text"]


async def test_memory_store_lessons():
    mem = FakeMemoryStore()
    await mem.put_lesson({"product": "alpha", "text": "lesson one"})
    await mem.put_lesson({"product": "beta", "text": "other"})
    lessons = await mem.get_lessons("alpha")
    assert len(lessons) == 1
    assert lessons[0]["text"] == "lesson one"


def test_config_store_seeded_defaults():
    cfg = FakeConfigStore.from_defaults()
    assert cfg.is_enabled("dry_run") is True
    assert cfg.is_enabled("critic.grounding") is True
    assert cfg.is_enabled("agent.SENTRY") is True
    assert cfg.is_enabled("trigger.SIGNAL.paused") is False
    assert cfg.get_value("default_threshold") == 0.6
    assert cfg.get_value("critics.value.weight") == 1.0


def test_config_store_set_flag_and_snapshot():
    cfg = FakeConfigStore.from_defaults()
    cfg.set_flag("critic.value", False)
    assert cfg.is_enabled("critic.value") is False
    cfg.set_flag("critic.value", True, product="alpha")
    assert cfg.is_enabled("critic.value", product="alpha") is True
    snap = cfg.snapshot()
    assert "_overrides" in snap


async def test_github_client_records_and_returns_local_url():
    gh = FakeGitHubClient()
    url1 = await gh.create_issue("org/repo", "Title", "Body", ["bug"])
    url2 = await gh.create_issue("org/repo", "Title2", "Body2", [])
    assert url1 == "local://issue/1"
    assert url2 == "local://issue/2"
    assert len(gh.calls) == 2
    assert gh.calls[0]["labels"] == ["bug"]


async def test_source_backend_returns_provided_list():
    item = EvidenceItem(
        source_agent="sentry",
        claim="boom",
        raw_citation="sentry://1",
        provenance=Provenance(query_used="q", source_kind=SourceKind.SENTRY),
    )
    backend = FakeSourceBackend([item])
    out = await backend.gather({"product": "alpha"})
    assert len(out) == 1
    assert out[0].raw_citation == "sentry://1"
    assert backend.calls == [{"product": "alpha"}]


def test_tracer_records_spans():
    tracer = FakeTracer()
    with tracer.span("s1_triage", run="r1"):
        pass
    with tracer.span("s2_investigation"):
        pass
    assert [name for name, _ in tracer.spans] == ["s1_triage", "s2_investigation"]
    assert tracer.spans[0][1] == {"run": "r1"}
