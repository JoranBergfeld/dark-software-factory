# SRE to Council Feedback Loop Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the SRE to Council feedback loop by feeding the managed Azure SRE Agent's incident issues and production telemetry back into the Feature Council as two new operational source agents (`incidents`, `azuremonitor`) that ride the existing S1 to S7 conveyor.

**Architecture:** Add two `SourceKind`s (`INCIDENTS`, `AZUREMONITOR`), each with a source agent following the `grafana` exemplar (fixture backend for offline + a live backend behind an injected client). The `incidents` backend pulls GitHub issues carrying a new `incident` label and aggregates recurrences into higher-confidence evidence; the `azuremonitor` backend pulls App Insights telemetry. Both register in the deployable-agent registry and the S2 agent-builder registry so the scheduled sweep gathers them automatically. Provisioning creates the `incident` label and the SRE runbook instructs the agent to stamp it. Recurrence intelligence lives in the incidents backend; the conveyor gains no new stage.

**Tech Stack:** Python 3.12, uv workspace, pydantic v2, FastAPI/Starlette (A2A apps), httpx (live clients + offline `MockTransport` tests), pytest (async), ruff.

---

## Conventions (read before starting)

- **Run everything through uv:** `uv run pytest ...`, `uv run ruff check .`. Never bare `python`.
- **Test dirs have NO `__init__.py`** (pytest importlib mode). Create test files directly under the shown directory.
- **Source packages DO have `__init__.py`** (see `feature-council/src/dsf/agents/grafana/__init__.py`).
- **Format gate is `ruff check` only** (NOT `ruff format`): multi-line lists are hand-formatted house style. Match the surrounding style.
- **No smart quotes / en-dashes / ellipsis characters** in any file. Only `->` in code and the literal `—` / `→` already used in prose are allowed. After edits, scan with:
  `grep -rnP "[\x{2013}\x{2018}\x{2019}\x{201C}\x{201D}\x{2026}]" <changed files>` and expect no output.
- **Commit trailer** on every commit:
  ```
  Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>
  ```
- **Async tests** need no decorator: the repo runs `pytest` with asyncio auto mode (see `feature-council/tests/agents/grafana/test_grafana.py` — `async def test_...` with no marker).
- **Branch:** all work lands on the existing branch `feat/sre-council-feedback-loop` (already checked out). Do NOT push to origin.
- **Full verification gauntlet** (run at the end, Task 9):
  ```
  uv run ruff check .
  uv run pytest -q
  ```

## File Structure

**New files:**
- `core/src/dsf/contracts/handoff.py` — extended with `INCIDENT_LABEL` constants (existing file).
- `feature-council/src/dsf/agents/incidents/{__init__.py,backend.py,client.py,main.py}` — the incidents source agent.
- `feature-council/src/dsf/agents/azuremonitor/{__init__.py,backend.py,client.py,main.py}` — the telemetry source agent.
- `tests/fixtures/incidents_evidence.json`, `tests/fixtures/azuremonitor_evidence.json` — offline fixtures.
- `feature-council/tests/agents/incidents/{test_incidents.py,test_incidents_live.py}` — incidents tests.
- `feature-council/tests/agents/azuremonitor/{test_azuremonitor.py,test_azuremonitor_live.py}` — telemetry tests.
- `feature-council/tests/e2e/test_operational_line.py` — offline e2e for an operational kind.
- `docs/adr/0013-sre-council-feedback-loop.md` — the new ADR.

**Modified files:**
- `core/src/dsf/contracts/enums.py` — add `INCIDENTS`, `AZUREMONITOR` to `SourceKind`.
- `config/defaults.json` — add `INCIDENTS`, `AZUREMONITOR` to the `agents` block.
- `feature-council/src/dsf/agents/registry.py` — add both to `DEPLOYABLE_AGENTS`.
- `feature-council/src/dsf/orchestrator/agent_registry.py` — add both to `AGENT_BUILDERS`.
- `core/src/dsf/config/registry.py` — add `azure_monitor_scope` to `Product`.
- `cli/src/dsf/instance/provisioner.py` — `create_labels` also creates the `incident` label.
- `cli/src/dsf/instance/runtime_render.py` — runbook stamps `incident`; `_product_from_spec` sets `azure_monitor_scope`.
- `feature-council/src/dsf/orchestrator/stations/s6_routing.py` — fix stale `squad triage` comment.
- `docs/adr/0009-leverage-azure-sre-agent.md` — fix two stale `squad triage --execute` references.
- `docs/phases/sre-agent.md`, `README.md`, `RUNBOOK.md` — un-defer the slow path (where present).

---

## Task 1: Incident label contract

**Files:**
- Modify: `core/src/dsf/contracts/handoff.py`
- Test: `core/tests/contracts/test_handoff.py` (create if absent)

- [ ] **Step 1: Create the handoff test file**

The `core/tests/contracts/` directory already exists (it holds `test_models.py`);
it has no `test_handoff.py`. Create `core/tests/contracts/test_handoff.py`:

```python
"""Tests for the well-known handoff/incident label contracts."""

from __future__ import annotations

from dsf.contracts.handoff import (
    HANDOFF_LABEL,
    INCIDENT_LABEL,
    INCIDENT_LABEL_COLOR,
    INCIDENT_LABEL_DESCRIPTION,
)


def test_incident_label_is_stable_marker():
    assert INCIDENT_LABEL == "incident"
    assert INCIDENT_LABEL != HANDOFF_LABEL


def test_incident_label_metadata_is_present():
    assert INCIDENT_LABEL_DESCRIPTION.strip()
    # 6-hex GitHub label color, no leading '#'.
    assert len(INCIDENT_LABEL_COLOR) == 6
    int(INCIDENT_LABEL_COLOR, 16)
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest core/tests/contracts/test_handoff.py -q`
Expected: FAIL with `ImportError: cannot import name 'INCIDENT_LABEL'`.

- [ ] **Step 3: Add the incident label constants**

In `core/src/dsf/contracts/handoff.py`, replace the trailing constants + `__all__`:

```python
HANDOFF_LABEL = "squad:ready"
HANDOFF_LABEL_DESCRIPTION = "Council-filed issue ready for coding-squad triage"
HANDOFF_LABEL_COLOR = "1d76db"

__all__ = ["HANDOFF_LABEL", "HANDOFF_LABEL_DESCRIPTION", "HANDOFF_LABEL_COLOR"]
```

with:

```python
HANDOFF_LABEL = "squad:ready"
HANDOFF_LABEL_DESCRIPTION = "Council-filed issue ready for coding-squad triage"
HANDOFF_LABEL_COLOR = "1d76db"

#: The SRE->council marker. The managed Azure SRE Agent stamps this on every
#: incident issue it files; the council's ``incidents`` source pulls only issues
#: carrying it. Because council-filed issues carry :data:`HANDOFF_LABEL` and never
#: this label, the incidents source cannot re-ingest council output (no loop).
INCIDENT_LABEL = "incident"
INCIDENT_LABEL_DESCRIPTION = "SRE-filed incident the feature council reflects on"
INCIDENT_LABEL_COLOR = "b60205"

__all__ = [
    "HANDOFF_LABEL",
    "HANDOFF_LABEL_DESCRIPTION",
    "HANDOFF_LABEL_COLOR",
    "INCIDENT_LABEL",
    "INCIDENT_LABEL_DESCRIPTION",
    "INCIDENT_LABEL_COLOR",
]
```

Also update the module docstring's first line to mention both contracts. Replace:

```python
"""The council->squad handoff contract: one well-known label.
```

with:

```python
"""Well-known label contracts: the council->squad handoff and the SRE->council
incident marker.
```

The original docstring body also contains a stale `squad triage` reference (the
coding squad is now a standing Ralph watch loop per ADR 0012). While editing this
file, replace:

```python
Every issue the feature council files carries :data:`HANDOFF_LABEL`, and the
coding squad's ``squad triage`` keys on exactly that label. Keeping it a single
```

with:

```python
Every issue the feature council files carries :data:`HANDOFF_LABEL`, and the
coding squad's standing ``squad watch`` Ralph loop keys on exactly that label.
Keeping it a single
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `uv run pytest core/tests/contracts/test_handoff.py -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add core/src/dsf/contracts/handoff.py core/tests/contracts/test_handoff.py
git commit -m "feat(contracts): add INCIDENT_LABEL SRE-to-council marker

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 2: Incidents source agent (fixture backend)

This task adds the `INCIDENTS` kind, its fixture, the fixture backend, and wires
the enum/registry/agent-registry/config together so the whole suite (including the
registry parity test) stays green.

**Files:**
- Modify: `core/src/dsf/contracts/enums.py`
- Create: `tests/fixtures/incidents_evidence.json`
- Create: `feature-council/src/dsf/agents/incidents/__init__.py`
- Create: `feature-council/src/dsf/agents/incidents/backend.py`
- Modify: `feature-council/src/dsf/agents/registry.py`
- Modify: `feature-council/src/dsf/orchestrator/agent_registry.py`
- Modify: `config/defaults.json`
- Test: `feature-council/tests/agents/incidents/test_incidents.py`

- [ ] **Step 1: Write the failing fixture-backend test**

Create `feature-council/tests/agents/incidents/test_incidents.py`:

```python
"""Incidents source agent tests (fixture backend + registry wiring)."""

from __future__ import annotations

from dsf.agents.incidents.backend import IncidentsFixtureBackend
from dsf.contracts.enums import SourceKind


async def test_fixture_backend_returns_grounded_incident_evidence():
    backend = IncidentsFixtureBackend()
    items = await backend.gather({"product_hints": ["microbi"]})

    assert len(items) >= 1
    for item in items:
        assert item.source_agent == "incidents"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.INCIDENTS
    assert backend.calls == [{"product_hints": ["microbi"]}]


def test_incidents_kind_exists():
    assert SourceKind.INCIDENTS.value == "INCIDENTS"
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest feature-council/tests/agents/incidents/test_incidents.py -q`
Expected: FAIL (`ModuleNotFoundError: dsf.agents.incidents` / `AttributeError: INCIDENTS`).

- [ ] **Step 3: Add the `INCIDENTS` enum value**

In `core/src/dsf/contracts/enums.py`, replace:

```python
class SourceKind(StrEnum):
    """Kind of source backend that produced evidence."""

    SENTRY = "SENTRY"
    GRAFANA = "GRAFANA"
    FOUNDRYIQ = "FOUNDRYIQ"
    WEBIQ = "WEBIQ"
    TICKETS = "TICKETS"
```

with:

```python
class SourceKind(StrEnum):
    """Kind of source backend that produced evidence."""

    SENTRY = "SENTRY"
    GRAFANA = "GRAFANA"
    FOUNDRYIQ = "FOUNDRYIQ"
    WEBIQ = "WEBIQ"
    TICKETS = "TICKETS"
    INCIDENTS = "INCIDENTS"
    AZUREMONITOR = "AZUREMONITOR"
```

(Both operational kinds are added now so the enum is stable for Tasks 2 and 3.)

- [ ] **Step 4: Create the incidents fixture**

Create `tests/fixtures/incidents_evidence.json`:

```json
[
  {
    "source_agent": "incidents",
    "claim": "Checkout 5xx incident recurred 4 times in 14 days with the same DB connection-pool exhaustion signature; each occurrence required a manual pool bump.",
    "raw_citation": "https://github.com/example/microbi/issues/512",
    "provenance": {
      "query_used": "label:incident state:open signature=checkout-5xx-pool-exhaustion",
      "source_kind": "INCIDENTS"
    },
    "confidence": 0.86,
    "product_hints": ["microbi", "checkout"]
  },
  {
    "source_agent": "incidents",
    "claim": "Orders webhook timeout incident filed once; no recurrence yet.",
    "raw_citation": "https://github.com/example/microbi/issues/518",
    "provenance": {
      "query_used": "label:incident state:open signature=orders-webhook-timeout",
      "source_kind": "INCIDENTS"
    },
    "confidence": 0.42,
    "product_hints": ["microbi", "orders"]
  }
]
```

- [ ] **Step 5: Create the incidents package `__init__.py`**

Create `feature-council/src/dsf/agents/incidents/__init__.py`:

```python
"""Incidents source agent.

Surfaces incident issues the managed Azure SRE Agent files into the product
repository (issues carrying :data:`~dsf.contracts.handoff.INCIDENT_LABEL`) as
grounded evidence, aggregating recurrences into higher-confidence claims. In
local/dry-run mode it uses :class:`IncidentsFixtureBackend` (fixture replay); in
azure mode it uses :class:`IncidentsGitHubBackend`, which lists incident issues
through an injected GitHub client. Runs as an Azure Container App in the product's
resource group (ADR 0004).
"""

from dsf.agents.incidents.backend import (
    IncidentsFixtureBackend,
    IncidentsGitHubBackend,
)

__all__ = ["IncidentsFixtureBackend", "IncidentsGitHubBackend"]
```

- [ ] **Step 6: Create the incidents backend (fixture + live)**

Create `feature-council/src/dsf/agents/incidents/backend.py`:

```python
"""Incidents source backends.

The incidents source turns SRE-filed incident issues (those carrying
:data:`~dsf.contracts.handoff.INCIDENT_LABEL`) into grounded
:class:`~dsf.contracts.models.EvidenceItem` objects so the feature council can
reflect on recurring production faults and decide whether systemic hardening is
warranted.

Two backends mirror the project's local/azure split:

* :class:`IncidentsFixtureBackend` — deterministic, loads a JSON fixture; used in
  local/dry-run mode and tests. Never touches the network.
* :class:`IncidentsGitHubBackend` — azure mode; lists incident issues via an
  injected ``gh_call`` client and aggregates recurrences onto evidence. All I/O
  goes through ``gh_call``; this class never opens a socket itself.

Recurrence intelligence lives here (design Approach A): issues are grouped by a
stable signature and a repeated signature is surfaced as a single, higher-
confidence item. The conveyor's threshold then decides what to do with it; no new
conveyor stage is added.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import TYPE_CHECKING, Any

from dsf.contracts.enums import SourceKind
from dsf.contracts.handoff import INCIDENT_LABEL
from dsf.contracts.models import EvidenceItem, Provenance

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def _fixture_path() -> Path:
    """Locate ``tests/fixtures/incidents_evidence.json`` at the repo root.

    ``feature-council/src/dsf/agents/incidents/backend.py`` -> repo root is five
    parents up.
    """
    here = Path(__file__).resolve()
    return here.parents[5] / "tests" / "fixtures" / "incidents_evidence.json"


class IncidentsFixtureBackend:
    """Local/dry-run incidents backend — replays a JSON fixture."""

    def __init__(self, fixture: Path | None = None) -> None:
        self._fixture = fixture or _fixture_path()
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return evidence loaded from the fixture."""
        self.calls.append(dict(run_scope))
        raw = json.loads(self._fixture.read_text(encoding="utf-8"))
        return [EvidenceItem.model_validate(item) for item in raw]


def _signature(issue: dict) -> str:
    """Stable grouping key for an incident issue.

    Prefers an explicit ``signature`` field; otherwise normalizes the title
    (lowercased, whitespace-collapsed) so repeated incidents collapse together.
    """
    explicit = issue.get("signature")
    if explicit:
        return str(explicit).strip().lower()
    return " ".join(str(issue.get("title", "")).lower().split())


def _confidence(count: int) -> float:
    """Scale confidence by recurrence count.

    A one-off scores low (below the default 0.6 bar); each extra occurrence adds
    weight, capped so a single signature never dominates outright.
    """
    return min(0.40 + 0.15 * (count - 1), 0.95)


class IncidentsGitHubBackend:
    """Azure-mode incidents backend — lists incident issues via a GitHub client.

    ``gh_call`` is an injected async callable that returns the open issues in the
    product repository carrying :data:`INCIDENT_LABEL`. Each returned issue is a
    dict with at least ``title`` and ``html_url`` (optionally ``signature``).
    Issues are grouped by signature; each group becomes one
    :class:`EvidenceItem` whose ``confidence`` rises with the recurrence count.
    """

    def __init__(
        self,
        gh_call: Callable[[dict], Awaitable[Any]] | None,
    ) -> None:
        if gh_call is None:
            raise RuntimeError(
                "IncidentsGitHubBackend requires a gh_call client (azure mode)"
            )
        self._gh_call = gh_call

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """List incident issues, group by signature, emit aggregated evidence."""
        issues = await self._gh_call(dict(run_scope)) or []
        groups: dict[str, list[dict]] = {}
        for issue in issues:
            groups.setdefault(_signature(issue), []).append(issue)

        product_hints = list(run_scope.get("product_hints", []))
        evidence: list[EvidenceItem] = []
        for signature, members in groups.items():
            count = len(members)
            head = members[0]
            title = head.get("title", signature)
            if count > 1:
                claim = f"Incident '{title}' recurred {count} times (signature: {signature})."
            else:
                claim = f"Incident '{title}' filed once; no recurrence yet."
            evidence.append(
                EvidenceItem(
                    source_agent="incidents",
                    claim=claim,
                    raw_citation=head.get("html_url") or signature,
                    provenance=Provenance(
                        query_used=f"label:{INCIDENT_LABEL} signature={signature}",
                        source_kind=SourceKind.INCIDENTS,
                    ),
                    confidence=_confidence(count),
                    product_hints=product_hints,
                )
            )
        return evidence


__all__ = ["IncidentsFixtureBackend", "IncidentsGitHubBackend"]
```

- [ ] **Step 7: Register the agent (deployable + S2 builders + config)**

The fixture-backend test does not need `main.py` yet, but the registry parity
test (`feature-council/tests/agents/test_registry.py`) asserts
`set(DEPLOYABLE_AGENTS) == {k.value.lower() for k in SourceKind}`. Since the enum
now has `INCIDENTS` and `AZUREMONITOR`, BOTH registry keys must be present to keep
that test green — but their `main:app` import paths must resolve. To avoid a
half-registered state, this step also creates the minimal `client.py`/`main.py`
for `incidents`. (The `azuremonitor` `main.py` is created in Task 3 Step 4; do
Task 2 and Task 3 back to back, or temporarily exclude `azuremonitor` from the
enum until Task 3. Recommended: keep both enum values and complete `incidents`
`main.py` here, then immediately create `azuremonitor` `main.py` stub in this same
step so all imports resolve.)

Create `feature-council/src/dsf/agents/incidents/client.py`:

```python
"""Live GitHub client for :class:`IncidentsGitHubBackend`.

Builds an async ``gh_call(scope)`` callable that lists the product repository's
open issues carrying :data:`~dsf.contracts.handoff.INCIDENT_LABEL` via the GitHub
REST API over ``httpx``. Constructed from environment variables but accepts an
injected ``httpx.AsyncClient`` so tests can drive it with ``httpx.MockTransport``
and never touch the network.
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import httpx

from dsf.agents.mode import env_required
from dsf.contracts.handoff import INCIDENT_LABEL

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def build_incidents_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[dict], Awaitable[list[dict]]]:
    """Return an async ``gh_call(scope)`` backed by the GitHub issues API.

    Env vars:

    * ``GITHUB_TOKEN`` (required) — token with read access to the repo's issues.
    * ``GITHUB_REPO`` (required) — ``owner/name`` of the product repository.

    The ``scope`` may override the repo via ``scope["github_repo"]``.
    """
    token = env_required("GITHUB_TOKEN", hint="GitHub token for incident issues")
    default_repo = env_required("GITHUB_REPO", hint="owner/name of product repo")
    auth_header = f"Bearer {token}"

    if client is None:
        client = httpx.AsyncClient(
            base_url="https://api.github.com",
            headers={
                "Authorization": auth_header,
                "Accept": "application/vnd.github+json",
            },
            timeout=20.0,
        )

    async def gh_call(scope: dict) -> list[dict]:
        repo = (scope or {}).get("github_repo") or default_repo
        resp = await client.get(
            f"/repos/{repo}/issues",
            params={"labels": INCIDENT_LABEL, "state": "open", "per_page": 100},
            headers={"Authorization": auth_header},
        )
        resp.raise_for_status()
        issues = resp.json() or []
        return [
            {
                "title": issue.get("title", ""),
                "html_url": issue.get("html_url", ""),
                "number": issue.get("number"),
            }
            for issue in issues
            # GitHub returns PRs on the issues endpoint too; drop them.
            if "pull_request" not in issue
        ]

    return gh_call


__all__ = ["build_incidents_client_from_env"]
```

Create `feature-council/src/dsf/agents/incidents/main.py`:

```python
"""Incidents agent entrypoint.

Builds the A2A app over the incidents backend. Run with
``uvicorn dsf.agents.incidents.main:app``. The GitHub backend is selected only in
azure mode; see :mod:`dsf.agents.incidents.backend`.
"""

from __future__ import annotations

from dsf.agents.base import SourceAgent
from dsf.agents.incidents.backend import (
    IncidentsFixtureBackend,
    IncidentsGitHubBackend,
)
from dsf.agents.mode import is_live, resolve_mode
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the incidents :class:`SourceAgent`, selecting the backend by mode."""
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.incidents.client import build_incidents_client_from_env

        backend = IncidentsGitHubBackend(gh_call=build_incidents_client_from_env())
    else:
        backend = IncidentsFixtureBackend()
    return SourceAgent(
        kind=SourceKind.INCIDENTS,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
```

In `feature-council/src/dsf/agents/registry.py`, replace the `DEPLOYABLE_AGENTS`
dict body:

```python
DEPLOYABLE_AGENTS: dict[str, str] = {
    "sentry": "dsf.agents.sentry.main:app",
    "grafana": "dsf.agents.grafana.main:app",
    "foundryiq": "dsf.agents.foundryiq.main:app",
    "webiq": "dsf.agents.webiq.main:app",
    "tickets": "dsf.agents.tickets.main:app",
}
```

with:

```python
DEPLOYABLE_AGENTS: dict[str, str] = {
    "sentry": "dsf.agents.sentry.main:app",
    "grafana": "dsf.agents.grafana.main:app",
    "foundryiq": "dsf.agents.foundryiq.main:app",
    "webiq": "dsf.agents.webiq.main:app",
    "tickets": "dsf.agents.tickets.main:app",
    "incidents": "dsf.agents.incidents.main:app",
    "azuremonitor": "dsf.agents.azuremonitor.main:app",
}
```

In `feature-council/src/dsf/orchestrator/agent_registry.py`, add the import. Ruff
enforces isort (`select = [..., "I", ...]`), so it must go in alphabetical order
between the `grafana` and `sentry` imports. The import block becomes:

```python
from dsf.agents.foundryiq.main import build_agent as _build_foundryiq
from dsf.agents.grafana.main import build_agent as _build_grafana
from dsf.agents.incidents.main import build_agent as _build_incidents
from dsf.agents.sentry.main import build_agent as _build_sentry
from dsf.agents.tickets.main import build_agent as _build_tickets
from dsf.agents.webiq.main import build_agent as _build_webiq
from dsf.contracts.enums import SourceKind
```

(If you misplace it, `uv run ruff check --fix feature-council/src/dsf/orchestrator/agent_registry.py`
will re-sort the block.)

and add to the `AGENT_BUILDERS` dict, after the `SourceKind.TICKETS` entry:

```python
    SourceKind.INCIDENTS: _build_incidents,
```

In `config/defaults.json`, replace the `TICKETS` entry inside the `agents` block:

```json
    "TICKETS": {
      "enabled": true
    }
```

with:

```json
    "TICKETS": {
      "enabled": true
    },
    "INCIDENTS": {
      "enabled": true
    }
```

> NOTE: `AZUREMONITOR` is added to `DEPLOYABLE_AGENTS` above so the parity test
> passes, which requires `dsf.agents.azuremonitor.main:app` to import. Therefore
> **Task 3 Steps 3-4 (create the `azuremonitor` package incl. `main.py`) must be
> completed before running the parity/import tests below.** Do Task 2 Step 7 and
> Task 3 Steps 3-4 together, then run the verification in this step and Task 3.

- [ ] **Step 8: Run the incidents fixture tests**

Run: `uv run pytest feature-council/tests/agents/incidents/test_incidents.py -q`
Expected: PASS (after Task 3's `azuremonitor` package exists, the imports in the
registry resolve).

- [ ] **Step 9: Commit (after Task 3 Step 4 exists)**

```bash
git add core/src/dsf/contracts/enums.py tests/fixtures/incidents_evidence.json \
  feature-council/src/dsf/agents/incidents config/defaults.json \
  feature-council/src/dsf/agents/registry.py \
  feature-council/src/dsf/orchestrator/agent_registry.py \
  feature-council/tests/agents/incidents/test_incidents.py
git commit -m "feat(council): add incidents source agent (fixture + GitHub backend)

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 3: Azure Monitor source agent

**Files:**
- Create: `tests/fixtures/azuremonitor_evidence.json`
- Create: `feature-council/src/dsf/agents/azuremonitor/{__init__.py,backend.py,client.py,main.py}`
- Modify: `feature-council/src/dsf/orchestrator/agent_registry.py`
- Modify: `config/defaults.json`
- Test: `feature-council/tests/agents/azuremonitor/test_azuremonitor.py`

- [ ] **Step 1: Write the failing fixture-backend test**

Create `feature-council/tests/agents/azuremonitor/test_azuremonitor.py`:

```python
"""Azure Monitor source agent tests (fixture backend + agent build)."""

from __future__ import annotations

from fastapi.testclient import TestClient

from dsf.agents.azuremonitor.backend import AzureMonitorFixtureBackend
from dsf.agents.azuremonitor.main import app, build_agent
from dsf.contracts.enums import SourceKind


async def test_fixture_backend_returns_grounded_telemetry_evidence():
    backend = AzureMonitorFixtureBackend()
    items = await backend.gather({"product_hints": ["microbi"]})

    assert len(items) >= 1
    for item in items:
        assert item.source_agent == "azuremonitor"
        assert item.raw_citation.strip()
        assert item.provenance.source_kind == SourceKind.AZUREMONITOR
    assert backend.calls == [{"product_hints": ["microbi"]}]


def test_agent_builds_with_azuremonitor_kind():
    agent = build_agent()
    assert agent.kind == SourceKind.AZUREMONITOR
    assert app is not None


def test_card_endpoint_reports_azuremonitor():
    client = TestClient(build_agent().make_app(token=""))
    body = client.get("/card").json()
    assert body["kind"] == "AZUREMONITOR"
    assert body["enabled"] is True
    assert "gather" in body["capabilities"]
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest feature-council/tests/agents/azuremonitor/test_azuremonitor.py -q`
Expected: FAIL (`ModuleNotFoundError: dsf.agents.azuremonitor`).

- [ ] **Step 3: Create the fixture + package backend**

Create `tests/fixtures/azuremonitor_evidence.json`:

```json
[
  {
    "source_agent": "azuremonitor",
    "claim": "App Insights: dependency failure rate to Azure SQL spiked to 12% (baseline <1%) for 18 minutes, correlating with checkout 5xx errors.",
    "raw_citation": "https://portal.azure.com/#@/resource/subscriptions/.../components/microbi-ai/failures",
    "provenance": {
      "query_used": "dependencies | where success == false | summarize failrate=...",
      "source_kind": "AZUREMONITOR"
    },
    "confidence": 0.81,
    "product_hints": ["microbi", "checkout"]
  },
  {
    "source_agent": "azuremonitor",
    "claim": "App Insights: server exceptions/min on the api role increased 6x and stayed elevated for 30 minutes.",
    "raw_citation": "https://portal.azure.com/#@/resource/subscriptions/.../components/microbi-ai/exceptions",
    "provenance": {
      "query_used": "exceptions | summarize count() by bin(timestamp, 1m), cloud_RoleName",
      "source_kind": "AZUREMONITOR"
    },
    "confidence": 0.77,
    "product_hints": ["microbi", "api"]
  }
]
```

Create `feature-council/src/dsf/agents/azuremonitor/__init__.py`:

```python
"""Azure Monitor source agent.

Surfaces production telemetry (Application Insights metrics, failures, and
exceptions) as grounded evidence. In local/dry-run mode the agent uses
:class:`AzureMonitorFixtureBackend` (fixture replay); in azure mode it uses
:class:`AzureMonitorBackend`, which queries Application Insights through an
injected client. Runs as an Azure Container App in the product's resource group
(ADR 0004).

The live telemetry role binding (Monitoring Reader on the product's Application
Insights resource) is a per-agent identity seam refined later; the offline path is
fully functional through the fixture backend.
"""

from dsf.agents.azuremonitor.backend import (
    AzureMonitorBackend,
    AzureMonitorFixtureBackend,
)

__all__ = ["AzureMonitorBackend", "AzureMonitorFixtureBackend"]
```

Create `feature-council/src/dsf/agents/azuremonitor/backend.py`:

```python
"""Azure Monitor source backends.

Surfaces production telemetry from Application Insights as grounded
:class:`~dsf.contracts.models.EvidenceItem` objects.

Two backends mirror the project's local/azure split:

* :class:`AzureMonitorFixtureBackend` — deterministic, loads a JSON fixture; used
  in local/dry-run mode and tests. Never touches the network.
* :class:`AzureMonitorBackend` — azure mode; queries Application Insights via an
  injected ``mcp_call`` client and maps results onto evidence. All I/O goes
  through ``mcp_call``; this class never opens a socket itself.
"""

from __future__ import annotations

import json
from pathlib import Path
from typing import TYPE_CHECKING, Any

from dsf.contracts.enums import SourceKind
from dsf.contracts.models import EvidenceItem, Provenance

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable


def _fixture_path() -> Path:
    """Locate ``tests/fixtures/azuremonitor_evidence.json`` at the repo root.

    ``feature-council/src/dsf/agents/azuremonitor/backend.py`` -> repo root is
    five parents up.
    """
    here = Path(__file__).resolve()
    return here.parents[5] / "tests" / "fixtures" / "azuremonitor_evidence.json"


class AzureMonitorFixtureBackend:
    """Local/dry-run Azure Monitor backend — replays a JSON fixture."""

    def __init__(self, fixture: Path | None = None) -> None:
        self._fixture = fixture or _fixture_path()
        self.calls: list[dict] = []

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Record the call and return evidence loaded from the fixture."""
        self.calls.append(dict(run_scope))
        raw = json.loads(self._fixture.read_text(encoding="utf-8"))
        return [EvidenceItem.model_validate(item) for item in raw]


class AzureMonitorBackend:
    """Azure-mode backend — queries Application Insights via an injected client.

    ``mcp_call`` is an injected async callable that, given a query spec, returns
    Application Insights rows. The spec's KQL queries are derived from
    ``run_scope`` (or the product registry's ``azure_monitor_scope``). Each row
    maps onto an :class:`EvidenceItem`.
    """

    def __init__(
        self,
        mcp_call: Callable[[dict], Awaitable[Any]] | None,
    ) -> None:
        if mcp_call is None:
            raise RuntimeError(
                "AzureMonitorBackend requires an mcp_call client (azure mode)"
            )
        self._mcp_call = mcp_call

    def _queries(self, run_scope: dict) -> list[dict]:
        """Derive query specs from ``run_scope`` / product registry scope."""
        queries = run_scope.get("azure_monitor_queries")
        if queries:
            return list(queries)
        scope = (
            run_scope.get("product_registry", {}).get("azure_monitor_scope")
            or run_scope.get("azure_monitor_scope")
            or ""
        )
        if not scope:
            return []
        return [{"app": scope}]

    async def gather(self, run_scope: dict) -> list[EvidenceItem]:
        """Query Application Insights via ``mcp_call`` and map rows to evidence."""
        evidence: list[EvidenceItem] = []
        for spec in self._queries(run_scope):
            results = await self._mcp_call(spec)
            for row in results or []:
                query = row.get("query") or spec.get("query") or spec.get("app", "")
                citation = row.get("link") or query
                evidence.append(
                    EvidenceItem(
                        source_agent="azuremonitor",
                        claim=row["summary"],
                        raw_citation=citation,
                        provenance=Provenance(
                            query_used=query,
                            source_kind=SourceKind.AZUREMONITOR,
                        ),
                        confidence=float(row.get("confidence", 0.0)),
                        product_hints=list(row.get("product_hints", [])),
                    )
                )
        return evidence


__all__ = ["AzureMonitorBackend", "AzureMonitorFixtureBackend"]
```

- [ ] **Step 4: Create the client + main**

Create `feature-council/src/dsf/agents/azuremonitor/client.py`:

```python
"""Live Application Insights client for :class:`AzureMonitorBackend`.

Builds an async ``mcp_call(spec)`` callable backed by the Application Insights
query API via ``httpx``. Constructed from environment variables but accepts an
injected ``httpx.AsyncClient`` so tests can drive it with ``httpx.MockTransport``
and never touch the network.

The token/key wiring here is the data path; the Azure RBAC role binding that lets
the agent identity read telemetry (Monitoring Reader) is a per-agent seam refined
later (design section 6).
"""

from __future__ import annotations

from typing import TYPE_CHECKING

import httpx

from dsf.agents.mode import env_required

if TYPE_CHECKING:
    from collections.abc import Awaitable, Callable

#: Default KQL surfacing recent failed dependencies and exceptions.
_DEFAULT_KQL = (
    "union exceptions, (dependencies | where success == false) "
    "| where timestamp > ago(1h) "
    "| summarize n=count() by cloud_RoleName "
    "| order by n desc"
)


def build_azure_monitor_client_from_env(
    client: httpx.AsyncClient | None = None,
) -> Callable[[dict], Awaitable[list[dict]]]:
    """Return an async ``mcp_call(spec)`` backed by the App Insights query API.

    Env vars:

    * ``AZURE_MONITOR_APP_ID`` (required) — Application Insights application id.
    * ``AZURE_MONITOR_API_KEY`` (required) — API key for the query endpoint.

    ``spec`` may carry an ``app`` (overrides the env app id) and a ``query``
    (overrides the default KQL).
    """
    default_app = env_required("AZURE_MONITOR_APP_ID", hint="App Insights app id")
    api_key = env_required("AZURE_MONITOR_API_KEY", hint="App Insights API key")

    if client is None:
        client = httpx.AsyncClient(
            base_url="https://api.applicationinsights.io",
            headers={"x-api-key": api_key},
            timeout=20.0,
        )

    async def mcp_call(spec: dict) -> list[dict]:
        spec = spec or {}
        app_id = spec.get("app") or default_app
        query = spec.get("query") or _DEFAULT_KQL
        resp = await client.get(
            f"/v1/apps/{app_id}/query",
            params={"query": query},
            headers={"x-api-key": api_key},
        )
        resp.raise_for_status()
        payload = resp.json()
        tables = payload.get("tables") or []
        rows: list[dict] = []
        link = f"https://portal.azure.com/#@/resource/apps/{app_id}/logs"
        for table in tables:
            cols = [c.get("name") for c in table.get("columns", [])]
            for record in table.get("rows", []):
                mapped = dict(zip(cols, record, strict=False))
                role = mapped.get("cloud_RoleName", "telemetry")
                count = mapped.get("n", "")
                rows.append(
                    {
                        "summary": f"{role}: {count} failures/exceptions in the last hour.",
                        "query": query,
                        "link": link,
                        "confidence": 0.7,
                        "product_hints": [],
                    }
                )
        return rows

    return mcp_call


__all__ = ["build_azure_monitor_client_from_env"]
```

Create `feature-council/src/dsf/agents/azuremonitor/main.py`:

```python
"""Azure Monitor agent entrypoint.

Builds the A2A app over the Azure Monitor backend. Run with
``uvicorn dsf.agents.azuremonitor.main:app``. The live backend is selected only in
azure mode; see :mod:`dsf.agents.azuremonitor.backend`.
"""

from __future__ import annotations

from dsf.agents.azuremonitor.backend import (
    AzureMonitorBackend,
    AzureMonitorFixtureBackend,
)
from dsf.agents.base import SourceAgent
from dsf.agents.mode import is_live, resolve_mode
from dsf.config.store import InMemoryConfigStore
from dsf.contracts.enums import SourceKind


def build_agent(config: object | None = None, mode: str | None = None) -> SourceAgent:
    """Build the Azure Monitor :class:`SourceAgent`, selecting backend by mode."""
    cfg = config if config is not None else InMemoryConfigStore.from_defaults()
    if is_live(resolve_mode(mode)):
        from dsf.agents.azuremonitor.client import (
            build_azure_monitor_client_from_env,
        )

        backend = AzureMonitorBackend(mcp_call=build_azure_monitor_client_from_env())
    else:
        backend = AzureMonitorFixtureBackend()
    return SourceAgent(
        kind=SourceKind.AZUREMONITOR,
        backend=backend,
        config=cfg,  # type: ignore[arg-type]
        capabilities=["gather"],
    )


#: ASGI app served by uvicorn (auth read from ``A2A_BEARER_TOKEN`` env var).
app = build_agent().make_app()


__all__ = ["app", "build_agent"]
```

In `feature-council/src/dsf/orchestrator/agent_registry.py`, add the import. Ruff
enforces isort, and `azuremonitor` sorts first, so it goes at the top of the
`from dsf.agents.*` block. After Task 2 added `_build_incidents`, the block becomes:

```python
from dsf.agents.azuremonitor.main import build_agent as _build_azuremonitor
from dsf.agents.foundryiq.main import build_agent as _build_foundryiq
from dsf.agents.grafana.main import build_agent as _build_grafana
from dsf.agents.incidents.main import build_agent as _build_incidents
from dsf.agents.sentry.main import build_agent as _build_sentry
from dsf.agents.tickets.main import build_agent as _build_tickets
from dsf.agents.webiq.main import build_agent as _build_webiq
from dsf.contracts.enums import SourceKind
```

(`uv run ruff check --fix feature-council/src/dsf/orchestrator/agent_registry.py`
re-sorts the block if you misplace it.)

and add to `AGENT_BUILDERS`, after the `SourceKind.INCIDENTS` entry:

```python
    SourceKind.AZUREMONITOR: _build_azuremonitor,
```

In `config/defaults.json`, replace the `INCIDENTS` entry added in Task 2:

```json
    "INCIDENTS": {
      "enabled": true
    }
```

with:

```json
    "INCIDENTS": {
      "enabled": true
    },
    "AZUREMONITOR": {
      "enabled": true
    }
```

- [ ] **Step 5: Run the azuremonitor + registry tests**

Run:
```
uv run pytest feature-council/tests/agents/azuremonitor/test_azuremonitor.py \
  feature-council/tests/agents/incidents/test_incidents.py \
  feature-council/tests/agents/test_registry.py -q
```
Expected: PASS (parity test now green: enum and registry both carry the two kinds;
all `main:app` imports resolve).

- [ ] **Step 6: Commit**

```bash
git add tests/fixtures/azuremonitor_evidence.json \
  feature-council/src/dsf/agents/azuremonitor config/defaults.json \
  feature-council/src/dsf/orchestrator/agent_registry.py \
  feature-council/tests/agents/azuremonitor/test_azuremonitor.py
git commit -m "feat(council): add azuremonitor telemetry source agent

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 4: Recurrence aggregation + live client tests

Prove the design's core behaviors: incidents recurrence aggregation and both live
clients map without touching the network.

**Files:**
- Test: `feature-council/tests/agents/incidents/test_incidents_live.py`
- Test: `feature-council/tests/agents/azuremonitor/test_azuremonitor_live.py`

- [ ] **Step 1: Write the incidents recurrence + client test**

Create `feature-council/tests/agents/incidents/test_incidents_live.py`:

```python
"""Incidents recurrence aggregation + live GitHub client (no network)."""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.incidents.backend import (
    IncidentsFixtureBackend,
    IncidentsGitHubBackend,
)
from dsf.agents.incidents.client import build_incidents_client_from_env
from dsf.agents.incidents.main import build_agent


def test_github_backend_requires_gh_call():
    with pytest.raises(RuntimeError, match="requires a gh_call client"):
        IncidentsGitHubBackend(gh_call=None)


async def test_recurring_signature_collapses_to_one_higher_confidence_item():
    async def fake_gh_call(scope: dict) -> list[dict]:
        return [
            {"title": "Checkout 5xx", "html_url": "https://gh/x/issues/1"},
            {"title": "checkout  5xx", "html_url": "https://gh/x/issues/2"},
            {"title": "CHECKOUT 5XX", "html_url": "https://gh/x/issues/7"},
            {"title": "Orders timeout", "html_url": "https://gh/x/issues/9"},
        ]

    backend = IncidentsGitHubBackend(gh_call=fake_gh_call)
    items = await backend.gather({"product_hints": ["microbi"]})

    by_claim = {i.claim: i for i in items}
    assert len(items) == 2
    recurring = next(i for i in items if "recurred 3 times" in i.claim)
    oneoff = next(i for i in items if "filed once" in i.claim)
    assert recurring.confidence > oneoff.confidence
    assert recurring.product_hints == ["microbi"]
    # Citation points at the first issue in the group.
    assert recurring.raw_citation == "https://gh/x/issues/1"
    assert by_claim  # at least the two grouped claims exist


async def test_one_off_incident_scores_below_default_bar():
    async def fake_gh_call(scope: dict) -> list[dict]:
        return [{"title": "Single blip", "html_url": "https://gh/x/issues/3"}]

    backend = IncidentsGitHubBackend(gh_call=fake_gh_call)
    items = await backend.gather({})
    assert len(items) == 1
    assert items[0].confidence < 0.6


@pytest.fixture(autouse=True)
def _env(monkeypatch):
    monkeypatch.setenv("GITHUB_TOKEN", "tok")
    monkeypatch.setenv("GITHUB_REPO", "example/microbi")


async def test_client_lists_incident_issues_and_drops_prs():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["query"] = request.url.query.decode()
        captured["auth"] = request.headers.get("Authorization")
        return httpx.Response(
            200,
            json=[
                {"title": "Checkout 5xx", "html_url": "https://gh/x/issues/1", "number": 1},
                {
                    "title": "A PR",
                    "html_url": "https://gh/x/pull/2",
                    "number": 2,
                    "pull_request": {"url": "..."},
                },
            ],
        )

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler),
        base_url="https://api.github.com",
        headers={"Authorization": "Bearer tok"},
    )
    gh_call = build_incidents_client_from_env(client=client)
    rows = await gh_call({"github_repo": "example/microbi"})

    assert captured["path"] == "/repos/example/microbi/issues"
    assert "labels=incident" in captured["query"]
    assert "state=open" in captured["query"]
    assert captured["auth"] == "Bearer tok"
    assert len(rows) == 1
    assert rows[0]["title"] == "Checkout 5xx"


def test_build_agent_local_uses_fixture(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, IncidentsFixtureBackend)


def test_build_agent_live_uses_github(monkeypatch):
    monkeypatch.setenv("GITHUB_TOKEN", "live-tok")
    monkeypatch.setenv("GITHUB_REPO", "example/microbi")
    assert isinstance(build_agent(mode="live").backend, IncidentsGitHubBackend)


def test_build_agent_live_missing_env_raises(monkeypatch):
    monkeypatch.delenv("GITHUB_TOKEN", raising=False)
    monkeypatch.delenv("GITHUB_REPO", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
```

- [ ] **Step 2: Run it (red, then green)**

Run: `uv run pytest feature-council/tests/agents/incidents/test_incidents_live.py -q`
Expected: PASS (the backend + client already exist from Tasks 2-3). If
`test_recurring_signature_collapses...` fails on the confidence formula, confirm
`_confidence(3) = 0.40 + 0.15*2 = 0.70` and `_confidence(1) = 0.40`; adjust the
assertion only if the formula in `backend.py` was changed.

- [ ] **Step 3: Write the azuremonitor live client test**

Create `feature-council/tests/agents/azuremonitor/test_azuremonitor_live.py`:

```python
"""Live App Insights client + backend selection (no network)."""

from __future__ import annotations

import httpx
import pytest

from dsf.agents.azuremonitor.backend import (
    AzureMonitorBackend,
    AzureMonitorFixtureBackend,
)
from dsf.agents.azuremonitor.client import build_azure_monitor_client_from_env
from dsf.agents.azuremonitor.main import build_agent
from dsf.contracts.enums import SourceKind

_CANNED = {
    "tables": [
        {
            "name": "PrimaryResult",
            "columns": [{"name": "cloud_RoleName"}, {"name": "n"}],
            "rows": [["api", 42], ["checkout", 17]],
        }
    ]
}


@pytest.fixture(autouse=True)
def _env(monkeypatch):
    monkeypatch.setenv("AZURE_MONITOR_APP_ID", "app-123")
    monkeypatch.setenv("AZURE_MONITOR_API_KEY", "key")


def test_backend_requires_mcp_call():
    with pytest.raises(RuntimeError, match="requires an mcp_call client"):
        AzureMonitorBackend(mcp_call=None)


async def test_client_maps_rows_and_hits_query_path():
    captured: dict = {}

    def handler(request: httpx.Request) -> httpx.Response:
        captured["path"] = request.url.path
        captured["key"] = request.headers.get("x-api-key")
        return httpx.Response(200, json=_CANNED)

    client = httpx.AsyncClient(
        transport=httpx.MockTransport(handler),
        base_url="https://api.applicationinsights.io",
        headers={"x-api-key": "key"},
    )
    mcp_call = build_azure_monitor_client_from_env(client=client)
    rows = await mcp_call({"app": "app-123"})

    assert captured["path"] == "/v1/apps/app-123/query"
    assert captured["key"] == "key"
    assert len(rows) == 2
    assert rows[0]["summary"].startswith("api: 42")
    assert rows[0]["confidence"] == 0.7


async def test_backend_maps_client_rows_to_evidence():
    async def fake_mcp_call(spec: dict) -> list[dict]:
        return [
            {
                "summary": "api: 42 failures/exceptions in the last hour.",
                "query": "exceptions | summarize count()",
                "link": "https://portal.azure.com/x",
                "confidence": 0.7,
                "product_hints": ["microbi", "api"],
            }
        ]

    backend = AzureMonitorBackend(mcp_call=fake_mcp_call)
    items = await backend.gather({"azure_monitor_scope": "app-123"})

    assert len(items) == 1
    item = items[0]
    assert item.source_agent == "azuremonitor"
    assert item.provenance.source_kind == SourceKind.AZUREMONITOR
    assert item.confidence == pytest.approx(0.7)
    assert item.product_hints == ["microbi", "api"]


async def test_backend_without_scope_yields_no_evidence():
    async def fake_mcp_call(spec: dict) -> list[dict]:
        raise AssertionError("must not be called without a scope")

    backend = AzureMonitorBackend(mcp_call=fake_mcp_call)
    assert await backend.gather({}) == []


def test_build_agent_local_uses_fixture(monkeypatch):
    monkeypatch.delenv("DSF_MODE", raising=False)
    assert isinstance(build_agent(mode="local").backend, AzureMonitorFixtureBackend)


def test_build_agent_live_uses_real_backend(monkeypatch):
    monkeypatch.setenv("AZURE_MONITOR_APP_ID", "app-123")
    monkeypatch.setenv("AZURE_MONITOR_API_KEY", "key")
    assert isinstance(build_agent(mode="live").backend, AzureMonitorBackend)


def test_build_agent_live_missing_env_raises(monkeypatch):
    monkeypatch.delenv("AZURE_MONITOR_APP_ID", raising=False)
    monkeypatch.delenv("AZURE_MONITOR_API_KEY", raising=False)
    with pytest.raises(RuntimeError):
        build_agent(mode="live")
```

- [ ] **Step 4: Run it**

Run: `uv run pytest feature-council/tests/agents/azuremonitor/test_azuremonitor_live.py -q`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add feature-council/tests/agents/incidents/test_incidents_live.py \
  feature-council/tests/agents/azuremonitor/test_azuremonitor_live.py
git commit -m "test(council): recurrence aggregation + offline live-client tests

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 5: Provisioning creates the incident label

**Files:**
- Modify: `cli/src/dsf/instance/provisioner.py`
- Test: `cli/tests/instance/test_provisioner.py` (append)

- [ ] **Step 1: Find the existing label test to mirror style**

Run: `grep -n "test_plan_create_labels\|_spec()\|labels.commands\|HANDOFF_LABEL" cli/tests/instance/test_provisioner.py`
Expected: `test_plan_create_labels_covers_taxonomy_and_handoff` builds the plan via
`InstanceProvisioner(_spec()).plan()`, finds the `create_labels` step, and asserts
over `labels.commands`. The file already has a module-level `_spec()` helper
(`InstanceSpec(product="demo", owner="acme")`) and imports `HANDOFF_LABEL`.

- [ ] **Step 2: Write the failing test**

Append to `cli/tests/instance/test_provisioner.py`. Add `INCIDENT_LABEL` to the
existing `from dsf.contracts.handoff import ...` line (currently
`HANDOFF_LABEL, HANDOFF_LABEL_COLOR`), then add:

```python
def test_plan_create_labels_includes_incident_marker():
    spec = _spec()
    plan = InstanceProvisioner(spec).plan()
    labels = next(s for s in plan.steps if s.name == "create_labels")
    created = [c[3] for c in labels.commands]
    assert INCIDENT_LABEL in created
    incident_cmd = next(c for c in labels.commands if c[3] == INCIDENT_LABEL)
    assert incident_cmd[:3] == ["gh", "label", "create"]
    assert "--force" in incident_cmd
    assert incident_cmd[incident_cmd.index("--repo") + 1] == spec.github_repo()
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_provisioner.py::test_plan_create_labels_includes_incident_marker -q`
Expected: FAIL (no `gh label create incident` command in the `create_labels` step).

- [ ] **Step 4: Add the incident label command**

In `cli/src/dsf/instance/provisioner.py`, update the import block:

```python
from dsf.contracts.handoff import (
    HANDOFF_LABEL,
    HANDOFF_LABEL_COLOR,
    HANDOFF_LABEL_DESCRIPTION,
)
```

to:

```python
from dsf.contracts.handoff import (
    HANDOFF_LABEL,
    HANDOFF_LABEL_COLOR,
    HANDOFF_LABEL_DESCRIPTION,
    INCIDENT_LABEL,
    INCIDENT_LABEL_COLOR,
    INCIDENT_LABEL_DESCRIPTION,
)
```

Then in `_label_commands`, after the block that appends the `HANDOFF_LABEL`
command (the `commands.append([... HANDOFF_LABEL ...])` ending with `]`), append:

```python
    commands.append(
        [
            "gh", "label", "create", INCIDENT_LABEL,
            "--repo", repo,
            "--color", INCIDENT_LABEL_COLOR,
            "--description", INCIDENT_LABEL_DESCRIPTION,
            "--force",
        ]
    )
```

Also update the `_label_commands` docstring line to mention the incident marker:
change "plus the universal\n    council->squad :data:`HANDOFF_LABEL`," to
"plus the universal council->squad :data:`HANDOFF_LABEL` and the SRE->council
:data:`INCIDENT_LABEL`,".

- [ ] **Step 5: Run the test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_provisioner.py -q`
Expected: PASS (new test green; existing label tests still green).

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/provisioner.py cli/tests/instance/test_provisioner.py
git commit -m "feat(cli): create the incident label during provisioning

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 6: SRE runbook stamps the incident label

**Files:**
- Modify: `cli/src/dsf/instance/runtime_render.py`
- Test: `cli/tests/instance/test_runtime_render.py` (append; confirm exact path first)

- [ ] **Step 1: Confirm the runbook test file + an existing assertion to mirror**

Run: `grep -rln "render_sre_onboarding\|sre-onboarding" cli/tests`
Expected: `cli/tests/instance/test_runtime_render.py` (confirmed to exist). It
already defines a `_manifest(tmp_path)` helper and a
`test_render_sre_onboarding_writes_guided_runbook` test that exercises the public
`render_sre_onboarding`. Append the new test there.

- [ ] **Step 2: Write the failing test**

Append to the runbook test module:

```python
def test_sre_onboarding_instructs_incident_label(tmp_path):
    from dsf.contracts.handoff import INCIDENT_LABEL
    from dsf.instance.runtime_render import _render_sre_onboarding_md

    md = _render_sre_onboarding_md(
        product="microbi",
        resource_group="rg-microbi",
        location="westeurope",
        repo="example/microbi",
    )
    # The agent must stamp the incident marker so the council's incidents source
    # pulls it, and the runbook must explain the council now learns from incidents.
    assert f"`{INCIDENT_LABEL}`" in md
    assert "council" in md.lower()
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py::test_sre_onboarding_instructs_incident_label -q`
Expected: FAIL (`incident` marker / council learning text absent).

- [ ] **Step 4: Add the runbook instruction**

In `cli/src/dsf/instance/runtime_render.py`, confirm `INCIDENT_LABEL` is importable
(it is exported from `dsf.contracts.handoff`). Find the import of `HANDOFF_LABEL`
in this file and extend it to also import `INCIDENT_LABEL`. For example, change:

```python
from dsf.contracts.handoff import HANDOFF_LABEL
```

to:

```python
from dsf.contracts.handoff import HANDOFF_LABEL, INCIDENT_LABEL
```

(If `HANDOFF_LABEL` is imported as part of a multi-name block, add `INCIDENT_LABEL`
to that block instead.)

Then in `_render_sre_onboarding_md`, replace the final section string:

```python
        "## 4. Keep the squad handoff\n\n"
        "The agent files issues/PRs into the repo. Incident issues must carry the\n"
        f"`{HANDOFF_LABEL}` label so the standing coding-squad Ralph watch loop\n"
        "picks them up — that label is already created by the `create_labels`\n"
        "provisioning step.\n"
```

with:

```python
        "## 4. Keep the squad handoff (fast path)\n\n"
        "The agent files issues/PRs into the repo. Incident issues must carry the\n"
        f"`{HANDOFF_LABEL}` label so the standing coding-squad Ralph watch loop\n"
        "picks them up — that label is already created by the `create_labels`\n"
        "provisioning step.\n\n"
        "## 5. Stamp the incident marker (slow path)\n\n"
        f"Also stamp every incident issue with the `{INCIDENT_LABEL}` label. The\n"
        "feature council's incidents source pulls issues carrying it on its own\n"
        "schedule, so the council now learns from resolved incidents (recurring\n"
        "faults become systemic hardening proposals) alongside production\n"
        f"telemetry. `{INCIDENT_LABEL}` is created by the `create_labels` step.\n"
```

- [ ] **Step 5: Run the test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py -q`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add cli/src/dsf/instance/runtime_render.py cli/tests/instance/test_runtime_render.py
git commit -m "feat(cli): SRE runbook stamps the incident marker for the council

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 7: Product telemetry scope wiring

**Files:**
- Modify: `core/src/dsf/config/registry.py`
- Modify: `cli/src/dsf/instance/runtime_render.py` (`_product_from_spec`)
- Test: `core/tests/config/test_registry.py` (append) + `cli/tests/instance/test_runtime_render.py` (append)

- [ ] **Step 1: Write the failing registry test**

Append to `core/tests/config/test_registry.py` (confirmed to exist; it already
builds `Product(key=..., github_repo=...)` instances in the same style):

```python
def test_product_has_azure_monitor_scope_default():
    from dsf.config.registry import Product

    p = Product(key="microbi", github_repo="example/microbi")
    assert p.azure_monitor_scope == ""

    scoped = Product(
        key="microbi",
        github_repo="example/microbi",
        azure_monitor_scope="app-123",
    )
    assert scoped.azure_monitor_scope == "app-123"
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `uv run pytest core/tests/config/test_registry.py::test_product_has_azure_monitor_scope_default -q`
Expected: FAIL (`Product` has no `azure_monitor_scope`; pydantic ignores the kwarg
or the attribute access raises).

- [ ] **Step 3: Add the field**

In `core/src/dsf/config/registry.py`, in `class Product`, after the
`grafana_dashboards` field, add:

```python
    azure_monitor_scope: str = ""
```

So the field block reads:

```python
    foundryiq_scope: str = ""
    sentry_projects: list[str] = Field(default_factory=list)
    grafana_dashboards: list[str] = Field(default_factory=list)
    azure_monitor_scope: str = ""
    confidence_threshold: float = 0.6
```

- [ ] **Step 4: Run the registry test to verify it passes**

Run: `uv run pytest core/tests/config/test_registry.py -q`
Expected: PASS.

- [ ] **Step 5: Write the failing `_product_from_spec` test**

Append to the runtime-render test module:

```python
def test_product_from_spec_threads_azure_monitor_scope():
    from dsf.instance.runtime_render import _product_from_spec
    from dsf.instance.spec import InstanceSpec

    spec = InstanceSpec(product="microbi", owner="acme")
    product = _product_from_spec(spec)
    # Defaults to the product key so the telemetry source has a non-empty scope to
    # resolve in azure mode; the live Application Insights id is filled in during
    # observability onboarding.
    assert product.azure_monitor_scope == spec.product
```

`InstanceSpec` is already imported at the top of this test module; the local
import above keeps the test self-contained if it is read in isolation.

- [ ] **Step 6: Run it to verify it fails**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py::test_product_from_spec_threads_azure_monitor_scope -q`
Expected: FAIL (`azure_monitor_scope` is empty, not the product key).

- [ ] **Step 7: Thread the scope in `_product_from_spec`**

In `cli/src/dsf/instance/runtime_render.py`, update `_product_from_spec`'s
`return Product(...)` to add the scope, and refresh the docstring. Replace:

```python
    return Product(
        key=spec.product,
        github_repo=spec.github_repo(),
        label_taxonomy=spec.label_taxonomy,
        confidence_threshold=spec.confidence_threshold,
        foundryiq_scope=spec.product,
    )
```

with:

```python
    return Product(
        key=spec.product,
        github_repo=spec.github_repo(),
        label_taxonomy=spec.label_taxonomy,
        confidence_threshold=spec.confidence_threshold,
        foundryiq_scope=spec.product,
        azure_monitor_scope=spec.product,
    )
```

And update the docstring's parenthetical to note `azure_monitor_scope` defaults to
the product key like `foundryiq_scope` (the live Application Insights id is filled
in during observability onboarding).

- [ ] **Step 8: Run the render test to verify it passes**

Run: `uv run pytest cli/tests/instance/test_runtime_render.py -q`
Expected: PASS.

- [ ] **Step 9: Commit**

```bash
git add core/src/dsf/config/registry.py cli/src/dsf/instance/runtime_render.py \
  core/tests/config/test_registry.py cli/tests/instance/test_runtime_render.py
git commit -m "feat: thread azure_monitor_scope into the product registry

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 8: Offline end-to-end — operational kind through the conveyor

**Files:**
- Test: `feature-council/tests/e2e/test_operational_line.py`

- [ ] **Step 1: Write the e2e test**

Create `feature-council/tests/e2e/test_operational_line.py`:

```python
"""End-to-end: an operational signal (INCIDENTS) flows through the whole conveyor
to a routed, grounded, squad:ready issue — offline, no real GitHub call."""

from __future__ import annotations

from dsf.contracts.enums import RunStatus, SourceKind
from dsf.contracts.handoff import HANDOFF_LABEL
from dsf.container import build_services
from dsf.orchestrator.blackboard import Blackboard
from dsf.orchestrator.conveyor import run_line
from dsf.triggers.ingestion import signal_to_run


async def test_incident_signal_flows_to_grounded_squad_issue() -> None:
    services = build_services("local")
    run = signal_to_run(
        {
            "id": "evt_incident_recurrence",
            "source": "incidents",
            "product_hints": ["microbi"],
            "source_kinds": ["INCIDENTS"],
            "title": "Recurring checkout 5xx",
            "text": "Checkout 5xx incident recurred several times this sprint.",
            "dry_run": True,
        }
    )

    final = await run_line(run, services)

    assert final.status is RunStatus.FILED
    # Operational evidence rode the conveyor.
    assert any(
        e.provenance.source_kind is SourceKind.INCIDENTS for e in final.evidence
    ), "expected INCIDENTS evidence on the run"

    bb = Blackboard(services.memory)
    issues = await bb.load_issues(final.id)
    proposals = {p.id: p for p in await bb.load_proposals(final.id)}
    assert issues, "expected at least one routed issue"
    evidence_ids = {e.id for e in final.evidence}
    for issue in issues:
        prop = proposals.get(issue.proposal_id)
        assert prop is not None
        assert prop.evidence_ids, "filed proposal must be grounded"
        assert set(prop.evidence_ids) <= evidence_ids
        assert HANDOFF_LABEL in issue.labels

    # Dry-run: nothing filed for real.
    assert services.github.calls == []
    assert all(issue.filed_url is None for issue in issues)
```

- [ ] **Step 2: Run the e2e test**

Run: `uv run pytest feature-council/tests/e2e/test_operational_line.py -q`
Expected: PASS.

Troubleshooting if it does NOT reach `FILED` with a routed issue:
- Confirm the incidents fixture's top item confidence (0.86) clears the product's
  `confidence_threshold` (default 0.6). The `microbi` product in
  `config/products.json` may set a different threshold; if so, either lower the
  assertion to "reaches FILED with evidence present" or raise the fixture
  confidence. Do NOT weaken grounding assertions.
- Confirm `issue.labels` is the attribute name on the routed issue model
  (`grep -n "labels" feature-council/src/dsf/contracts/models.py`). Adjust the
  attribute in the test if the field differs; keep the assertion intent.
- If `Blackboard.load_issues` / `load_proposals` signatures differ from the
  existing `feature-council/tests/e2e/test_dry_run_line.py`, mirror that file
  exactly (it is the known-good reference for these calls).

- [ ] **Step 3: Commit**

```bash
git add feature-council/tests/e2e/test_operational_line.py
git commit -m "test(e2e): operational INCIDENTS evidence routes through S1-S7

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 9: Documentation — un-defer the loop, fix stale references, add ADR 0013

No code; updates the narrative + records the decision. (Doc-only; not linted/tested
beyond the unicode scan.)

**Files:**
- Modify: `feature-council/src/dsf/orchestrator/stations/s6_routing.py`
- Modify: `docs/adr/0009-leverage-azure-sre-agent.md`
- Modify: `docs/phases/sre-agent.md`
- Modify: `README.md`, `RUNBOOK.md` (only where the deferred slow path appears)
- Create: `docs/adr/0013-sre-council-feedback-loop.md`

- [ ] **Step 1: Fix the stale routing comment**

In `feature-council/src/dsf/orchestrator/stations/s6_routing.py`, replace:

```python
    # The universal council->squad handoff signal: squad triage keys on this.
    labels.append(HANDOFF_LABEL)
```

with:

```python
    # The universal council->squad handoff signal: the coding-squad Ralph watch
    # loop (ADR 0012) keys on this.
    labels.append(HANDOFF_LABEL)
```

- [ ] **Step 2: Fix the two stale ADR 0009 references**

In `docs/adr/0009-leverage-azure-sre-agent.md`:

Replace (around line 49):

```
  label means the **same** `squad triage --execute` intake picks them up — one
```

with:

```
  label means the **same** coding-squad Ralph watch loop (ADR 0012) picks them up — one
```

Replace (in the onboarding-flow code block, around line 64):

```
Azure SRE Agent → investigates incidents → files issues/PRs (squad:ready)
        → squad triage --execute → Copilot coding agent → PR → human review
```

with:

```
Azure SRE Agent → investigates incidents → files issues/PRs (squad:ready + incident)
        → coding-squad Ralph watch loop (ADR 0012) → PR → review/auto-merge
        → incident issues also feed the feature council (ADR 0013, slow path)
```

- [ ] **Step 3: Un-defer the slow path in the phase narrative**

In `docs/phases/sre-agent.md`, find the passages that frame the SRE-to-council
signal path as "deferred" (search: `grep -ni "defer\|slow path\|council" docs/phases/sre-agent.md`).
Reword them to state the loop is built: the SRE agent stamps the `incident` label;
the council's `incidents` and `azuremonitor` sources pull incidents and telemetry
on the council's schedule (ADR 0013); recurring incidents become systemic
hardening proposals. Remove the word "deferred" from those passages. Keep the
file's existing structure; change only the deferred-framing sentences.

- [ ] **Step 4: Update README / RUNBOOK loop framing (only if present)**

Run: `grep -ni "slow path\|defer\|sre.*council\|council.*sre" README.md RUNBOOK.md`
For any hit that says the SRE-to-council loop is deferred/future, update it to
describe the now-built loop (two operational sources + the `incident` marker, ADR
0013). If neither file mentions it, skip this step.

- [ ] **Step 5: Write ADR 0013**

Create `docs/adr/0013-sre-council-feedback-loop.md`:

```markdown
# 13. SRE-to-council feedback loop via operational council sources

Date: 2026-06-20

## Status

Accepted. Builds on ADR 0009 (leverage the managed Azure SRE Agent), ADR 0011
(deliberative council), and ADR 0012 (coding-squad Ralph watch loop).

## Context

The factory could build (council) and ship (squad) but could not reflect: the
managed Azure SRE Agent observed production and filed incidents, yet nothing
carried what it learned back into the council. Phase 3 named this the slow path
and left it deferred, so recurring production faults never became systemic
hardening proposals and live telemetry never informed what the council built next.

Two hazards shaped the decision. First, the council files its own issues into the
product repo with `squad:ready`, so a naive "read the repo" source would re-ingest
council output and loop. Second, judging an incident as systemic is exactly the
synthesize-then-validate work the deliberative council already does (ADR 0011); a
lone SRE step should not make that call.

## Decision

Feed operations into the council as ordinary sources. Add two `SourceKind`s,
`INCIDENTS` and `AZUREMONITOR`, each with a source agent following the existing
fixture-plus-live backend pattern. Their `EvidenceItem`s ride the existing S1 to
S7 conveyor; synthesis, the validation jury, the confidence threshold, dry-run
governance, and FEATURE/FIX routing are unchanged (no new stage, no mini-council).

A single `incident` label is the whole SRE-to-council contract: the SRE Agent
stamps it (per the onboarding runbook), the `incidents` source pulls only issues
carrying it, and because council-filed issues carry `squad:ready` and never
`incident`, the loop cannot self-ingest. Recurrence intelligence lives in the
`incidents` backend, which collapses a repeated signature into one higher-
confidence item; the conveyor's threshold then decides. The `azuremonitor` source
pulls Application Insights telemetry scoped by `Product.azure_monitor_scope`.

The telemetry agent's live Azure access (Monitoring Reader) stays its own identity
seam, refined later; the offline fixture path is fully functional.

## Consequences

- The third loop closes: recurring incidents and telemetry become council
  proposals through the same validated pipeline, not single-agent judgments.
- `create_labels` now creates the `incident` label; the SRE runbook instructs the
  agent to stamp it; provisioning threads `azure_monitor_scope` into the registry.
- New operational sources are registry-driven (DEPLOYABLE_AGENTS + AGENT_BUILDERS);
  the scheduled sweep gathers them automatically once enabled in config.
- The live telemetry role binding is an explicit, documented follow-up, not a
  half-built feature.
```

- [ ] **Step 6: Unicode scan + commit**

Run:
```
grep -rnP "[\x{2013}\x{2018}\x{2019}\x{201C}\x{201D}\x{2026}]" \
  docs/adr/0013-sre-council-feedback-loop.md docs/adr/0009-leverage-azure-sre-agent.md \
  docs/phases/sre-agent.md feature-council/src/dsf/orchestrator/stations/s6_routing.py
```
Expected: no output.

```bash
git add docs/adr/0013-sre-council-feedback-loop.md \
  docs/adr/0009-leverage-azure-sre-agent.md docs/phases/sre-agent.md \
  feature-council/src/dsf/orchestrator/stations/s6_routing.py README.md RUNBOOK.md
git commit -m "docs(sre): record the closed SRE-to-council loop (ADR 0013) + cleanup

Co-authored-by: Copilot <223556219+Copilot@users.noreply.github.com>"
```

---

## Task 10: Full verification gauntlet

- [ ] **Step 1: Lint**

Run: `uv run ruff check .`
Expected: `All checks passed!`

- [ ] **Step 2: Full test suite**

Run: `uv run pytest -q`
Expected: all green (the prior baseline was 432 passed; this adds roughly 25-30
tests across the two agents, provisioning, registry parity, and the e2e).

- [ ] **Step 3: Import-contract / parity confirmation**

Run: `uv run pytest feature-council/tests/agents/test_registry.py -q`
Expected: PASS — `DEPLOYABLE_AGENTS` keys equal `{k.value.lower() for k in SourceKind}`
(now including `incidents` and `azuremonitor`), and every registered app imports.

- [ ] **Step 4: Final unicode sweep across all changed files**

Run:
```
git diff --name-only main...HEAD | xargs grep -lP "[\x{2013}\x{2018}\x{2019}\x{201C}\x{201D}\x{2026}]" 2>/dev/null || echo "clean"
```
Expected: `clean`.

If everything is green, the loop is closed. Do NOT push to origin; the branch
`feat/sre-council-feedback-loop` is finished via the finishing-a-development-branch
skill (operator chooses merge/PR/cleanup).

---

## Self-Review notes (for the plan author; not execution steps)

- **Spec coverage:** Section 3 enum/agents -> Tasks 2,3; 3.1 incidents + recurrence
  -> Tasks 2,4; 3.2 azuremonitor + `azure_monitor_scope` -> Tasks 3,7; section 4
  loop-safety (label breaks cycle, dedup) -> Task 1 (constant) + Task 8 (e2e) +
  the design's reliance on existing dedup (no code change needed; existing S5
  behavior); section 5 provisioning/runbook/registry-driven -> Tasks 5,6,7; section
  6 deferred seam -> documented in azuremonitor `__init__`/client + ADR (Tasks 3,9);
  section 7 doc cleanup -> Task 9; section 8 testing -> Tasks 2-4,8 + Task 10
  gauntlet; section 9 research grounding -> ADR context (Task 9). No gaps.
- **No new conveyor stage** anywhere (Approach A honored).
- **Type/name consistency:** backend classes `IncidentsFixtureBackend`/
  `IncidentsGitHubBackend`, `AzureMonitorFixtureBackend`/`AzureMonitorBackend`;
  factories `build_agent`; clients `build_incidents_client_from_env` /
  `build_azure_monitor_client_from_env`; constants `INCIDENT_LABEL(_COLOR/_DESCRIPTION)`;
  field `azure_monitor_scope`; enum `INCIDENTS`/`AZUREMONITOR`. Used consistently
  across tasks.
