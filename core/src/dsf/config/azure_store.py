"""Azure App Configuration-backed ConfigStore (real ``azure`` mode adapter).

Talks to a narrow :class:`ConfigGateway` (get/set/list of JSON-string values
keyed by ``(key, label)``). The default gateway wraps ``azure-appconfiguration``
and is built lazily, so importing this module never requires the SDK. Inject an
in-memory gateway in tests to stay offline.
"""

from __future__ import annotations

import json
from typing import Any, Protocol

from dsf.config.store import resolve_flag_key


class ConfigGateway(Protocol):
    """Narrow seam over App Configuration: string values keyed by (key, label)."""

    def get(self, key: str, label: str | None) -> str | None: ...
    def set(self, key: str, value: str, label: str | None) -> None: ...
    def list(self) -> list[tuple[str, str, str | None]]: ...
    def delete(self, key: str, label: str | None) -> None: ...


class AppConfigStore:
    """:class:`~dsf.ports.ConfigStore` backed by Azure App Configuration.

    Per-product overrides use App Configuration *labels* (label = product); a
    labelled setting takes precedence over the unlabelled default.
    """

    def __init__(self, gateway: ConfigGateway) -> None:
        self._gw = gateway

    @classmethod
    def from_endpoint(cls, endpoint: str) -> AppConfigStore:
        """Build a store backed by the real App Configuration SDK gateway."""
        return cls(_SdkConfigGateway(endpoint))

    def is_enabled(self, flag: str, product: str | None = None) -> bool:
        key = resolve_flag_key(flag)
        if key is None:
            return False
        raw = self._gw.get(key, product) if product is not None else None
        if raw is None:
            raw = self._gw.get(key, None)
        return bool(json.loads(raw)) if raw is not None else False

    def get_value(self, key: str, default: Any = None) -> Any:
        raw = self._gw.get(key, None)
        return default if raw is None else json.loads(raw)

    def set_flag(self, flag: str, value: bool, product: str | None = None) -> None:
        key = resolve_flag_key(flag)
        if key is None:
            raise ValueError(f"unknown flag {flag!r}")
        self._gw.set(key, json.dumps(bool(value)), product)

    def snapshot(self) -> dict:
        snap: dict[str, Any] = {}
        overrides: dict[str, Any] = {}
        for key, value, label in self._gw.list():
            try:
                parsed: Any = json.loads(value)
            except (ValueError, TypeError):
                parsed = value
            if label is None:
                _assign_dotted(snap, key, parsed)
            else:
                overrides[f"{key}@{label}"] = parsed
        snap["_overrides"] = overrides
        return snap


def _assign_dotted(root: dict, dotted: str, value: Any) -> None:
    """Assign ``value`` into ``root`` along a dotted ``a.b.c`` path."""
    parts = dotted.split(".")
    node = root
    for part in parts[:-1]:
        node = node.setdefault(part, {})
    node[parts[-1]] = value


class _SdkConfigGateway:
    """Real gateway wrapping ``azure-appconfiguration`` (lazy import)."""

    def __init__(self, endpoint: str) -> None:
        self._endpoint = endpoint
        self._client: Any = None

    def _client_or_build(self) -> Any:
        if self._client is None:
            try:
                from azure.appconfiguration import AzureAppConfigurationClient
                from azure.identity import DefaultAzureCredential
            except ImportError as exc:  # pragma: no cover - requires azure extra
                raise RuntimeError(
                    "azure extra not installed; run: uv pip install -e '.[azure]'"
                ) from exc
            self._client = AzureAppConfigurationClient(
                base_url=self._endpoint, credential=DefaultAzureCredential()
            )
        return self._client

    def get(self, key: str, label: str | None) -> str | None:  # pragma: no cover
        client = self._client_or_build()
        try:
            setting = client.get_configuration_setting(key=key, label=label)
        except Exception as exc:
            if type(exc).__name__ == "ResourceNotFoundError":
                return None
            raise
        return setting.value

    def set(self, key: str, value: str, label: str | None) -> None:  # pragma: no cover
        from azure.appconfiguration import ConfigurationSetting

        client = self._client_or_build()
        client.set_configuration_setting(
            ConfigurationSetting(key=key, value=value, label=label)
        )

    def list(self) -> list[tuple[str, str, str | None]]:  # pragma: no cover
        client = self._client_or_build()
        return [(s.key, s.value, s.label) for s in client.list_configuration_settings()]

    def delete(self, key: str, label: str | None) -> None:  # pragma: no cover
        client = self._client_or_build()
        client.delete_configuration_setting(key=key, label=label)
