"""The Grafana dashboard JSON is valid and carries a non-empty panel list."""

from __future__ import annotations

import json
from pathlib import Path

import dsf.observability

DASHBOARD = (
    Path(dsf.observability.__file__).parent / "grafana" / "dashboard.json"
)


def test_dashboard_is_valid_json_with_panels() -> None:
    with DASHBOARD.open(encoding="utf-8") as fh:
        data = json.load(fh)

    assert isinstance(data, dict)
    panels = data.get("panels")
    assert isinstance(panels, list)
    assert len(panels) > 0
    # Every panel has a title and a type.
    for panel in panels:
        assert panel.get("title")
        assert panel.get("type")
