"""Open-handoff-issue count: the KEDA scale metric source for the Ralph loop.

The exporter shells ``gh issue list --label squad:ready --state open --json
number`` and feeds the JSON here. KEDA scales the Ralph Deployment to 1 while the
count is >= 1 and back to 0 when it returns to 0. Parsing is total: any malformed
or unexpected input counts as zero work, so a transient ``gh`` hiccup scales the
loop down rather than wedging it up.
"""

from __future__ import annotations

import json


def open_handoff_issue_count(issues_json: str) -> int:
    """Return the number of open handoff issues in a ``gh issue list --json`` array.

    Returns 0 for blank, malformed, or non-array input.
    """
    if not issues_json or not issues_json.strip():
        return 0
    try:
        parsed = json.loads(issues_json)
    except json.JSONDecodeError:
        return 0
    if not isinstance(parsed, list):
        return 0
    return len(parsed)
