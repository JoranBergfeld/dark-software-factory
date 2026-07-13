"""Tests for listing provisioned product factories from the owner index."""

from __future__ import annotations

import json

from dsf.cli.factory import _cmd_list, build_parser
from dsf_testing.azure_doubles import InMemoryConfigGateway


def _gateway() -> InMemoryConfigGateway:
    return InMemoryConfigGateway(
        {
            ("GITHUB_REPOSITORY", "pets"): "acme/pets",
            ("AZURE_APPCONFIG_ENDPOINT", "pets"): "https://pets.azconfig.io",
            ("AZURE_COSMOS_ENDPOINT", "pets"): "https://pets.cosmos.azure.com",
            ("AZURE_OPENAI_ENDPOINT", "pets"): "https://pets.openai.azure.com",
            ("GITHUB_REPOSITORY", "toys"): "acme/toys",
            ("AZURE_APPCONFIG_ENDPOINT", "toys"): "https://toys.azconfig.io",
            ("AZURE_COSMOS_ENDPOINT", "toys"): "https://toys.cosmos.azure.com",
            ("AZURE_OPENAI_ENDPOINT", "toys"): "https://toys.openai.azure.com",
        }
    )


def test_list_parser_wiring():
    args = build_parser().parse_args(["list"])

    assert args.command == "list"
    assert args.json is False

    alias_args = build_parser().parse_args(["ls"])
    assert alias_args.func is _cmd_list


def test_list_prints_populated_table(capsys):
    args = build_parser().parse_args(
        ["list", "--owner-appconfig-endpoint", "https://owner.azconfig.io"]
    )

    rc = _cmd_list(args, gateway=_gateway())

    out = capsys.readouterr().out
    assert rc == 0
    for expected in (
        "pets",
        "acme/pets",
        "https://pets.azconfig.io",
        "https://pets.cosmos.azure.com",
        "https://pets.openai.azure.com",
        "toys",
        "acme/toys",
        "https://toys.azconfig.io",
        "https://toys.cosmos.azure.com",
        "https://toys.openai.azure.com",
    ):
        assert expected in out


def test_list_json_emits_full_index_rows(capsys):
    args = build_parser().parse_args(
        ["list", "--json", "--owner-appconfig-endpoint", "https://owner.azconfig.io"]
    )

    rc = _cmd_list(args, gateway=_gateway())

    rows = json.loads(capsys.readouterr().out)
    assert rc == 0
    assert len(rows) == 2
    pets = next(row for row in rows if row["product"] == "pets")
    assert pets["GITHUB_REPOSITORY"] == "acme/pets"
    assert pets["AZURE_COSMOS_ENDPOINT"] == "https://pets.cosmos.azure.com"


def test_list_table_renders_missing_column_as_dash(capsys):
    gateway = InMemoryConfigGateway(
        {
            ("GITHUB_REPOSITORY", "pets"): "acme/pets",
            ("AZURE_APPCONFIG_ENDPOINT", "pets"): "https://pets.azconfig.io",
            ("AZURE_OPENAI_ENDPOINT", "pets"): "https://pets.openai.azure.com",
        }
    )
    args = build_parser().parse_args(
        ["list", "--owner-appconfig-endpoint", "https://owner.azconfig.io"]
    )

    rc = _cmd_list(args, gateway=gateway)

    out = capsys.readouterr().out
    product_line = next(line for line in out.splitlines() if line.startswith("pets"))
    assert rc == 0
    assert "-" in product_line.split()


def test_list_without_endpoint_prints_hint(monkeypatch, capsys):
    monkeypatch.delenv("DSF_OWNER_APPCONFIG_ENDPOINT", raising=False)
    args = build_parser().parse_args(["list"])

    rc = _cmd_list(args, gateway=InMemoryConfigGateway())

    out = capsys.readouterr().out
    assert rc == 0
    assert "[dsf]" in out
    assert "DSF_OWNER_APPCONFIG_ENDPOINT" in out


def test_list_json_without_endpoint_prints_empty_array(monkeypatch, capsys):
    monkeypatch.delenv("DSF_OWNER_APPCONFIG_ENDPOINT", raising=False)
    args = build_parser().parse_args(["list", "--json"])

    rc = _cmd_list(args, gateway=InMemoryConfigGateway())

    assert rc == 0
    assert capsys.readouterr().out.strip() == "[]"


def test_list_json_with_endpoint_and_zero_products_prints_empty_array(capsys):
    args = build_parser().parse_args(
        ["list", "--json", "--owner-appconfig-endpoint", "https://owner.azconfig.io"]
    )

    rc = _cmd_list(args, gateway=InMemoryConfigGateway())

    assert rc == 0
    assert capsys.readouterr().out.strip() == "[]"
