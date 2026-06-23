from __future__ import annotations

from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf.council.decision import decide
from dsf.council.scope import ScopeJudgment, annotate_scope
from dsf_testing import build_test_services, make_evidence, make_proposal, make_run
from dsf_testing.charter import InMemoryCharterStore


def _charter_with_non_goals() -> Charter:
    return Charter(
        product="alpha",
        vision="V",
        target_users="U",
        goals=["g"],
        success_metrics=["m"],
        non_goals=["We will not build a native mobile app."],
    )


async def test_no_charter_is_in_scope():
    services = build_test_services()
    run = make_run([make_evidence("x")])
    prop = make_proposal(run, product="alpha")
    note = await annotate_scope(prop, None, services)
    assert note.in_scope and note.note == ""
    assert not any("[scope]" in p for (_s, p) in services.model.calls)  # no model call


async def test_no_non_goals_is_in_scope():
    services = build_test_services()
    run = make_run([make_evidence("x")])
    prop = make_proposal(run, product="alpha")
    charter = Charter(
        product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"]
    )
    note = await annotate_scope(prop, charter, services)
    assert note.in_scope


async def test_unstructured_model_output_is_in_scope():
    # The default double returns an echo string (not a ScopeJudgment) -> safe in-scope.
    services = build_test_services()
    run = make_run([make_evidence("x")])
    prop = make_proposal(run, product="alpha")
    note = await annotate_scope(prop, _charter_with_non_goals(), services)
    assert note.in_scope


async def test_scope_injects_untrusted_envelope():
    services = build_test_services()
    run = make_run([make_evidence("x")])
    prop = make_proposal(run, product="alpha")
    await annotate_scope(prop, _charter_with_non_goals(), services)
    scope_prompts = [p for (_s, p) in services.model.calls if "[scope]" in p]
    assert scope_prompts and all('<product_charter trust="UNTRUSTED">' in p for p in scope_prompts)


async def test_model_flags_non_goal_conflict():
    services = build_test_services()
    services.model.register(
        "[scope]",
        lambda system, prompt: ScopeJudgment(
            in_scope=False,
            conflicting_non_goal="native mobile app",
            rationale="builds a mobile app",
        ),
    )
    run = make_run([make_evidence("x")])
    prop = make_proposal(run, product="alpha", proposed_change="Build a native mobile app.")
    note = await annotate_scope(prop, _charter_with_non_goals(), services)
    assert not note.in_scope and "native mobile app" in note.note


async def test_decide_appends_scope_annotation_when_conflicting():
    store = InMemoryCharterStore()
    await store.put_charter(
        StoredCharter(product="alpha", charter=_charter_with_non_goals(), status=CharterStatus.OK)
    )
    services = build_test_services(product="alpha", charter=store)
    services.model.register(
        "[scope]",
        lambda system, prompt: ScopeJudgment(
            in_scope=False, conflicting_non_goal="native mobile app"
        ),
    )
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, product="alpha")

    verdict = await decide(prop, run, services)

    assert "scope:" in verdict.rationale and "advisory" in verdict.rationale
    assert any(r.station == "council:scope" for r in run.audit)


async def test_decide_without_charter_adds_no_scope_line():
    services = build_test_services(product="alpha")  # uncharted
    run = make_run([make_evidence("CRITICAL outage", confidence=0.9)])
    prop = make_proposal(run, product="alpha")
    verdict = await decide(prop, run, services)
    assert "scope:" not in verdict.rationale
    assert not any(r.station == "council:scope" for r in run.audit)
