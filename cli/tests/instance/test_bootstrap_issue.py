from __future__ import annotations

from dsf.contracts.charter import Charter
from dsf.instance.bootstrap_issue import render_bootstrap_issue


def _charter() -> Charter:
    return Charter(
        product="alpha",
        vision="Make billing painless.",
        target_users="SMB owners.",
        goals=["Cut invoice time"],
        success_metrics=["p50 < 60s"],
        constraints="EU only.",
        glossary={"Invoice": "a billable document"},
        source_sha="sha",
        source_ref="main",
    )


def test_title_names_the_product():
    title, _ = render_bootstrap_issue(_charter())
    assert "alpha" in title


def test_body_walks_the_speckit_lifecycle_and_points_at_docs():
    _, body = render_bootstrap_issue(_charter())
    for needle in (
        "/speckit.specify",
        "/speckit.plan",
        "/speckit.tasks",
        ".specify/memory/constitution.md",
        ".dsf/charter.md",
    ):
        assert needle in body


def test_body_embeds_charter_as_untrusted_data():
    _, body = render_bootstrap_issue(_charter())
    assert '<product_charter trust="UNTRUSTED">' in body
    assert "Make billing painless." in body  # vision rendered inside the envelope


def test_body_requests_a_large_model():
    _, body = render_bootstrap_issue(_charter())
    assert "Opus 4.8" in body
