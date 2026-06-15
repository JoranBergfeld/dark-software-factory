"""Export JSON Schema for each top-level contract model.

Writes ``contracts/schemas/<Model>.json`` (relative to this package) for every
top-level blackboard model via pydantic's ``model_json_schema()``.
"""

from __future__ import annotations

import json
from pathlib import Path

from pydantic import BaseModel

from dsf.contracts.models import (
    AuditRecord,
    CouncilVerdict,
    CriticScore,
    EvidenceItem,
    Proposal,
    Provenance,
    RoutedIssue,
    Run,
)

# Top-level models that get their own schema file.
TOP_LEVEL_MODELS: list[type[BaseModel]] = [
    Provenance,
    EvidenceItem,
    AuditRecord,
    Run,
    Proposal,
    CriticScore,
    CouncilVerdict,
    RoutedIssue,
]


def schemas_dir() -> Path:
    """Directory where generated schemas are written."""
    return Path(__file__).parent / "schemas"


def export_schemas(out_dir: Path | None = None) -> list[Path]:
    """Write one JSON Schema file per top-level model. Returns written paths."""
    target = out_dir or schemas_dir()
    target.mkdir(parents=True, exist_ok=True)
    written: list[Path] = []
    for model in TOP_LEVEL_MODELS:
        path = target / f"{model.__name__}.json"
        schema = model.model_json_schema()
        path.write_text(json.dumps(schema, indent=2, sort_keys=True), encoding="utf-8")
        written.append(path)
    return written


def main() -> None:
    """CLI entrypoint: export schemas and print where they went."""
    written = export_schemas()
    for path in written:
        print(f"wrote {path}")


if __name__ == "__main__":
    main()
