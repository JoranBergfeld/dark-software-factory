"""Deterministic, offline ModelClient double for tests.

``DeterministicModelClient`` is the rule/template-based
:class:`~dsf.ports.ModelClient` implementation. Handlers are registered against
a *tag* substring; when ``complete`` is called, the first handler whose tag
appears in the prompt is invoked and its result returned. This lets the
synthesizer/council receive deterministic structured results with no LLM call.
"""

from __future__ import annotations

from collections.abc import Callable

from pydantic import BaseModel

ECHO_PREFIX = "[deterministic]"


class DeterministicModelClient:
    """Rule/template-based model client returning deterministic output.

    Handlers are registered against a *tag* substring. When ``complete`` is
    called, the first handler whose tag appears in the prompt is invoked and
    its result returned. When no handler matches, a deterministic echo prefixed
    with :data:`ECHO_PREFIX` is returned so the port never raises.
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
        # Default deterministic echo so the port never raises.
        return f"{ECHO_PREFIX} {prompt}"


__all__ = ["ECHO_PREFIX", "DeterministicModelClient"]
