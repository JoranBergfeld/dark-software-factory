from __future__ import annotations

import pytest

from dsf.charter.interview import CharterInterviewer, CharterInterviewError, InterviewerTurn
from dsf.contracts.charter import Charter
from dsf_testing.model import DeterministicModelClient


def _draft() -> Charter:
    return Charter(
        product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"]
    )


async def test_interviewer_asks_then_finalizes():
    model = DeterministicModelClient()

    def handler(system: str, prompt: str):
        if prompt.count("user:") >= 2:
            return InterviewerTurn(message="Drafted.", done=True, draft=_draft())
        return InterviewerTurn(message="What problem does alpha solve?", done=False)

    model.register("[charter-interview]", handler)
    iv = CharterInterviewer(model, "alpha")
    first = await iv.start()
    assert not first.done and "alpha" in first.message
    assert not (await iv.respond("Slow dashboards")).done
    final = await iv.respond("Analysts")
    assert final.done and final.draft is not None and final.draft.vision == "V"


async def test_interviewer_normalizes_draft_product():
    model = DeterministicModelClient()
    model.register(
        "[charter-interview]",
        lambda s, p: InterviewerTurn(
            message="done",
            done=True,
            draft=Charter(
                product="WRONG", vision="V", target_users="U", goals=["g"], success_metrics=["m"]
            ),
        ),
    )
    turn = await CharterInterviewer(model, "alpha").respond("answer")
    assert turn.draft is not None and turn.draft.product == "alpha"


async def test_interviewer_forces_finalize_at_max_turns():
    model = DeterministicModelClient()

    def handler(system: str, prompt: str):
        if "MUST finalize now" in prompt:
            return InterviewerTurn(message="forced", done=True, draft=_draft())
        return InterviewerTurn(message="another question?", done=False)

    model.register("[charter-interview]", handler)
    iv = CharterInterviewer(model, "alpha", max_turns=1)
    turn = await iv.respond("only answer")
    assert turn.done and turn.draft is not None


async def test_interviewer_raises_if_model_will_not_finalize():
    model = DeterministicModelClient()
    model.register("[charter-interview]", lambda s, p: InterviewerTurn(message="more?", done=False))
    iv = CharterInterviewer(model, "alpha", max_turns=1)
    with pytest.raises(CharterInterviewError):
        await iv.respond("answer")
