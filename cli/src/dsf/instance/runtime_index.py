"""Assemble the owner-index payload (one dict) for a single product.

The index carries endpoints (reusing ``runtime_endpoint_env`` so they never
drift from ``.env.orchestrator``), the WebIQ pointers, and the GitHub App
binding *pointers* (app id, installation id, private-key secret *name*). It
never carries secret material -- the runtime reads secrets from the product Key
Vault at ``build_services`` time.
"""

from __future__ import annotations

from dsf.instance.runtime_render import runtime_endpoint_env
from dsf.instance.spec import InstanceManifest

_STATIC = {"WEBIQ_PROVIDER": "webiq", "WEBIQ_API_KEY_SECRET": "webiq-api-key"}


def runtime_index_values(manifest: InstanceManifest) -> dict[str, str]:
    """Return the full ``{env_key: value}`` map to publish for this product."""
    outputs = manifest.azure.outputs if manifest.azure else {}
    values = dict(runtime_endpoint_env(outputs))
    values.update(_STATIC)
    values["DSF_PRODUCT"] = manifest.spec.product
    values["GITHUB_REPOSITORY"] = manifest.spec.github_repo()
    app = manifest.github_app
    if app is not None:
        values["GITHUB_APP_ID"] = app.app_id
        values["GITHUB_INSTALLATION_ID"] = app.installation_id
        values["GITHUB_APP_PRIVATE_KEY_SECRET"] = app.private_key_secret
    return values
