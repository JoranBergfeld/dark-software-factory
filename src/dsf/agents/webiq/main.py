"""WebIQ agent entrypoint (plan Task 2.5).

Builds the A2A app over the WebIQ backend, selecting by mode. Run with
``uvicorn dsf.agents.webiq.main:app``. In live mode the real web-search backend
(:class:`dsf.agents.webiq.backend.WebIqMcpBackend`) is wired to a provider client
(Tavily) built from env vars; otherwise the fixture-backed fake is used.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.mode import is_live, resolve_mode
from dsf.agents.webiq.backend import WebIqFakeBackend, WebIqMcpBackend
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the WebIQ :class:`SourceAgent`, selecting the backend by mode.

    In live mode (``DSF_MODE`` set to anything but ``local``, or ``mode``
    explicitly live) the real web-search backend is wired to the provider client
    (Tavily) built from env vars. Otherwise the deterministic fixture-backed fake
    is used.
    """
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.webiq.client import build_webiq_client_from_env

        backend = WebIqMcpBackend(search=build_webiq_client_from_env())
    else:
        backend = WebIqFakeBackend()
    return SourceAgent(
        kind=SourceKind.WEBIQ,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
