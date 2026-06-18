# SP4 — Coding-squad handoff hardening (implementation plan)

Spec: `docs/superpowers/specs/2026-06-18-sp4-coding-squad-handoff-design.md`
Branch: `docs/factory-template-charter`. Each task: TDD → targeted tests green → `ruff` → commit (+ Co-authored-by trailer). Final task runs full suite + eval gate + offline dry-run.

Verified commands: `uv run pytest -q`; `uv run ruff check .`; `uv run python -m dsf.evals.runner --gate`; dry-run `uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json`. (`make` not on PATH; `python` → `uv run python`.)

---

## Task 1 — Handoff contract constant

**New:** `src/dsf/contracts/handoff.py`
```python
"""The council→squad handoff contract: one well-known label."""

from __future__ import annotations

HANDOFF_LABEL = "squad:ready"
HANDOFF_LABEL_DESCRIPTION = "Council-filed issue ready for coding-squad triage"
HANDOFF_LABEL_COLOR = "1d76db"

__all__ = ["HANDOFF_LABEL", "HANDOFF_LABEL_DESCRIPTION", "HANDOFF_LABEL_COLOR"]
```
No behavior yet. `ruff` clean. Commit: `feat(contracts): add council→squad handoff label constant (SP4)`.

## Task 2 — S6 routing stamps the handoff label (TDD)

**Test (`tests/orchestrator/test_s6_routing.py` or existing S6 test):** a routed issue's labels include `HANDOFF_LABEL` in addition to type+severity; ordering: descriptive labels first, handoff last.

**Impl:** in `s6_routing._labels_for`, append `HANDOFF_LABEL` after the severity label. Import from `dsf.contracts.handoff`.

Targeted S6 tests green; `ruff` clean. Commit: `feat(routing): stamp handoff label on every routed issue (SP4)`.

## Task 3 — Provisioner: `create_labels` + `squad_triage` steps (TDD)

**Test (`tests/instance/test_provisioner.py`):**
- step order is now `create_repo, create_labels, squad_init, squad_copilot, squad_triage, create_resource_group, provision_azure, deploy_council, deploy_sre, write_config`.
- `create_labels` carries one `gh label create <name> --force ...` per taxonomy label (type+area+severity) **plus** the handoff label (with `--description` + `--color`), in `step.commands`.
- `squad_triage` `command == ["squad", "triage", "--execute", "--label", "squad:ready"]`, `cwd == repo_dir`.
- `apply(execute=True)` with a fake runner: the runner receives each `gh label create` command and the `squad triage` command. `apply(execute=False)` records `dry-run`/`would …` without invoking those.

**Impl:**
1. `ProvisionStep` (`src/dsf/instance/spec.py`): add `commands: list[list[str]] = Field(default_factory=list)`.
2. `provisioner.plan()`: insert `create_labels` (after `create_repo`) building `commands` from `self.spec.label_taxonomy` values + the handoff label; insert `squad_triage` (after `squad_copilot`) with the triage command + `cwd=repo_dir`.
3. `provisioner.apply()`: in the execute branch, when `step.commands` is set, run each in order (`cwd` honored) and mark executed/`"executed"`; in the non-execute branch a `commands` step records `"dry-run"`. Keep single-`command` behavior intact.

Targeted provisioner tests green; `ruff` clean. Commit: `feat(provisioner): create repo labels + wire squad triage to handoff (SP4)`.

## Task 4 — Closed-loop test (TDD)

**Test (`tests/learning/` or `tests/orchestrator/`):** with `build_services("local")`, run S6 on an accepted proposal scoped to a product → the routed issue carries `HANDOFF_LABEL`; then feed a merged-PR webhook (`product:<key>`, `merged=True`) to `handle_pr_event` → `services.memory.get_lessons(product)` returns a lesson. Asserts the council half of the loop is wired end to end.

No new impl expected (verifies Tasks 2 + existing feedback-watcher). If a gap surfaces, fix minimally. `ruff` clean. Commit: `test(handoff): assert closed council↔squad knowledge loop (SP4)`.

## Task 5 — ADR 0007 + RUNBOOK + charter flip

- **New** `docs/adr/0007-council-squad-handoff.md` (Accepted): the handoff label contract, label-provisioning placement, `squad triage --execute` wiring, and the closed knowledge loop diagram.
- **RUNBOOK.md:** add a "Council → Squad handoff" section with the loop diagram + the `squad:ready` contract.
- **Charter §6:** flip the SP4 row to `| SP4 ✅ |` with `*(done — ADR 0007)*` and tighten the wording.
- **Full verification:** `uv run pytest -q` (all green), `uv run ruff check .` clean, eval gate PASSED, offline dry-run green.

Commit: `docs(adr): ADR 0007 council↔squad handoff; mark SP4 done (SP4)`.
