# SP5 — SRE agent (implementation plan)

Spec: `docs/superpowers/specs/2026-06-18-sp5-sre-agent-design.md`
Branch: `docs/factory-template-charter`. Each task: TDD → targeted tests green → `ruff` → commit (+ Co-authored-by). Final task runs full suite + eval gate + offline dry-run.

Verified commands: `uv run pytest -q`; `uv run ruff check .`; `uv run python -m dsf.evals.runner --gate`; dry-run `uv run dsfctl run --dry-run --signal tests/fixtures/sample_signal.json`. (`python` → `uv run python`.)

Self-referential return annotations must be **unquoted** (`from __future__ import annotations` ⇒ UP037).

---

## Task 1 — SRE models + label

**New `src/dsf/sre/__init__.py`, `src/dsf/sre/models.py`:**
- `SRE_LABEL = "sre"`.
- `Incident(BaseModel)`: `product: str`, `title: str`, `summary: str`, `severity: str`, `citations: list[str]`, `source_kinds: list[str]`, `fingerprint: str`.
- `SreSweepResult(BaseModel)`: `observed: int`, `incidents: int`, `filed: list[str]`, `duplicates: int`, `dry_run: bool`.

No behavior. `ruff` clean. Commit: `feat(sre): Incident + sweep-result models and SRE label (SP5)`.

## Task 2 — `detect_incidents` (TDD)

**Test `tests/sre/test_detect.py`:** evidence below threshold dropped; severity mapping (0.9→sev-critical … <0.5 dropped); grouped by first `product_hint`; identical (product, claim) ⇒ identical fingerprint; different claim ⇒ different fingerprint; evidence with no product hint is skipped.

**Impl `src/dsf/sre/detect.py`:** `detect_incidents(evidence, *, threshold=0.7) -> list[Incident]`. Severity tiers by confidence (reuse the S6 idea: ≥0.85 critical, ≥0.7 high, else medium/low). `fingerprint = sha1(f"{product}|{claim.lower().strip()}").hexdigest()[:16]`. `source_kinds`/`citations` from the evidence item(s).

Targeted tests green; `ruff` clean. Commit: `feat(sre): detect incidents from evidence above a confidence threshold (SP5)`.

## Task 3 — `SreAgent` observe / fix_forward / reflect / sweep (TDD)

**Test `tests/sre/test_agent.py`** (use `build_services("local")` + `RecordingGitHubClient`, registry product `microbi`):
- `observe`: concatenates two fixture backends' evidence; a backend raising is skipped (degrade), not fatal.
- `fix_forward(incident, dry_run=False)`: `RecordingGitHubClient.calls[0]` repo = product's `github_repo`, labels include `HANDOFF_LABEL`, `SRE_LABEL`, and the severity; returns a url.
- dedup: a second `fix_forward` of the same fingerprint files nothing, returns `None`.
- `fix_forward(dry_run=True)`: no github call, returns `None`, fingerprint **not** indexed (a subsequent real call files).
- `reflect`: `services.memory.get_lessons(product)` returns a lesson with the incident summary.
- `sweep(scope)`: observe→detect→fix_forward+reflect; `SreSweepResult.filed` non-empty on first run, empty (all duplicates) on the second.

**Impl `src/dsf/sre/agent.py`:** `SreAgent(github, memory, config, backends, *, registry=None)`.
- `observe(scope)`: gather each backend, concatenate; `except Exception` → log + skip (mirror `SourceAgent.gather`).
- `_repo_for(product)`: `route_product([product], registry or load_registry())`.
- `fix_forward(incident, *, dry_run)`: dedup via `memory.get_working(f"sre:fp:{fp}")`; resolve repo (skip + audit if unknown); if not dry_run → `github.create_issue(repo, title, body, [severity, SRE_LABEL, HANDOFF_LABEL])` then `memory.put_working(f"sre:fp:{fp}", True)`; return url or None.
- `reflect(incident, action)`: `memory.put_record({"kind": "sre_incident", ...})` + `memory.put_lesson({...product, signal:"sre:filed|dup|dry_run", text})`.
- `sweep(scope, *, dry_run=False)`: drive the loop, return `SreSweepResult`.

Targeted tests green; `ruff` clean. Commit: `feat(sre): SreAgent observe/fix-forward/reflect/sweep over the squad handoff (SP5)`.

## Task 4 — Wiring + entrypoint (TDD)

**Test `tests/sre/test_wiring.py`:** `build_sre_agent(build_services("local"))` defaults to `[SentryFixtureBackend, GrafanaFixtureBackend]`; `run_sweep` on local services returns a `SreSweepResult` with `observed > 0`.

**Impl:** `src/dsf/sre/wiring.py` `build_sre_agent(services, *, backends=None)`; `src/dsf/sre/main.py` `async run_sweep(services, scope=None, *, dry_run=False)`. Export from `__init__`.

Targeted tests green; `ruff` clean. Commit: `feat(sre): build_sre_agent wiring + run_sweep entrypoint (SP5)`.

## Task 5 — `dsfctl sre-sweep` CLI (TDD)

**Test `tests/cli/test_control.py`** (or existing control-CLI test): parser has `sre-sweep`; `--dry-run`/`--product` parsed; dispatch calls the sweep and prints a summary (monkeypatch `run_sweep` to a stub; assert no real filing).

**Impl `src/dsf/cli/control.py`:** `_cmd_sre_sweep` builds services, runs `run_sweep(services, scope, dry_run=...)`, prints `observed/incidents/filed/duplicates`. Register `sre-sweep` subparser with `--dry-run` + `--product`.

Targeted tests green; `ruff` clean. Commit: `feat(cli): dsfctl sre-sweep runs one SRE sweep (SP5)`.

## Task 6 — Provisioner: complete `deploy_sre` (TDD)

**Test `tests/instance/`:** `deploy_sre` is no longer in the deferred set; dry-run apply → `results["deploy_sre"] == "rendered (dry-run)"` and the SRE descriptor file exists; execute apply → fake runner sees `["az","containerapp","update","--resource-group",rg,"--name","dsf-sre-<product>","--image",image]` and `results["deploy_sre"] == "deployed"`. Add a `render_sre_bundle` unit test (writes `sre.containerapp.yaml`).

**Impl:**
1. `src/dsf/instance/runtime_render.py`: `render_sre_bundle(manifest, *, repo_root=None)` writing `config/instances/<product>.runtime/sre.containerapp.yaml` (app `dsf-sre-<product>`, image, `DSF_MODE=azure`, `DSF_PRODUCT`).
2. `src/dsf/instance/provisioner.py`: drop `deferred=True` from `deploy_sre`; add an `elif step.name == "deploy_sre":` branch mirroring `deploy_council` (render → "rendered (dry-run)" / `az containerapp update --name dsf-sre-<product>` → "deployed").
3. Update the `deploy_sre` description (no longer "deferred to SP5").
4. Update `test_plan_deferred_flags` (now `deferred == set()`), and the CLI help string in `cli/factory.py` that says "SRE deploy stays deferred".

Targeted tests green; `ruff` clean. Commit: `feat(provisioner): render + deploy the SRE runtime (un-defer deploy_sre) (SP5)`.

## Task 7 — ADR 0008 + RUNBOOK + charter flip + full verification

- **New** `docs/adr/0008-sre-agent.md` (Accepted): fast-path-only SRE, reuse of Sentry/Grafana backends + the SP4 handoff label, dedup/reflection, deferred council slow-path, `deploy_sre` symmetry with council.
- **RUNBOOK.md:** "SRE agent" section (sweep command + fix-forward loop diagram).
- **Charter §6:** flip SP5 row `| SP5 ✅ |` `*(done — ADR 0008)*`; if all SP rows are ✅, add a closing note.
- **Full verification:** `uv run pytest -q` green; `uv run ruff check .` clean; eval gate PASSED; offline dry-run green.

Commit: `docs(adr): ADR 0008 SRE agent; mark SP5 done (SP5)`.
