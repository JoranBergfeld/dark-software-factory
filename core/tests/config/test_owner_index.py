"""Unit tests for the owner-level runtime-config index (offline gateway)."""

from __future__ import annotations

from dsf.config.owner_index import (
    OWNER_APPCONFIG_ENV,
    delete_runtime_config,
    publish_runtime_config,
    read_runtime_config,
    runtime_env_for_product,
)
from dsf_testing.azure_doubles import InMemoryConfigGateway

ENDPOINT = "https://owner-index.azconfig.io"


def test_publish_then_read_round_trips_only_that_product():
    gw = InMemoryConfigGateway()
    publish_runtime_config(ENDPOINT, "pets", {"A": "1", "B": "2"}, gateway=gw)
    publish_runtime_config(ENDPOINT, "cars", {"A": "9"}, gateway=gw)

    assert read_runtime_config(ENDPOINT, "pets", gateway=gw) == {"A": "1", "B": "2"}
    assert read_runtime_config(ENDPOINT, "cars", gateway=gw) == {"A": "9"}


def test_delete_removes_only_that_products_entries():
    gw = InMemoryConfigGateway()
    publish_runtime_config(ENDPOINT, "pets", {"A": "1", "B": "2"}, gateway=gw)
    publish_runtime_config(ENDPOINT, "cars", {"A": "9"}, gateway=gw)

    delete_runtime_config(ENDPOINT, "pets", gateway=gw)

    assert read_runtime_config(ENDPOINT, "pets", gateway=gw) == {}
    assert read_runtime_config(ENDPOINT, "cars", gateway=gw) == {"A": "9"}


def test_runtime_env_layers_index_under_os_env_and_forces_product():
    gw = InMemoryConfigGateway()
    publish_runtime_config(
        ENDPOINT,
        "pets",
        {
            "AZURE_OPENAI_ENDPOINT": "from-index",
            "AZURE_SEARCH_ENDPOINT": "from-index-only",
            "DSF_PRODUCT": "WRONG",
        },
        gateway=gw,
    )
    base_env = {"AZURE_OPENAI_ENDPOINT": "from-os", "EXTRA": "kept"}

    env = runtime_env_for_product(
        "pets", owner_endpoint=ENDPOINT, base_env=base_env, gateway=gw
    )

    # index supplies keys absent from the environment...
    assert env["AZURE_SEARCH_ENDPOINT"] == "from-index-only"
    # ...os.environ wins over the index where both define a key...
    assert env["AZURE_OPENAI_ENDPOINT"] == "from-os"
    assert env["EXTRA"] == "kept"
    # ...and DSF_PRODUCT is always forced to the argument.
    assert env["DSF_PRODUCT"] == "pets"


def test_runtime_env_resolves_endpoint_from_base_env_pointer():
    gw = InMemoryConfigGateway()
    publish_runtime_config(ENDPOINT, "pets", {"AZURE_SEARCH_ENDPOINT": "from-index"}, gateway=gw)
    base_env = {OWNER_APPCONFIG_ENV: ENDPOINT}

    env = runtime_env_for_product("pets", base_env=base_env, gateway=gw)

    assert env["AZURE_SEARCH_ENDPOINT"] == "from-index"
    assert env["DSF_PRODUCT"] == "pets"


def test_runtime_env_without_endpoint_is_base_env_plus_product():
    env = runtime_env_for_product("pets", owner_endpoint="", base_env={"X": "y"})
    assert env == {"X": "y", "DSF_PRODUCT": "pets"}


def test_owner_appconfig_env_name_is_stable():
    assert OWNER_APPCONFIG_ENV == "DSF_OWNER_APPCONFIG_ENDPOINT"
