"""Deterministic fake ModelClient."""

from __future__ import annotations

from collections.abc import Callable

from pydantic import BaseModel


class FakeModelClient:
    """Deterministic model client.

    Handlers are registered against a *tag* substring. When ``complete`` is
    called, the first handler whose tag appears in the prompt is invoked and
    its result returned. This lets the synthesizer/council receive canned
    structured results in dry-run with no LLM.
    """

    def __init__(self) -> None:
        self._handlers: list[tuple[str, Callable[[str, str], BaseModel | str]]] = []
        self.calls: list[tuple[str, str]] = []

    def register(self, tag: str, handler: Callable[[str, str], BaseModel | str]) -> None:
        """Register a handler keyed on a ``tag`` substring of the prompt."""
        self._handlers.append((tag, handler))

    async def complete(
        self,
        system: str,
        prompt: str,
        schema: type[BaseModel] | None = None,
    ) -> BaseModel | str:
        """Return deterministic output for the first matching tag handler."""
        self.calls.append((system, prompt))
        for tag, handler in self._handlers:
            if tag in prompt:
                return handler(system, prompt)
        # Default deterministic echo so the port never raises in dry-run.
        return f"[fake-model] {prompt}"
