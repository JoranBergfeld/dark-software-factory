"""Tests for SRE wiring and the run_sweep entrypoint."""

from __future__ import annotations

from dsf.agents.grafana.backend import GrafanaFixtureBackend
from dsf.agents.sentry.backend import SentryFixtureBackend
from dsf.container import build_services
from dsf.sre.main import run_sweep
from dsf.sre.models import SreSweepResult
from dsf.sre.wiring import build_sre_agent


def test_build_sre_agent_defaults_to_fixture_backends() -> None:
    services = build_services("local")
    agent = build_sre_agent(services)
    kinds = {type(b) for b in agent.backends}
    assert kinds == {SentryFixtureBackend, GrafanaFixtureBackend}


def test_build_sre_agent_accepts_custom_backends() -> None:
    services = build_services("local")
    agent = build_sre_agent(services, backends=[SentryFixtureBackend()])
    assert len(agent.backends) == 1


async def test_run_sweep_returns_result_with_observations() -> None:
    services = build_services("local")
    result = await run_sweep(services, {"products": ["microbi"]})
    assert isinstance(result, SreSweepResult)
    assert result.observed > 0


async def test_run_sweep_dry_run_files_nothing() -> None:
    services = build_services("local")
    result = await run_sweep(services, dry_run=True)
    assert result.dry_run is True
    assert services.github.calls == []
