"""Tests for signal debounce (plan Task 5.1)."""

from __future__ import annotations

from dsf.container import build_services
from dsf.triggers.debounce import record_signal, should_suppress, signal_text


async def test_first_signal_not_suppressed_then_duplicate_suppressed() -> None:
    services = build_services("local")
    payload = {
        "fingerprint": "checkout-typeerror-id-spike",
        "text": "error spike in checkout flow",
        "product_hints": ["microbi"],
    }

    # First sighting: nothing in the window yet -> not suppressed.
    assert await should_suppress(payload, services) is False

    # Record it (as the /ingest handler would once it accepts the signal).
    await record_signal(payload, services)

    # A repeat of the same signal -> suppressed.
    assert await should_suppress(payload, services) is True


async def test_distinct_signal_not_suppressed() -> None:
    services = build_services("local")
    first = {"fingerprint": "checkout-typeerror-id-spike", "text": "checkout error spike"}
    await record_signal(first, services)

    other = {"fingerprint": "billing-timeout", "text": "billing webhook timeouts climbing"}
    assert await should_suppress(other, services) is False


def test_signal_text_prefers_fingerprint() -> None:
    assert signal_text({"fingerprint": "abc", "text": "xyz"}) == "abc"
    assert signal_text({"text": "xyz"}) == "xyz"
    assert signal_text({"product_hints": ["microbi"]}) == "signal for microbi"
