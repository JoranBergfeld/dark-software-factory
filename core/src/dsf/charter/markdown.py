"""Deterministic markdown <-> :class:`Charter` parser/renderer.

The on-disk format is a fixed-heading markdown document prefixed with a
``<!-- dsf:charter schema_version=1 -->`` marker. Parsing is strict and
collects *all* diagnostics before raising :class:`CharterParseError`, so a
human editing `.dsf/charter.md` sees every problem at once.
"""

from __future__ import annotations

import hashlib
import re

from dsf.contracts.charter import Charter

_MARKER_RE = re.compile(r"<!--\s*dsf:charter\s+schema_version=(\d+)\s*-->")

#: Every required ``## `` heading, in render order.
_HEADINGS: tuple[str, ...] = (
    "Vision",
    "Target Users",
    "Goals",
    "Non-Goals",
    "Success Metrics",
    "Constraints",
    "Glossary",
)


class CharterParseError(ValueError):
    """Raised when `.dsf/charter.md` is malformed. Carries all diagnostics."""

    def __init__(self, diagnostics: list[str]) -> None:
        self.diagnostics = list(diagnostics)
        super().__init__("; ".join(self.diagnostics))


def render_charter(charter: Charter) -> str:
    """Render a :class:`Charter` to canonical markdown (round-trips with parse)."""

    def bullets(items: list[str]) -> str:
        return "\n".join(f"- {item}" for item in items)

    glossary = "\n".join(f"- {k}: {v}" for k, v in charter.glossary.items())
    sections = [
        f"<!-- dsf:charter schema_version={charter.schema_version} -->",
        f"# Product Charter: {charter.product}",
        f"## Vision\n{charter.vision}".rstrip(),
        f"## Target Users\n{charter.target_users}".rstrip(),
        f"## Goals\n{bullets(charter.goals)}".rstrip(),
        f"## Non-Goals\n{bullets(charter.non_goals)}".rstrip(),
        f"## Success Metrics\n{bullets(charter.success_metrics)}".rstrip(),
        f"## Constraints\n{charter.constraints}".rstrip(),
        f"## Glossary\n{glossary}".rstrip(),
    ]
    return "\n\n".join(sections) + "\n"


def _split_sections(lines: list[str]) -> tuple[dict[str, list[str]], list[str]]:
    """Return ``{heading: body_lines}`` and the heading order (for dup checks)."""
    sections: dict[str, list[str]] = {}
    order: list[str] = []
    current: str | None = None
    for line in lines:
        if line.startswith("## "):
            current = line[3:].strip()
            order.append(current)
            sections.setdefault(current, [])
        elif current is not None:
            sections[current].append(line)
    return sections, order


def parse_charter(text: str, *, product: str) -> Charter:
    """Parse canonical charter markdown into a :class:`Charter` for ``product``.

    Raises :class:`CharterParseError` listing every problem found.
    """
    diagnostics: list[str] = []
    lines = text.splitlines()

    for line in lines:
        stripped = line.strip()
        if (
            stripped.startswith("<<<<<<<")
            or stripped.startswith(">>>>>>>")
            or stripped == "======="
        ):
            diagnostics.append("merge conflict markers present in charter")
            break

    marker = _MARKER_RE.search(text)
    if marker is None:
        diagnostics.append("missing or malformed '<!-- dsf:charter schema_version=N -->' marker")
    elif marker.group(1) != "1":
        diagnostics.append(f"unsupported schema_version {marker.group(1)} (expected 1)")

    sections, order = _split_sections(lines)
    known_headings = set(_HEADINGS)
    for heading in order:
        if heading not in known_headings:
            diagnostics.append(f"unknown section '## {heading}'")

    for heading in _HEADINGS:
        count = order.count(heading)
        if count == 0:
            diagnostics.append(f"missing required section '## {heading}'")
        elif count > 1:
            diagnostics.append(f"duplicate section '## {heading}'")

    def prose(name: str) -> str:
        return "\n".join(sections.get(name, [])).strip()

    def items(name: str) -> list[str]:
        out: list[str] = []
        for raw in sections.get(name, []):
            entry = raw.strip()
            if not entry:
                continue
            if not entry.startswith("- "):
                diagnostics.append(
                    f"malformed list item in '## {name}': {entry!r} (must start with '- ')"
                )
                continue
            out.append(entry[2:].strip())
        return out

    vision = prose("Vision")
    target_users = prose("Target Users")
    constraints = prose("Constraints")
    goals = items("Goals")
    non_goals = items("Non-Goals")
    success_metrics = items("Success Metrics")

    glossary: dict[str, str] = {}
    for raw in sections.get("Glossary", []):
        entry = raw.strip()
        if not entry:
            continue
        if not entry.startswith("- ") or ": " not in entry:
            diagnostics.append(
                f"malformed glossary entry: {entry!r} (must be '- term: definition')"
            )
            continue
        key, value = entry[2:].split(": ", 1)
        glossary[key.strip()] = value.strip()

    if not vision:
        diagnostics.append("Vision must not be empty")
    if not target_users:
        diagnostics.append("Target Users must not be empty")
    if not goals:
        diagnostics.append("at least one Goal is required")
    if not success_metrics:
        diagnostics.append("at least one Success Metric is required")

    if diagnostics:
        raise CharterParseError(diagnostics)

    return Charter(
        product=product,
        vision=vision,
        target_users=target_users,
        goals=goals,
        non_goals=non_goals,
        success_metrics=success_metrics,
        constraints=constraints,
        glossary=glossary,
    )


def git_blob_sha(data: bytes) -> str:
    """Compute the git blob SHA-1 of ``data`` (matches GitHub's blob ``sha``)."""
    header = b"blob " + str(len(data)).encode() + b"\0"
    return hashlib.sha1(header + data).hexdigest()  # noqa: S324 (git blob id, not security)


__all__ = ["CharterParseError", "git_blob_sha", "parse_charter", "render_charter"]
