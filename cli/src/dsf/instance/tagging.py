"""Canonical Azure resource tags applied to every DSF-provisioned resource.

A single source of truth so that ``dsf new`` (the factory RG + both Bicep
deployments) and the teardown guard (:mod:`dsf.instance.teardown_common`) agree
on the tag set. Every resource **and** every resource group DSF creates carries:

- ``project=dark-software-factory``
- ``managed-by=dsf``
- ``product=<key>``
- ``component=<backing-services|sre-agent|...>``

The ``managed-by=dsf`` tag is the safety marker teardown checks before deleting a
resource group, so we never delete a foreign group that merely shares a name.
"""

from __future__ import annotations

#: Stable ``project`` tag value shared by every DSF instance.
PROJECT_TAG_VALUE = "dark-software-factory"

#: Tag key/value that marks a resource as DSF-managed. Teardown refuses to delete
#: a resource group that is not tagged ``managed-by=dsf``.
MANAGED_BY_TAG = "managed-by"
MANAGED_BY_VALUE = "dsf"


def canonical_tags(product: str, component: str) -> dict[str, str]:
    """Return the canonical tag set for one ``product`` / ``component``."""
    return {
        "project": PROJECT_TAG_VALUE,
        MANAGED_BY_TAG: MANAGED_BY_VALUE,
        "product": product,
        "component": component,
    }


def tag_cli_args(tags: dict[str, str]) -> list[str]:
    """Format a tag dict as ``key=value`` tokens for ``az ... --tags``."""
    return [f"{key}={value}" for key, value in tags.items()]