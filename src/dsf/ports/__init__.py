"""Port protocols — the seams every external dependency hides behind.

Each port is a :class:`typing.Protocol`. I/O-bearing methods are async so the
real Azure/MCP implementations can do network calls; the in-memory fakes in
``dsf.fakes`` satisfy the same signatures deterministically for dry-run.
"""

from __future__ import annotations

from contextlib import AbstractContextManager
from typing import Any, Protocol, runtime_checkable

from pydantic import BaseModel

from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem


@runtime_checkable
class ModelClient(Protocol):
    """LLM completion port.

    The fake returns deterministic output driven by a handler registered
    against a tag substring found in the prompt.
    """

    async def complete(
        self,
        system: str,
        prompt: str,
        schema: type[BaseModel] | None = None,
    ) -> BaseModel | str:
        """Complete a prompt, optionally parsing into ``schema``."""
        ...


@runtime_checkable
class MemoryStore(Protocol):
    """Unified institutional memory (working + long-term + retrieval)."""

    async def put_working(self, key: str, value: Any, ttl: float | None = None) -> None:
        """Store a value in the working (short-term/TTL) tier."""
        ...

    async def get_working(self, key: str) -> Any | None:
        """Read a value from the working tier (None if missing/expired)."""
        ...

    async def put_record(self, record: dict) -> None:
        """Persist a durable long-term record."""
        ...

    async def query_similar(self, text: str, kind: str, k: int = 5) -> list[dict]:
        """Retrieve up to ``k`` records of ``kind`` similar to ``text``."""
        ...

    async def put_lesson(self, lesson: dict) -> None:
        """Persist a product-scoped lesson."""
        ...

    async def get_lessons(self, product: str, k: int = 5) -> list[dict]:
        """Retrieve up to ``k`` lessons for ``product``."""
        ...


@runtime_checkable
class ConfigStore(Protocol):
    """Feature flags + tunable config (control center backend)."""

    def is_enabled(self, flag: str, product: str | None = None) -> bool:
        """Whether ``flag`` is enabled (optionally per-product)."""
        ...

    def get_value(self, key: str, default: Any = None) -> Any:
        """Read a config value, falling back to ``default``."""
        ...

    def set_flag(self, flag: str, value: bool, product: str | None = None) -> None:
        """Set a feature flag (optionally per-product)."""
        ...

    def snapshot(self) -> dict:
        """Return a full snapshot of current config/flags."""
        ...


@runtime_checkable
class GitHubClient(Protocol):
    """GitHub issue-filing port."""

    async def create_issue(
        self,
        repo: str,
        title: str,
        body: str,
        labels: list[str],
    ) -> str:
        """Create an issue and return its URL."""
        ...


@runtime_checkable
class SourceBackend(Protocol):
    """A source agent's evidence-gathering backend."""

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Gather evidence for the given run scope."""
        ...


@runtime_checkable
class Tracer(Protocol):
    """Observability span emitter."""

    def span(self, name: str, **attrs: Any) -> AbstractContextManager[Any]:
        """Open a span as a context manager."""
        ...


__all__ = [
    "ConfigStore",
    "GitHubClient",
    "MemoryStore",
    "ModelClient",
    "SourceBackend",
    "Tracer",
    "SourceKind",
]
