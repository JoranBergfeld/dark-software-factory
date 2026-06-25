"""Live WebIQ web-research client backed by Azure AI Foundry.

Builds an async ``search(query) -> list[dict]`` callable backed by Azure AI
Foundry's *Grounding with Bing Search* tool (the Foundry Agents service), so
WebIQ does external/industry research through the product's own Azure AI Foundry
resource instead of a third-party search API. The returned dicts share the shape
:class:`dsf.agents.webiq.backend.WebIqMcpBackend` expects (``finding`` / ``url`` /
``confidence``), so the backend stays provider-agnostic.

Testability: the load-bearing *mapping* from a grounding response to result dicts
is pure and unit-tested. The actual Azure AI Agents call is isolated behind an
injectable ``runner`` seam; the real SDK-backed runner is built lazily (requires
the ``azure`` extra) and is ``# pragma: no cover`` like the other real Azure
adapters in the tree.
"""

from __future__ import annotations

import os
from dataclasses import dataclass, field
from typing import TYPE_CHECKING

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

#: Confidence assigned to a Bing-grounded citation (the grounding tool returns no
#: per-citation relevance score).
_DEFAULT_CONFIDENCE = 0.6
#: Confidence for the answer-only fallback when no citations are returned.
_ANSWER_CONFIDENCE = 0.5
#: Default cap on results per query.
_DEFAULT_MAX_RESULTS = 5


@dataclass
class GroundingCitation:
    """One source URL cited by a grounded answer."""

    url: str
    title: str = ""
    snippet: str = ""


@dataclass
class GroundingResult:
    """A grounded answer plus the source citations behind it."""

    answer: str = ""
    citations: list[GroundingCitation] = field(default_factory=list)


def _coerce_max_results(raw: str | None) -> int:
    """Parse ``WEBIQ_MAX_RESULTS`` into a positive int (fallback to default)."""
    if not raw:
        return _DEFAULT_MAX_RESULTS
    try:
        value = int(raw)
    except ValueError:
        return _DEFAULT_MAX_RESULTS
    return value if value > 0 else _DEFAULT_MAX_RESULTS


def _map_result(result: GroundingResult, max_results: int) -> list[dict]:
    """Map a :class:`GroundingResult` onto WebIQ result dicts.

    Each citation becomes one result (finding from its snippet, then title, then
    the synthesized answer). When grounding returned no citations but did return
    an answer, emit a single answer-only result with an empty URL so the finding
    is still captured. Capped at ``max_results``.
    """
    results: list[dict] = []
    for citation in result.citations:
        finding = citation.snippet or citation.title or result.answer or ""
        if not finding and not citation.url:
            continue
        results.append(
            {
                "finding": finding,
                "url": citation.url,
                "confidence": _DEFAULT_CONFIDENCE,
            }
        )
        if len(results) >= max_results:
            return results
    if not results and result.answer:
        results.append(
            {"finding": result.answer, "url": "", "confidence": _ANSWER_CONFIDENCE}
        )
    return results


def build_foundry_search_from_env(
    runner: Callable[[str], Awaitable[GroundingResult]] | None = None,
) -> Callable[[str], Awaitable[list[dict]]]:
    """Return an async ``search(query)`` backed by Azure AI Foundry grounding.

    Env vars (read when building the real runner):

    * ``AZURE_AI_PROJECT_ENDPOINT`` (required) — the Foundry *project* endpoint,
      e.g. ``https://<resource>.services.ai.azure.com/api/projects/<project>``.
    * ``WEBIQ_BING_CONNECTION_ID`` (required) — resource id of the project's
      *Grounding with Bing Search* connection.
    * ``WEBIQ_FOUNDRY_MODEL`` (default: ``AZURE_OPENAI_DEPLOYMENT``) — the model
      deployment the grounding agent runs on.
    * ``WEBIQ_MAX_RESULTS`` (default ``5``) — cap on results per query.

    When ``runner`` is ``None`` a real Azure AI Agents-backed runner is built
    lazily (requires the ``azure`` extra). Tests inject a ``runner`` returning a
    canned :class:`GroundingResult`, so the mapping is exercised without network.
    """
    max_results = _coerce_max_results(os.environ.get("WEBIQ_MAX_RESULTS"))
    real_runner = runner if runner is not None else _build_real_runner()

    async def search(query: str) -> list[dict]:
        result = await real_runner(query)
        return _map_result(result, max_results)

    return search


def _build_real_runner() -> Callable[[str], Awaitable[GroundingResult]]:
    """Build the real Azure AI Foundry grounding runner from env.

    Reads the required env (project endpoint + Bing connection + model) eagerly so
    a misconfiguration fails fast with a clear message; the returned runner spins
    up an ephemeral grounding agent per query, runs the Bing-grounded search, and
    maps the agent's reply (answer text + URL-citation annotations) into a
    :class:`GroundingResult`. The agent is always cleaned up.
    """
    endpoint = env_required(
        "AZURE_AI_PROJECT_ENDPOINT", hint="Azure AI Foundry project endpoint"
    )
    connection_id = env_required(
        "WEBIQ_BING_CONNECTION_ID", hint="Foundry Grounding-with-Bing connection id"
    )
    model = os.environ.get("WEBIQ_FOUNDRY_MODEL") or env_required(
        "AZURE_OPENAI_DEPLOYMENT", hint="grounding agent model deployment"
    )

    async def runner(query: str) -> GroundingResult:  # pragma: no cover - azure extra
        try:
            from azure.ai.agents.aio import AgentsClient
            from azure.ai.agents.models import BingGroundingTool, MessageRole
            from azure.identity.aio import DefaultAzureCredential
        except ImportError as exc:
            raise RuntimeError(
                "azure extra not installed; run: uv pip install -e '.[azure]'"
            ) from exc

        bing = BingGroundingTool(connection_id=connection_id)
        async with (
            DefaultAzureCredential() as credential,
            AgentsClient(endpoint=endpoint, credential=credential) as client,
        ):
            agent = await client.create_agent(
                model=model,
                name="webiq-grounding",
                instructions=(
                    "You are an industry/competitive web-research assistant. Use the "
                    "Bing grounding tool to answer the query with current, factual "
                    "findings, and cite your sources."
                ),
                tools=bing.definitions,
            )
            try:
                thread = await client.threads.create()
                await client.messages.create(
                    thread_id=thread.id, role=MessageRole.USER, content=query
                )
                await client.runs.create_and_process(
                    thread_id=thread.id, agent_id=agent.id
                )
                return await _collect_grounding(client, thread.id)
            finally:
                await client.delete_agent(agent.id)

    return runner


async def _collect_grounding(client, thread_id) -> GroundingResult:  # pragma: no cover
    """Extract the answer text + URL citations from the grounding agent's reply."""
    answer_parts: list[str] = []
    citations: list[GroundingCitation] = []
    seen: set[str] = set()
    async for message in client.messages.list(thread_id=thread_id):
        if message.role != "assistant":
            continue
        for item in getattr(message, "text_messages", []) or []:
            answer_parts.append(item.text.value)
        for ann in getattr(message, "url_citation_annotations", []) or []:
            cite = ann.url_citation
            if not cite.url or cite.url in seen:
                continue
            seen.add(cite.url)
            citations.append(
                GroundingCitation(url=cite.url, title=getattr(cite, "title", "") or "")
            )
    return GroundingResult(answer="\n".join(answer_parts).strip(), citations=citations)


__all__ = [
    "GroundingCitation",
    "GroundingResult",
    "build_foundry_search_from_env",
]
