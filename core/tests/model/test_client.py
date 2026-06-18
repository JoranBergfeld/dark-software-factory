"""DeterministicModelClient behavior + protocol conformance."""

from __future__ import annotations

from dsf.model import DeterministicModelClient
from dsf.ports import ModelClient


def test_deterministic_model_satisfies_protocol():
    assert isinstance(DeterministicModelClient(), ModelClient)


async def test_model_client_handler_keyed_on_tag():
    client = DeterministicModelClient()
    client.register("##SYNTH##", lambda s, p: "synthesized")
    out = await client.complete("sys", "please ##SYNTH## now")
    assert out == "synthesized"
    # Deterministic: same call, same result.
    again = await client.complete("sys", "please ##SYNTH## now")
    assert again == "synthesized"
    # Unmatched prompt falls back to deterministic echo.
    miss = await client.complete("sys", "nothing here")
    assert miss.startswith("[deterministic]")
