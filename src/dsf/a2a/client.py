"""A2A client — how the orchestrator calls a source agent.

Two transports are supported through the same functions:

* **Network:** pass an ``endpoint`` URL; an :class:`httpx.AsyncClient` makes a
  real HTTP request.
* **In-process (local dry-run):** pass ``transport=httpx.ASGITransport(app=...)``
  (and optionally ``base_url``); the request is routed straight into the agent's
  FastAPI app with no socket. This is how the orchestrator drives in-process
  agents during local dry-run.

Any timeout or transport error is swallowed into a degraded
:class:`~dsf.a2a.envelope.A2AResponse` (never raised) — partial coverage is
explicit, never fabricated.
"""

from __future__ import annotations

import httpx

from dsf.a2a.card import AgentCard
from dsf.a2a.envelope import A2ARequest, A2AResponse

#: Base URL used when routing into an in-process ASGI app.
DEFAULT_BASE_URL = "http://agent"


def _error_text(exc: Exception) -> str:
    """Produce a non-empty error string for a degraded response.

    Some httpx transport errors (e.g. ConnectTimeout) carry an empty message;
    fall back to the exception type name so ``error`` is always meaningful.
    """
    text = str(exc).strip()
    return text or type(exc).__name__


def _auth_headers(token: str | None) -> dict[str, str]:
    """Build the Authorization header dict for ``token`` (empty if none)."""
    if token:
        return {"Authorization": f"Bearer {token}"}
    return {}


def _make_client(
    endpoint: str | None,
    transport: httpx.ASGITransport | None,
    base_url: str | None,
    timeout: float,
    headers: dict[str, str],
) -> httpx.AsyncClient:
    """Construct an AsyncClient for either network or in-process transport."""
    if transport is not None:
        return httpx.AsyncClient(
            transport=transport,
            base_url=base_url or DEFAULT_BASE_URL,
            timeout=timeout,
            headers=headers,
        )
    return httpx.AsyncClient(timeout=timeout, headers=headers)


def _url(endpoint: str | None, transport: httpx.ASGITransport | None, path: str) -> str:
    """Resolve the request URL/path for the chosen transport."""
    if transport is not None:
        return path
    base = (endpoint or "").rstrip("/")
    return f"{base}{path}"


async def gather(
    endpoint: str | None,
    scope: dict,
    token: str | None = None,
    timeout: float = 10.0,
    *,
    transport: httpx.ASGITransport | None = None,
    base_url: str | None = None,
) -> A2AResponse:
    """Call an agent's ``/gather`` and return its :class:`A2AResponse`.

    On :class:`httpx.TimeoutException` or any transport/HTTP error, returns a
    degraded response ``A2AResponse(evidence=[], degraded=True, error=str(e))``
    instead of raising.
    """
    request = A2ARequest(run_scope=dict(scope))
    headers = _auth_headers(token)
    try:
        async with _make_client(endpoint, transport, base_url, timeout, headers) as client:
            resp = await client.post(
                _url(endpoint, transport, "/gather"),
                json=request.model_dump(mode="json"),
            )
            resp.raise_for_status()
            return A2AResponse.model_validate(resp.json())
    except Exception as exc:  # noqa: BLE001 - degrade, never propagate
        return A2AResponse(evidence=[], degraded=True, error=_error_text(exc))


async def fetch_card(
    endpoint: str | None,
    token: str | None = None,
    timeout: float = 10.0,
    *,
    transport: httpx.ASGITransport | None = None,
    base_url: str | None = None,
) -> AgentCard | None:
    """Fetch an agent's :class:`AgentCard` from ``GET /card``.

    Returns ``None`` on any timeout/transport/HTTP error rather than raising.
    """
    headers = _auth_headers(token)
    try:
        async with _make_client(endpoint, transport, base_url, timeout, headers) as client:
            resp = await client.get(_url(endpoint, transport, "/card"))
            resp.raise_for_status()
            return AgentCard.model_validate(resp.json())
    except Exception:  # noqa: BLE001 - treat an unreachable card as unknown
        return None


__all__: list[str] = ["DEFAULT_BASE_URL", "fetch_card", "gather"]
