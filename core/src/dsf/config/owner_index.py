"""Owner-level App Configuration index of per-product runtime env.

``dsf new`` publishes a product's runtime env here under App Configuration
*label = <product>*; the runtime resolves it from just ``--product`` plus the
``DSF_OWNER_APPCONFIG_ENDPOINT`` pointer. Values are plain strings (raw env
values), NOT JSON-encoded flags. Secrets never live here -- only endpoints and
non-secret pointers (e.g. the private-key *secret name*, never the PEM itself).
"""

from __future__ import annotations

import os
from collections.abc import Mapping

from dsf.config.azure_store import ConfigGateway, _SdkConfigGateway

OWNER_APPCONFIG_ENV = "DSF_OWNER_APPCONFIG_ENDPOINT"


def _gateway(endpoint: str, gateway: ConfigGateway | None) -> ConfigGateway:
    """Use the injected gateway (tests) or build the real SDK one (production)."""
    return gateway if gateway is not None else _SdkConfigGateway(endpoint)


def publish_runtime_config(
    endpoint: str,
    product: str,
    values: Mapping[str, str],
    *,
    gateway: ConfigGateway | None = None,
) -> None:
    """Write every ``values`` entry to the index under label = ``product``."""
    gw = _gateway(endpoint, gateway)
    for key, value in values.items():
        gw.set(key, value, product)


def read_runtime_config(
    endpoint: str,
    product: str,
    *,
    gateway: ConfigGateway | None = None,
) -> dict[str, str]:
    """Return the index entries stored under label = ``product``."""
    gw = _gateway(endpoint, gateway)
    return {key: value for key, value, label in gw.list() if label == product}


def delete_runtime_config(
    endpoint: str,
    product: str,
    *,
    gateway: ConfigGateway | None = None,
) -> None:
    """Remove every index entry stored under label = ``product``."""
    gw = _gateway(endpoint, gateway)
    for key, _value, label in list(gw.list()):
        if label == product:
            gw.delete(key, product)


def runtime_env_for_product(
    product: str,
    *,
    owner_endpoint: str | None = None,
    base_env: Mapping[str, str] | None = None,
    gateway: ConfigGateway | None = None,
) -> dict[str, str]:
    """Resolve the full runtime env for ``product``.

    Precedence (low to high): index entries < ``base_env`` (os.environ) <
    forced ``DSF_PRODUCT = product``. When no owner endpoint is configured the
    index layer is empty, so the result is just ``base_env`` plus the product.
    """
    env = dict(base_env if base_env is not None else os.environ)
    endpoint = owner_endpoint if owner_endpoint is not None else env.get(OWNER_APPCONFIG_ENV, "")
    index: dict[str, str] = {}
    if endpoint:
        index = read_runtime_config(endpoint, product, gateway=gateway)
    return {**index, **env, "DSF_PRODUCT": product}


def list_products(
    owner_endpoint: str,
    *,
    gateway: ConfigGateway | None = None,
) -> list[str]:
    """Return the distinct product labels published in the owner index (sorted)."""
    if not owner_endpoint:
        return []
    gw = _gateway(owner_endpoint, gateway)
    return sorted({label for _key, _value, label in gw.list() if label})


def repo_for_product(
    owner_endpoint: str,
    product: str,
    *,
    gateway: ConfigGateway | None = None,
) -> str | None:
    """Resolve ``product``'s ``owner/name`` repo from the owner index (or ``None``)."""
    if not owner_endpoint:
        return None
    return read_runtime_config(owner_endpoint, product, gateway=gateway).get(
        "GITHUB_REPOSITORY"
    ) or None
