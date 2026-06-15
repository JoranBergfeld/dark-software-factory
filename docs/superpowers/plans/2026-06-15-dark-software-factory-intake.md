# Dark Software Factory — Intake Line Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a locally-runnable, fully-tested multi-agent feature-intake line that turns signals into grounded, deduplicated, labeled GitHub issues, with every external dependency behind a port so the whole conveyor runs end-to-end in dry-run with no Azure/LLM.

**Architecture:** Hybrid conveyor (deterministic stations + agentic investigation/council workcells). One Python package `dsf` with focused modules; each source agent is a thin A2A service entrypoint (own Dockerfile = own container) over shared code. All I/O (model, memory, config, github, source backends) behind ports with in-memory fakes + Azure impls.

**Tech Stack:** Python 3.12, `uv`, `pydantic` v2, `fastapi`/`uvicorn`, `httpx`, `pytest`, `ruff`, `jinja2`; Bicep/`azd` for IaC. Microsoft Agent Framework + Azure OpenAI behind the `ModelClient` port (fake = deterministic, no LLM needed for dry-run).

**Critical principle:** Dry-run E2E must pass with `pytest` and `python -m dsf.cli run --dry-run` using only in-memory ports. Azure impls + IaC are authored but never invoked autonomously.

---

## File structure (locked)

```
pyproject.toml, README.md, .env.example, Makefile
src/dsf/
  contracts/        models.py (pydantic), schemas/ (generated JSON Schema), enums.py
  ports/            __init__.py defining Protocols: ModelClient, MemoryStore, ConfigStore,
                    GitHubClient, SourceBackend, Tracer
  fakes/            in-memory implementations of every port (deterministic)
  config/           feature flags + product registry loader; ConfigStore impls (memory, appconfig)
  memory/           store.py (working+long-term tiers), dedup.py, consolidation.py; impls (memory, cosmos)
  a2a/              server.py (FastAPI agent scaffold), client.py, card.py, envelope.py, auth.py
  agents/           base.py (SourceAgent), sentry/, grafana/, foundryiq/, webiq/, tickets/  (each: backend.py real+fake, main.py entrypoint, Dockerfile)
  council/          synthesizer.py, critics/ (grounding, value, duplication, feasibility, strategic_fit, cost, security), decision.py
  orchestrator/     conveyor.py, blackboard.py, stations/ (s1_triage..s7_filing)
  triggers/         scheduler.py, ingestion.py (webhook->event), debounce.py
  learning/         feedback_watcher.py, lessons.py, calibration.py
  evals/            golden/ (datasets), evaluators.py, runner.py
  observability/    tracing.py, grafana/ (dashboard.json)
  control_center/   app.py (FastAPI), templates/, static/
  cli.py            `dsf run|sweep|serve-agent|control-center`
infra/              main.bicep, modules/, azure.yaml (azd), compose.homelab.yml
tests/              mirrors src; tests/e2e/test_dry_run_line.py
.github/workflows/  ci.yml (lint, test, evals gate)
```

---

## Phase 0 — Foundation (serial; do first, lock the shared backbone)

### Task 0.1: Project scaffold & tooling
**Files:** Create `pyproject.toml`, `Makefile`, `.env.example`, `src/dsf/__init__.py`, `tests/__init__.py`, `.github/workflows/ci.yml`.
- [ ] Create `pyproject.toml` (project `dsf`, deps: pydantic>=2, fastapi, uvicorn, httpx, jinja2, jsonschema, pytest, pytest-asyncio, ruff; `[tool.ruff]` line-length 100; `[tool.pytest.ini_options] asyncio_mode="auto"`). src layout.
- [ ] `Makefile` targets: `install` (`uv venv && uv pip install -e ".[dev]"`), `test` (`pytest -q`), `lint` (`ruff check .`), `dryrun` (`python -m dsf.cli run --dry-run --signal tests/fixtures/sample_signal.json`).
- [ ] `ci.yml`: matrix py3.12 → install, ruff, pytest, then `python -m dsf.evals.runner --gate`.
- [ ] Verify: `uv venv && uv pip install -e ".[dev]" && pytest -q` (0 tests, exits 0). Commit `chore: project scaffold`.

### Task 0.2: Contracts (the shared backbone — must be stable)
**Files:** Create `src/dsf/contracts/enums.py`, `src/dsf/contracts/models.py`, `tests/contracts/test_models.py`, `src/dsf/contracts/export_schema.py`.
Define (pydantic v2 `BaseModel`, all with `id: str`, `created_at: datetime`):
- `enums.py`: `Severity(LOW/MEDIUM/HIGH/CRITICAL)`, `SourceKind(SENTRY/GRAFANA/FOUNDRYIQ/WEBIQ/TICKETS)`, `ProposalKind(FEATURE/FIX)`, `RunStatus(OPEN/INVESTIGATING/SYNTHESIZING/GROUNDING/COUNCIL/ROUTING/FILED/KILLED/ERROR)`, `Verdict(ACCEPT/KILL)`, `TriggerKind(SCHEDULED/SIGNAL)`.
- `Provenance{timestamp:datetime, query_used:str, source_kind:SourceKind}`
- `EvidenceItem{id, source_agent:str, claim:str, raw_citation:str, provenance:Provenance, confidence:float, product_hints:list[str]}`
- `Run{id, trigger:TriggerKind, status:RunStatus, scope_product_hints:list[str], source_kinds:list[SourceKind], signal_payload:dict, evidence:list[EvidenceItem]=[], proposals:list[str]=[], dry_run:bool=False, audit:list[AuditRecord]=[]}`
- `AuditRecord{id, station:str, message:str, created_at}`
- `Proposal{id, run_id, kind:ProposalKind, title:str, problem:str, proposed_change:str, product:str|None, evidence_ids:list[str], confidence:float}`
- `CriticScore{critic:str, score:float, veto:bool, rationale:str}`
- `CouncilVerdict{id, proposal_id, verdict:Verdict, weighted_score:float, threshold:float, scores:list[CriticScore], rationale:str}`
- `RoutedIssue{id, proposal_id, product:str, repo:str, title:str, body:str, labels:list[str], filed_url:str|None}`
- [ ] **Test first** `tests/contracts/test_models.py`: round-trip `EvidenceItem` requires non-empty `raw_citation` (add field_validator: empty raw_citation raises); `Run` defaults; `CouncilVerdict.verdict==KILL` when any score.veto.
- [ ] Run → fails. Implement models. Run → pass.
- [ ] `export_schema.py`: writes `contracts/schemas/<Model>.json` for each top-level model via `model_json_schema()`. Test asserts files generated. Commit `feat: core contracts`.

### Task 0.3: Ports (Protocols) + fakes
**Files:** Create `src/dsf/ports/__init__.py`, `src/dsf/fakes/*.py`, `tests/fakes/test_fakes.py`.
Define `typing.Protocol`s (all async where I/O):
- `ModelClient.complete(system:str, prompt:str, schema:type[BaseModel]|None) -> BaseModel|str` — fake returns deterministic output driven by a registered handler keyed on a tag in the prompt (so council/synth get canned structured results in dry-run).
- `MemoryStore`: `put_working(key,value,ttl)`, `get_working(key)`, `put_record(record)`, `query_similar(text, kind, k) -> list[dict]`, `put_lesson(lesson)`, `get_lessons(product, k)`. Fake = dicts + naive token-overlap similarity (no embeddings).
- `ConfigStore`: `is_enabled(flag, product=None)->bool`, `get_value(key, default)`, `set_flag(flag,bool,product=None)`, `snapshot()->dict`. Fake = in-memory dict seeded from `config/defaults.json`.
- `GitHubClient.create_issue(repo,title,body,labels)->str(url)`; fake records calls, returns `local://issue/<n>`, never network.
- `SourceBackend.gather(run_scope)->list[EvidenceItem]`; per-agent fakes return fixture evidence.
- `Tracer.span(name, **attrs)` contextmanager; fake = no-op recorder.
- [ ] Test fakes satisfy protocols + deterministic outputs. Commit `feat: ports and in-memory fakes`.

### Task 0.4: App container / wiring + CLI skeleton
**Files:** Create `src/dsf/container.py` (builds a `Services` dataclass selecting fake vs azure impls by env `DSF_MODE=local|azure`), `src/dsf/cli.py`, `tests/test_container.py`.
- [ ] `Services` exposes all ports; `build_services(mode)` returns fakes for `local`. CLI `run/sweep/serve-agent/control-center` parse args; `run --dry-run` loads a signal json. Test: `build_services("local")` returns wired fakes. Commit `feat: service container + CLI skeleton`.

---

## Phase 1 — Memory & Config (parallel after Phase 0)

### Task 1.1: Memory tiers, dedup, consolidation
**Files:** `src/dsf/memory/store.py` (wraps MemoryStore port with tier helpers), `dedup.py` (`is_duplicate(text, store, threshold)`), `consolidation.py` (`consolidate_run(run, verdict, store)` writes record + lesson), tests in `tests/memory/`.
- [ ] Tests: working put/get with TTL expiry (fake clock injected); `is_duplicate` returns True when similar record exists; `consolidate_run` writes a long-term record + a Lesson retrievable by product. Implement. Commit.

### Task 1.2: Config: feature flags + Product Registry
**Files:** `src/dsf/config/flags.py` (typed accessors: `critic_enabled(name,product)`, `agent_enabled(kind)`, `triggers_paused(kind)`, `dry_run_global()`, `threshold(product)`, `weights(product)`), `src/dsf/config/registry.py` (`Product` model + `load_registry()` from `config/products.json`), `config/defaults.json`, `config/products.json` (2 seed products), tests.
- [ ] `Product{key, github_repo, label_taxonomy:dict, foundryiq_scope, sentry_projects:list, grafana_dashboards:list, confidence_threshold:float}`. Tests: disabled critic flag respected; registry routing lookup by product hint. Commit.

---

## Phase 2 — A2A + Source Agents (parallel; one subagent per agent)

### Task 2.0: Shared A2A library
**Files:** `src/dsf/a2a/card.py` (`AgentCard` model: name, kind, endpoint, capabilities, enabled), `envelope.py` (`A2ARequest{run_scope}`, `A2AResponse{evidence:list[EvidenceItem], degraded:bool, error:str|None}`), `server.py` (`make_agent_app(agent)` → FastAPI with `GET /card`, `POST /gather`, bearer auth dep), `client.py` (`async gather(endpoint, scope, token, timeout)` with timeout→degraded), `auth.py`, tests.
- [ ] Tests: app serves card; `/gather` returns evidence; missing/blank bearer → 401; client timeout → `A2AResponse(degraded=True)`. Commit.

### Task 2.1: Base SourceAgent + agent template
**Files:** `src/dsf/agents/base.py` (`SourceAgent{kind, backend:SourceBackend}` with `gather(scope)` honoring `agent_enabled` flag → empty+degraded if disabled), tests.
- [ ] Test: disabled agent returns degraded empty; enabled delegates to backend. Commit.

### Tasks 2.2–2.5: Sentry / Grafana / FoundryIQ / WebIQ agents (one each)
**Files per agent `src/dsf/agents/<x>/`:** `backend.py` (`<X>FakeBackend` returns fixture `EvidenceItem`s from `tests/fixtures/<x>_*.json`; `<X>McpBackend` calls the real MCP/SDK behind the same interface — implemented but selected only in azure mode), `main.py` (`app = make_agent_app(SourceAgent(kind, backend))`), `Dockerfile`, fixtures, tests.
- [ ] For each: test fake backend yields ≥1 well-formed `EvidenceItem` with non-empty `raw_citation` and correct `source_agent`. MCP backend: structure the call + map response → EvidenceItem; guard so it is never invoked in local mode (no creds). Each agent gets its own `Dockerfile` (`CMD uvicorn dsf.agents.<x>.main:app`). Commit per agent.

### Task 2.6: Tickets stub agent
**Files:** `src/dsf/agents/tickets/` backend raises `NotImplementedError` in azure mode, fake returns []; documented as stub.
- [ ] Test: contract present, fake returns []. Commit.

---

## Phase 3 — Council (parallel after Phase 0/1)

### Task 3.1: Synthesizer
**Files:** `src/dsf/council/synthesizer.py` (`synthesize(run, store, model)->list[Proposal]`: retrieves lessons, clusters evidence by product_hint, asks ModelClient for proposal(s); fake model returns deterministic proposal from evidence), tests.
- [ ] Test: given 2 evidence items sharing a product hint → ≥1 Proposal whose `evidence_ids` ⊆ run evidence and `product` set. Commit.

### Tasks 3.2–3.8: Seven critics
**Files:** `src/dsf/council/critics/<name>.py` each exposing `evaluate(proposal, run, services)->CriticScore`. Behaviors:
- grounding: veto if any proposal claim not traceable to evidence_ids.
- value: score from evidence count/severity.
- duplication: veto if `is_duplicate` against memory.
- feasibility: score down for oversized scope (heuristic on proposed_change length/keywords).
- strategic_fit: score from FoundryIQ lessons/registry roadmap hint.
- cost: score inverse to estimated effort heuristic.
- security: veto on flagged content keywords (deterministic list) in dry-run.
- [ ] Each critic: unit test for its veto/score path. Each respects `critic_enabled(name, product)` (skipped if disabled). Commit per critic.

### Task 3.9: Decision engine
**Files:** `src/dsf/council/decision.py` (`decide(proposal, run, services)->CouncilVerdict`: run enabled critics, any veto→KILL, else weighted_score vs `threshold(product)`), tests.
- [ ] Tests: a veto → KILL with rationale; all-pass above threshold → ACCEPT; below → KILL. Disabled critic excluded from weights. Commit.

---

## Phase 4 — Orchestrator / Conveyor (after Phases 1–3)

### Task 4.1: Blackboard
**Files:** `src/dsf/orchestrator/blackboard.py` (persist/load `Run` via MemoryStore working tier; append audit; idempotent station checkpoint markers), tests.
- [ ] Test: save→load run; checkpoint prevents re-running a completed station. Commit.

### Tasks 4.2–4.8: Stations S1–S7
**Files:** `src/dsf/orchestrator/stations/sN_*.py`, each `async run(run, services)->Run`:
- S1 triage: scope hints from registry, debounce via memory (duplicate in-flight → KILLED).
- S2 investigation: parallel A2A `client.gather` to enabled agents (in local mode use in-process agent apps via httpx ASGITransport or direct backend calls); collect evidence, mark degraded sources in audit.
- S3 synthesis → proposals.
- S4 grounding gate: strip ungrounded claims; kill unsupported proposals.
- S5 council: `decide` per proposal; drop KILLs (logged).
- S6 routing: map product→repo+labels from registry.
- S7 filing: final dedup; if `dry_run` or `dry_run_global()` → record intended issue, do NOT call GitHubClient; else `create_issue`. Write RoutedIssue + url.
- [ ] Each station: unit test with fakes. Commit per station.

### Task 4.9: Conveyor driver
**Files:** `src/dsf/orchestrator/conveyor.py` (`async run_line(run, services)` sequences S1–S7, persisting after each, catching per-station errors → status ERROR + audit, resumable), tests.
- [ ] Test: full sequence advances a seeded run to FILED (dry-run, no real issue). Commit.

---

## Phase 5 — Triggers & ingestion

### Task 5.1: Ingestion + debounce
**Files:** `src/dsf/triggers/ingestion.py` (`signal_to_run(payload)->Run` with `TriggerKind.SIGNAL`), `debounce.py` (suppress repeat signals within window via memory), `scheduler.py` (`sweep(services)->list[Run]` builds a SCHEDULED run across enabled sources), tests.
- [ ] Tests: duplicate signal within window suppressed; `triggers_paused(SIGNAL)` honored; sweep respects paused scheduled. Commit.

### Task 5.2: Ingestion HTTP endpoint
**Files:** add `POST /ingest` to a small `dsf.triggers.app` FastAPI (Event Grid / webhook shaped), test with TestClient.
- [ ] Test: posting a sample Sentry alert creates+runs a dry-run line. Commit.

---

## Phase 6 — Learning loop

### Task 6.1: PR feedback watcher + lessons + calibration
**Files:** `src/dsf/learning/feedback_watcher.py` (`handle_pr_event(event, services)`: parse verdict (approved/closed) + proposed-vs-final spec diff → `record_outcome`), `lessons.py` (`outcome_to_lesson`), `calibration.py` (`recompute_weights(outcomes)`), tests.
- [ ] Tests: an approved PR event → lesson stored + outcome record; a rejected event → lesson with negative signal; calibration shifts a weight given correlated critic. Commit.

---

## Phase 7 — Control Center UI (v1 must-have)

### Task 7.1: Control Center web app
**Files:** `src/dsf/control_center/app.py` (FastAPI + Jinja), `templates/index.html`, `static/app.css`, tests.
Pages/endpoints: dashboard of current flags (critics per product, agents, trigger pause, dry-run switch), POST toggles → `ConfigStore.set_flag`; thresholds/weights view with calibration proposals (accept→set_value); read from `snapshot()`.
- [ ] Tests (TestClient): GET renders current flags; POST toggling a critic flips `is_enabled`; POST dry-run switch sets global flag. Commit.

---

## Phase 8 — Evals (CI gate)

### Task 8.1: Golden set + evaluators + runner
**Files:** `src/dsf/evals/golden/*.json` (≥5 cases: signal → expected product, expected verdict, must-be-grounded), `evaluators.py` (`groundedness`, `routing_accuracy`, `verdict_match`), `runner.py` (`--gate` runs line in dry-run over golden set, fails CI if metrics below thresholds), tests.
- [ ] Tests: runner over golden set returns metrics; `--gate` exits non-zero on injected regression. Commit.

---

## Phase 9 — Observability

### Task 9.1: Tracing wiring + Grafana dashboard
**Files:** `src/dsf/observability/tracing.py` (Tracer azure impl = OpenTelemetry GenAI spans behind port; local = recorder), wire `tracer.span` into conveyor stations, `observability/grafana/dashboard.json` (panels per design 6.2), test that stations emit spans via fake tracer.
- [ ] Test: running the line records spans for each station. Commit.

---

## Phase 10 — Infrastructure (authored, not invoked)

### Task 10.1: Bicep/azd + homelab compose
**Files:** `infra/azure.yaml` (azd), `infra/main.bicep` + `modules/` (Container Apps env + apps for orchestrator/agents/control-center/ingestion, Cosmos, App Configuration, Key Vault, App Insights, Foundry/AOAI ref, managed identity, RBAC), `infra/compose.homelab.yml` (grafana agent + tunnel sidecar), `.env.example`.
- [ ] `az bicep build infra/main.bicep` succeeds (lint only; no deploy). Commit. (If `az` unavailable, validate via bicep schema MCP and note in report.)

---

## Phase 11 — End-to-end & docs

### Task 11.1: E2E dry-run test + runbook
**Files:** `tests/e2e/test_dry_run_line.py` (build local services, ingest sample signal, assert run reaches FILED with a RoutedIssue, GitHubClient fake never called for real network, grounding enforced, ≥1 audit per station), `docs/RUNBOOK.md` (how to run locally, dry-run, control center, what's stubbed vs real, deploy steps for when awake), update README.
- [ ] Test passes. `python -m dsf.cli run --dry-run` works. Commit. Final `docs/adr/` entries for key choices (single-package, ports, Cosmos-vector, A2A-tunnel).

---

## Self-review notes
- Every spec section maps to a phase: §4 stations→Ph4; §5 agents/contracts→Ph0/2; §5.2 council→Ph3; §6 obs/evals/learning→Ph6/8/9; §7 control center/memory→Ph1/7; §8 infra→Ph10; §9 guardrails→stations+config; §10 security→a2a auth+keyvault(infra); §11 testing→every task + Ph11.
- No placeholders: each task names files, signatures, and its test's asserted behavior.
- Type consistency: model/field names above are the single source; subagents import from `dsf.contracts`.
- "Runnable without cloud": guaranteed by ports+fakes and the Ph11 E2E gate.
```
