from __future__ import annotations

from dsf.contracts.charter import Charter, StoredCharter
from dsf.contracts.enums import CharterStatus
from dsf.orchestrator.stations import s3_synthesis
from dsf_testing import build_test_services, make_evidence, make_run
from dsf_testing.charter import InMemoryCharterStore

UNCHARTED_TAG = "uncharted product context"


def _charter() -> Charter:
    return Charter(
        product="alpha", vision="V", target_users="U", goals=["g"], success_metrics=["m"]
    )


async def test_s3_tags_uncharted_proposals():
    services = build_test_services(product="alpha")  # uncharted
    run = make_run([make_evidence("latency spike", product="alpha")])

    await s3_synthesis.run(run, services)

    proposals = await s3_synthesis.load_proposals(run.id, services)
    assert proposals
    assert all(UNCHARTED_TAG in p.context_tags for p in proposals if p.product is not None)


async def test_s3_does_not_tag_when_charter_present():
    store = InMemoryCharterStore()
    await store.put_charter(
        StoredCharter(product="alpha", charter=_charter(), status=CharterStatus.OK)
    )
    services = build_test_services(product="alpha", charter=store)
    run = make_run([make_evidence("latency spike", product="alpha")])

    await s3_synthesis.run(run, services)

    proposals = await s3_synthesis.load_proposals(run.id, services)
    assert proposals
    assert all(UNCHARTED_TAG not in p.context_tags for p in proposals)
