"""Sentry MCP-server backend client.

Lets the Sentry agent gather evidence by speaking the **Model Context Protocol**
to a Sentry MCP server (e.g. one running in a homelab over Streamable HTTP),
rather than calling the Sentry REST API directly. This feeds the existing
:class:`dsf.agents.sentry.backend.SentryMcpBackend` — it supplies the injected
``mcp_call`` it expects.

Design: the transport (open an MCP session, call a tool, read the text result)
is isolated in ``_default_tool_caller`` and injectable as ``tool_caller`` so the
markdown→dict mapping stays unit-testable without a network. The Sentry MCP
server returns markdown text (no structured content), so issues are parsed out
of that text, anchored on the per-issue ``## `` card headings and issue URLs.

Env (used when ``tool_caller`` is not injected):
* ``SENTRY_MCP_URL``    — required; the MCP server's Streamable-HTTP URL.
* ``SENTRY_MCP_TOKEN``  — optional bearer token for the MCP endpoint.
* ``SENTRY_ORG``        — default organization slug when the run scope omits it.
"""

from __future__ import annotations

import logging
import os
import re
from collections.abc import Awaitable, Callable
from typing import Any

from dsf.agents.mode import env_required

_logger = logging.getLogger(__name__)

#: A tool caller: given an MCP tool name + arguments, return the tool's text.
ToolCaller = Callable[[str, dict], Awaitable[str]]

_URL_RE = re.compile(r"https?://[^\s)\]]+")


def _find_int(section: str, labels: list[str]) -> int | None:
    """Find ``**<Label>**: 1,234`` for any of ``labels`` and return the int."""
    for label in labels:
        m = re.search(rf"\*\*(?:{label})\*\*:\s*([\d,]+)", section, re.IGNORECASE)
        if m:
            return int(m.group(1).replace(",", ""))
    return None


def parse_search_issues(text: str) -> list[dict[str, Any]]:
    """Parse a Sentry MCP ``search_issues`` markdown result into issue dicts.

    Each issue is a ``## `` card; the title is the heading and the citation is
    the first issue URL in the card. ``count``/``user_count`` are best-effort
    (several label spellings). Cards without a URL are skipped (an EvidenceItem
    requires a non-empty citation).
    """
    issues: list[dict[str, Any]] = []
    # Sections after each "## " heading; the first chunk is the preamble.
    for section in re.split(r"\n##\s+", "\n" + text)[1:]:
        lines = section.splitlines()
        title = lines[0].strip() if lines else ""
        urls = _URL_RE.findall(section)
        permalink = next((u for u in urls if "/issues/" in u), urls[0] if urls else "")
        if not permalink:
            continue
        issues.append(
            {
                "title": title or "Sentry issue",
                "permalink": permalink,
                "count": _find_int(section, ["Events", "Occurrences", "Times Seen"]),
                "user_count": _find_int(section, ["Users Impacted", "Users", "User Count"]),
                "confidence": 0.75,
            }
        )
    return issues


async def _default_tool_caller(tool_name: str, arguments: dict) -> str:
    """Open a Streamable-HTTP MCP session, call ``tool_name``, return its text."""
    # Imported lazily so importing this module never requires the mcp SDK at
    # collection time and the fake/REST paths stay dependency-light.
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    url = env_required("SENTRY_MCP_URL", hint="the Sentry MCP server Streamable-HTTP URL")
    token = os.environ.get("SENTRY_MCP_TOKEN")
    headers = {"Authorization": f"Bearer {token}"} if token else None

    try:
        async with streamablehttp_client(url, headers=headers) as (read, write, _):
            async with ClientSession(read, write) as session:
                await session.initialize()
                result = await session.call_tool(tool_name, arguments)
                return "\n".join(
                    c.text for c in result.content if getattr(c, "text", None)
                )
    except Exception as exc:
        # MCP transport errors arrive wrapped in an anyio ExceptionGroup; unwrap
        # the first leaf so the developer log is actionable.
        cause = exc
        while isinstance(cause, BaseExceptionGroup) and cause.exceptions:
            cause = cause.exceptions[0]
        _logger.error(
            "Sentry MCP call %r via %s failed: %r", tool_name, url, cause, exc_info=True
        )
        raise RuntimeError(f"sentry-mcp:{tool_name}:failed") from exc


def build_sentry_mcp_call_from_env(
    tool_caller: ToolCaller | None = None,
) -> Callable[..., Awaitable[list[dict]]]:
    """Build the ``mcp_call`` for :class:`SentryMcpBackend` over an MCP server.

    ``tool_caller`` is injectable for tests; when ``None`` it defaults to a real
    Streamable-HTTP MCP session built from ``SENTRY_MCP_URL``.
    """
    call = tool_caller or _default_tool_caller

    async def mcp_call(tool_name: str, **kwargs: Any) -> list[dict]:
        if tool_name != "search_issues":
            return []
        org = kwargs.get("organization_slug") or os.environ.get("SENTRY_ORG")
        arguments: dict[str, Any] = {
            "organizationSlug": org,
            "query": kwargs.get("query", "is:unresolved"),
            "limit": 25,
        }
        project = kwargs.get("project_slug")
        if project:
            arguments["projectSlugOrId"] = project
        text = await call(tool_name, arguments)
        return parse_search_issues(text)

    return mcp_call


__all__ = ["build_sentry_mcp_call_from_env", "parse_search_issues", "ToolCaller"]
