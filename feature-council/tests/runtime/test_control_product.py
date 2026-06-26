"""`--product` makes the runtime CLI resolve env from the owner index."""

from __future__ import annotations

import dsf.runtime.control as control


def test_get_services_passes_resolved_env_when_product_given(monkeypatch):
    captured: dict[str, object] = {}

    def fake_build_services(*, env=None):
        captured["env"] = env
        return "services"

    monkeypatch.setattr(control, "build_services", fake_build_services)
    monkeypatch.setattr(
        control,
        "runtime_env_for_product",
        lambda product: {"DSF_PRODUCT": product, "AZURE_OPENAI_ENDPOINT": "x"},
    )

    args = control.build_parser().parse_args(["sweep", "--product", "pets"])
    services = control._get_services(args)

    assert services == "services"
    assert captured["env"] == {"DSF_PRODUCT": "pets", "AZURE_OPENAI_ENDPOINT": "x"}


def test_get_services_uses_plain_env_without_product(monkeypatch):
    captured: dict[str, object] = {"called": False}

    def fake_build_services(*, env=None):
        captured["called"] = True
        captured["env"] = env
        return "services"

    monkeypatch.setattr(control, "build_services", fake_build_services)

    args = control.build_parser().parse_args(["sweep"])
    control._get_services(args)

    assert captured["called"] is True
    assert captured["env"] is None
