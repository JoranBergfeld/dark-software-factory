"""Tests for signal debounce (plan Task 5.1)."""

from __future__ import annotations

import time

from dsf.triggers.debounce import (
    DEFAULT_DEBOUNCE_TTL,
    record_signal,
    should_suppress,
    signal_text,
)
from dsf_testing import build_test_services


async def test_first_signal_not_suppressed_then_duplicate_suppressed() -> None:
    services = build_test_services()
    payload = {
        "fingerprint": "checkout-typeerror-id-spike",
        "text": "error spike in checkout flow",
        "product_hints": ["microbi"],
    }

    # First sighting: nothing in the window yet -> not suppressed.
    assert await should_suppress(payload, services) is False

    # Record it (as S1 triage does once it accepts the signal).
    await record_signal(payload, services)

    # A repeat of the same signal -> suppressed.
    assert await should_suppress(payload, services) is True


async def test_distinct_signal_not_suppressed() -> None:
    services = build_test_services()
    first = {"fingerprint": "checkout-typeerror-id-spike", "text": "checkout error spike"}
    await record_signal(first, services)

    other = {"fingerprint": "billing-timeout", "text": "billing webhook timeouts climbing"}
    assert await should_suppress(other, services) is False


def test_signal_text_prefers_fingerprint() -> None:
    assert signal_text({"fingerprint": "abc", "text": "xyz"}) == "abc"
    assert signal_text({"text": "xyz"}) == "xyz"
    assert signal_text({"product_hints": ["microbi"]}) == "signal for microbi"


async def test_debounce_ttl_expiry_allows_signal_again() -> None:
    """After the TTL window closes the same signal must be accepted again."""
    services = build_test_services()
    payload = {"text": "checkout regression spike"}

    # Record with a very short TTL.
    await record_signal(payload, services, ttl=0.01)
    assert await should_suppress(payload, services) is True

    # After expiry the signal is no longer suppressed.
    time.sleep(0.02)
    assert await should_suppress(payload, services) is False


def test_default_debounce_ttl_is_positive() -> None:
    assert DEFAULT_DEBOUNCE_TTL > 0
