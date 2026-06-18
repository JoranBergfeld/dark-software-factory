"""AzureOpenAIModelClient — Azure OpenAI adapter, exercised offline."""

import sys

from pydantic import BaseModel

from dsf.model.azure_client import AzureOpenAIModelClient
from tests.support.azure_doubles import RecordingChatGateway


class _Verdict(BaseModel):
    accept: bool
    reason: str


async def test_complete_returns_prose_without_schema():
    client = AzureOpenAIModelClient(RecordingChatGateway(response="hello there"))
    assert await client.complete("sys", "say hi") == "hello there"


async def test_complete_parses_into_schema():
    gw = RecordingChatGateway(response='{"accept": true, "reason": "grounded"}')
    client = AzureOpenAIModelClient(gw)
    out = await client.complete("sys", "judge", schema=_Verdict)
    assert isinstance(out, _Verdict)
    assert out.accept is True
    assert out.reason == "grounded"


async def test_complete_passes_json_schema_when_schema_given():
    gw = RecordingChatGateway(response='{"accept": false, "reason": "x"}')
    await AzureOpenAIModelClient(gw).complete("sys", "judge", schema=_Verdict)
    sent = gw.calls[0]["json_schema"]
    assert sent is not None
    assert sent["name"] == "_Verdict"
    assert "accept" in sent["schema"]["properties"]


async def test_complete_no_schema_passes_none():
    gw = RecordingChatGateway(response="prose")
    await AzureOpenAIModelClient(gw).complete("sys", "p")
    assert gw.calls[0]["json_schema"] is None


def test_module_import_is_sdk_free():
    assert "openai" not in sys.modules
