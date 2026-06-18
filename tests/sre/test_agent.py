"""Tests for the SRE agent (observe -> fix-forward -> reflect -> sweep)."""

from __future__ import annotations

from dsf.agents.grafana.backend import GrafanaFixtureBackend
from dsf.agents.sentry.backend import SentryFixtureBackend
from dsf.container import build_services
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.sre.agent import SreAgent
from dsf.sre.models import SRE_LABEL, Incident


class _RaisingBackend:
    async def gather(self, run_scope: dict):
        raise RuntimeError("backend down")


def _incident(fingerprint: str = "fp-checkout", product: str = "microbi") -> Incident:
    return Incident(
        product=product,
        title="checkout 500s spiking",
        summary="checkout 500s spiking (confidence 0.90, source SENTRY)",
        severity="sev-critical",
        citations=["https://microbi.sentry.io/issues/1"],
        source_kinds=["SENTRY"],
        fingerprint=fingerprint,
    )


def _agent(services, backends) -> SreAgent:
    return SreAgent(
        services.github, services.memory, services.config, backends
    )


async def test_observe_concatenates_backends() -> None:
    services = build_services("local")
    agent = _agent(services, [SentryFixtureBackend(), GrafanaFixtureBackend()])
    evidence = await agent.observe({"products": ["microbi"]})
    assert len(evidence) == 6


async def test_observe_degrades_when_backend_raises() -> None:
    services = build_services("local")
    agent = _agent(services, [SentryFixtureBackend(), _RaisingBackend()])
    evidence = await agent.observe({})
    assert len(evidence) == 3


async def test_fix_forward_files_to_product_repo_with_labels() -> None:
    services = build_services("local")
    agent = _agent(services, [])
    url = await agent.fix_forward(_incident(), dry_run=False)
    assert url == "local://issue/1"
    call = services.github.calls[0]
    assert call["repo"] == "joranbergfeld/microbi"
    assert HANDOFF_LABEL in call["labels"]
    assert SRE_LABEL in call["labels"]
    assert "sev-critical" in call["labels"]


async def test_fix_forward_dedups_same_fingerprint() -> None:
    services = build_services("local")
    agent = _agent(services, [])
    await agent.fix_forward(_incident(), dry_run=False)
    second = await agent.fix_forward(_incident(), dry_run=False)
    assert second is None
    assert len(services.github.calls) == 1


async def test_dry_run_files_nothing_and_does_not_index() -> None:
    services = build_services("local")
    agent = _agent(services, [])
    dry = await agent.fix_forward(_incident(), dry_run=True)
    assert dry is None
    assert services.github.calls == []
    # A later real fix-forward of the same fingerprint must still file.
    real = await agent.fix_forward(_incident(), dry_run=False)
    assert real == "local://issue/1"


async def test_fix_forward_skips_unroutable_product() -> None:
    services = build_services("local")
    agent = _agent(services, [])
    url = await agent.fix_forward(_incident(product="nonesuch"), dry_run=False)
    assert url is None
    assert services.github.calls == []


async def test_reflect_records_a_product_lesson() -> None:
    services = build_services("local")
    agent = _agent(services, [])
    incident = _incident()
    await agent.reflect(incident, action="filed", url="local://issue/1")
    lessons = await services.memory.get_lessons("microbi")
    assert any(incident.summary in le.get("text", "") for le in lessons)


async def test_sweep_files_first_then_dedups() -> None:
    services = build_services("local")
    backends = [SentryFixtureBackend(), GrafanaFixtureBackend()]
    agent = _agent(services, backends)

    first = await agent.sweep({"products": ["microbi"]})
    assert first.observed == 6
    assert first.incidents > 0
    assert len(first.filed) == first.incidents
    assert first.duplicates == 0

    second = await agent.sweep({"products": ["microbi"]})
    assert second.filed == []
    assert second.duplicates == second.incidents


async def test_sweep_dry_run_files_nothing() -> None:
    services = build_services("local")
    backends = [SentryFixtureBackend(), GrafanaFixtureBackend()]
    agent = _agent(services, backends)
    result = await agent.sweep({"products": ["microbi"]}, dry_run=True)
    assert result.dry_run is True
    assert result.filed == []
    assert services.github.calls == []
