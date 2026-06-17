"""Tests for the Sentry MCP-server backend client (no network)."""

from __future__ import annotations

import pytest

from dsf.agents.sentry.backend import SentryMcpBackend
from dsf.agents.sentry.main import build_agent
from dsf.agents.sentry.mcp_client import (
    build_sentry_mcp_call_from_env,
    parse_search_issues,
)

POPULATED = """# Search Results for "is:unresolved"

**Suggested presentation:** Cards work well for these issues.

## Unhandled TypeError in checkout

**Issue ID**: VO21-PROJ-12
**Status**: unresolved
**Events**: 1,284
**Users Impacted**: 312
**Permalink**: https://vo21.sentry.io/issues/4815162342/

## KeyError: tenant_id

**Status**: unresolved
**Occurrences**: 56
**Users**: 7
https://vo21.sentry.io/issues/4823390011/

## Card without a link

**Status**: ignored
"""

EMPTY = """# Search Results for "is:unresolved"

**Suggested presentation:** Cards work well for these issues.

No issues found matching your search criteria.
"""


def test_parse_populated_cards():
    issues = parse_search_issues(POPULATED)
    assert len(issues) == 2  # the link-less card is skipped
    first, second = issues
    assert first["title"] == "Unhandled TypeError in checkout"
    assert first["permalink"] == "https://vo21.sentry.io/issues/4815162342/"
    assert first["count"] == 1284
    assert first["user_count"] == 312
    assert second["title"] == "KeyError: tenant_id"
    assert second["permalink"] == "https://vo21.sentry.io/issues/4823390011/"
    assert second["count"] == 56
    assert second["user_count"] == 7


def test_parse_no_issues():
    assert parse_search_issues(EMPTY) == []


async def test_mcp_call_maps_args_and_parses():
    seen: dict = {}

    async def fake_tool_caller(tool_name: str, arguments: dict) -> str:
        seen["tool"] = tool_name
        seen["args"] = arguments
        return POPULATED

    mcp_call = build_sentry_mcp_call_from_env(tool_caller=fake_tool_caller)
    issues = await mcp_call(
        "search_issues",
        organization_slug="vo21",
        project_slug="api",
        query="is:unresolved is:regression",
    )
    assert seen["tool"] == "search_issues"
    assert seen["args"]["organizationSlug"] == "vo21"
    assert seen["args"]["query"] == "is:unresolved is:regression"
    assert seen["args"]["projectSlugOrId"] == "api"
    assert seen["args"]["limit"] == 25
    assert len(issues) == 2


async def test_mcp_call_ignores_other_tools():
    async def fake_tool_caller(tool_name: str, arguments: dict) -> str:  # pragma: no cover
        raise AssertionError("should not be called")

    mcp_call = build_sentry_mcp_call_from_env(tool_caller=fake_tool_caller)
    assert await mcp_call("search_events", organization_slug="vo21") == []


def test_build_agent_live_prefers_mcp(monkeypatch):
    monkeypatch.setenv("SENTRY_MCP_URL", "http://192.168.5.39:8403/mcp")
    monkeypatch.delenv("SENTRY_AUTH_TOKEN", raising=False)
    agent = build_agent(mode="live")
    assert isinstance(agent.backend, SentryMcpBackend)


def test_build_agent_live_requires_a_source(monkeypatch):
    monkeypatch.delenv("SENTRY_MCP_URL", raising=False)
    monkeypatch.delenv("SENTRY_AUTH_TOKEN", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
