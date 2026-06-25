"""Tests for the branch-protection ruleset builders."""

from __future__ import annotations

from dsf.instance.branch_protection import (
    CI_WORKFLOW_PATH,
    RULESET_NAME,
    auto_merge_command,
    baseline_ci_workflow,
    is_unsupported_ruleset_error,
    ruleset_payload,
)
from dsf.instance.spec import InstanceSpec


def _spec(maturity: str) -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme", creation_maturity=maturity)


def _rule(payload: dict, rule_type: str) -> dict:
    return next(r for r in payload["rules"] if r["type"] == rule_type)


def test_ruleset_targets_default_branch_and_requires_ci():
    payload = ruleset_payload(_spec("low"))
    assert payload["name"] == RULESET_NAME
    assert payload["target"] == "branch"
    assert payload["enforcement"] == "active"
    assert payload["conditions"]["ref_name"]["include"] == ["~DEFAULT_BRANCH"]
    checks = _rule(payload, "required_status_checks")["parameters"]["required_status_checks"]
    assert checks == [{"context": "ci"}]


def test_low_requires_one_review():
    params = _rule(ruleset_payload(_spec("low")), "pull_request")["parameters"]
    assert params["required_approving_review_count"] == 1


def test_high_requires_zero_reviews():
    params = _rule(ruleset_payload(_spec("high")), "pull_request")["parameters"]
    assert params["required_approving_review_count"] == 0


def test_auto_merge_command_enabled_only_for_high():
    assert auto_merge_command(_spec("low")) == [
        "gh", "api", "--method", "PATCH", "repos/acme/demo",
        "-F", "allow_auto_merge=false",
    ]
    assert auto_merge_command(_spec("high")) == [
        "gh", "api", "--method", "PATCH", "repos/acme/demo",
        "-F", "allow_auto_merge=true",
    ]


def test_is_unsupported_ruleset_error_matches_pro_upgrade_403():
    stderr = (
        "gh: Upgrade to GitHub Pro or make this repository public to enable "
        "this feature. (HTTP 403)"
    )
    assert is_unsupported_ruleset_error(stderr) is True


def test_is_unsupported_ruleset_error_ignores_other_failures():
    assert is_unsupported_ruleset_error("HTTP 404: Not Found") is False
    assert is_unsupported_ruleset_error("gh: Bad credentials (HTTP 401)") is False
    assert is_unsupported_ruleset_error("") is False


def test_baseline_ci_workflow_defines_a_ci_job():
    wf = baseline_ci_workflow()
    # The job id is the status-check context the ruleset requires.
    assert "\n  ci:\n" in wf
    assert "runs-on: ubuntu-latest" in wf
    assert wf.startswith("name: ci\n")
    assert wf.endswith("\n")


def test_ci_workflow_path_is_under_github_workflows():
    assert CI_WORKFLOW_PATH == ".github/workflows/ci.yml"
