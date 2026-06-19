"""Tests for the squad maturity-dial governance commands."""

from __future__ import annotations

from dsf.instance.spec import InstanceSpec
from dsf.instance.squad_governance import governance_commands


def _spec(maturity: str) -> InstanceSpec:
    return InstanceSpec(product="demo", owner="acme", squad_maturity=maturity)


def test_low_maturity_disables_auto_merge():
    cmds = governance_commands(_spec("low"))
    assert cmds == [
        ["gh", "api", "--method", "PATCH", "repos/acme/demo", "-F", "allow_auto_merge=false"]
    ]


def test_high_maturity_enables_auto_merge():
    cmds = governance_commands(_spec("high"))
    assert cmds == [
        ["gh", "api", "--method", "PATCH", "repos/acme/demo", "-F", "allow_auto_merge=true"]
    ]
