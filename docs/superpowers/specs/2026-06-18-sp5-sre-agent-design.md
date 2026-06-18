# SP5 — SRE agent (design)

- Date: 2026-06-18
- Status: Proposed
- Charter: §6 SP5 ("Observe prod (reuse Sentry/Grafana backends) → fix-forward to Squad → (later) signals to council → self-reflection").
- Builds on: the Sentry/Grafana `SourceBackend`s (`src/dsf/agents/`), SP4's `HANDOFF_LABEL` handoff contract, the product registry (S6 routing), the `GitHubClient` port, and the `MemoryStore` learning loop.

## Problem

The factory observes production telemetry through the Sentry and Grafana source
backends, but nothing turns a production incident into action. The charter's SRE
agent closes that gap with a **fast path**: observe prod → detect actionable
incidents → **fix-forward** them straight into the coding squad as GitHub issues
(carrying the SP4 handoff label) → reflect on what it did. The provisioner's
`deploy_sre` step is a hard-deferred stub ("deferred to SP5").

The council's *slow path* (SRE emitting operational signals into the feature
council) is explicitly **later** per the charter and is out of scope here.

## Goals / non-goals

**Goals**
- An `SreAgent` that **reuses** the existing Sentry/Grafana backends to observe
  prod, **detects** incidents above a confidence threshold, **fix-forwards** each
  to the right product repo as a squad-triageable issue, and **reflects** (record
  + product lesson) — all offline-testable, dry-run-safe.
- A runnable entrypoint (`dsfctl sre-sweep`) and a completed provisioner story
  (`deploy_sre` renders an SRE runtime descriptor, deploys on `--execute`,
  mirroring `deploy_council`).

**Non-goals**
- The slow path (SRE → council signals): deferred (charter "later").
- Live Sentry/Grafana/GitHub calls in tests (backends/clients are injected; the
  suite stays offline — ADR 0001/0006).
- Auto-remediation / writing code: the SRE agent *files issues*; the squad's
  Copilot coding agent implements them (SP4 handoff).

## Design

### Observe → detect → fix-forward → reflect

```
SreAgent.sweep(scope):
  evidence  = observe(scope)        # gather from Sentry + Grafana backends (degrade-safe)
  incidents = detect(evidence)      # threshold + group by product + severity + fingerprint
  for incident in incidents:
      action = fix_forward(incident)  # dedup -> file issue (squad:ready) -> index fingerprint
      reflect(incident, action)       # durable record + product-scoped lesson
  return SreSweepResult(...)
```

- **`observe(scope)`** calls each injected `SourceBackend.gather(scope)` and
  concatenates the `EvidenceItem`s. A backend that raises is logged and skipped
  (degrade, never fabricate) — the same posture as `SourceAgent.gather`.
- **`detect(evidence)`** (`src/dsf/sre/detect.py`): keep evidence at or above a
  confidence threshold (default 0.7), group by the first `product_hint`, and emit
  one `Incident` per (product, evidence) with a **severity** derived from
  confidence (`sev-low/medium/high/critical`) and a **fingerprint** (stable hash
  of product + normalized claim) for dedup.
- **`fix_forward(incident, *, dry_run)`**: resolve the product → repo via the
  product registry (`route_product`, same as S6). Skip if the fingerprint was seen
  before (`MemoryStore` dedup, kind `sre_incident`). Otherwise file an issue via
  the `GitHubClient` port with labels `[severity, SRE_LABEL, HANDOFF_LABEL]` so the
  squad triages it exactly like a council issue; under `dry_run` record the intent
  and file nothing. The fingerprint is indexed only when an issue is actually
  filed, so a dry-run preview never suppresses a later real sweep.
- **`reflect(incident, action)`**: write a durable `sre_incident` record and a
  product-scoped Lesson (`MemoryStore.put_lesson`) capturing what was observed and
  whether it was filed/duplicate/dry-run — the SRE's self-reflection store.

### The handoff label is reused, not reinvented

SRE-filed issues carry SP4's `HANDOFF_LABEL` (`squad:ready`) plus a new
`SRE_LABEL` (`sre`) marker, so the *same* `squad triage` picks them up. The
council and the SRE agent are two producers into one squad intake — the handoff
contract already supports this.

### Wiring (mode-aware, offline by default)

`build_sre_agent(services, *, backends=None)` (`src/dsf/sre/wiring.py`):
- `backends` defaults to `[SentryFixtureBackend(), GrafanaFixtureBackend()]`
  (local/offline). In azure mode the caller injects the MCP backends.
- Pulls `github` + `memory` + `config` from the passed `Services` bundle.

`dsfctl sre-sweep [--dry-run] [--product P]` builds services and runs one sweep,
printing a compact summary (mirrors `dsfctl sweep`).

### Provisioner: complete `deploy_sre`

Flip `deploy_sre` from `deferred=True` to a real step mirroring `deploy_council`:
render an SRE runtime descriptor (`render_sre_bundle` in `instance/runtime_render.py`
→ `config/instances/<product>.runtime/sre.containerapp.yaml`), record
"rendered (dry-run)" under preview and run `az containerapp update --name
dsf-sre-<product> --image <runtime_image>` under `--execute`. Same offline posture
as the council: rendering is pure; the deploy is gated behind `--execute`.

## Components

| Unit | Change | Boundary |
|---|---|---|
| `src/dsf/sre/models.py` (new) | `Incident`, `SreSweepResult`, `SRE_LABEL` | runtime |
| `src/dsf/sre/detect.py` (new) | `detect_incidents(evidence, *, threshold)` | runtime |
| `src/dsf/sre/agent.py` (new) | `SreAgent` (observe/fix_forward/reflect/sweep) | runtime |
| `src/dsf/sre/wiring.py` (new) | `build_sre_agent(services, *, backends=None)` | runtime |
| `src/dsf/sre/main.py` (new) | `run_sweep(services, scope, *, dry_run)` entrypoint | runtime |
| `src/dsf/cli/control.py` | `sre-sweep` subcommand | runtime CLI |
| `src/dsf/instance/runtime_render.py` | `render_sre_bundle` | instance |
| `src/dsf/instance/provisioner.py` | un-defer + wire `deploy_sre` | instance |
| `docs/adr/0008-sre-agent.md`, RUNBOOK, charter | record + flip SP5 ✅ | docs |

## Testing (all offline)

- **detect:** threshold filtering; severity mapping; per-product grouping; stable
  fingerprint for identical claims.
- **observe:** concatenates Sentry+Grafana fixture evidence; a raising backend is
  skipped, not fatal.
- **fix_forward:** files via a `RecordingGitHubClient` with `HANDOFF_LABEL` +
  `SRE_LABEL` + severity; dedup skips a repeat fingerprint; `dry_run=True` files
  nothing and records intent without indexing the fingerprint, so a later real
  sweep still files it (dry-run never suppresses real filing); product→repo
  routing via registry.
- **reflect:** a lesson is retrievable via `MemoryStore.get_lessons(product)`.
- **sweep:** end-to-end on fixtures yields ≥1 filed incident + lessons; a second
  sweep files nothing (dedup).
- **wiring/CLI:** `build_sre_agent` defaults to fixture backends; `sre-sweep`
  dry-run prints a summary and files nothing.
- **provisioner:** `deploy_sre` is no longer deferred; renders the SRE descriptor
  (dry-run) and issues `az containerapp update --name dsf-sre-<product>` on execute.
- Full suite + `ruff` + eval gate green; offline dry-run of the council line green.

## Decisions (resolved)

- **SRE files issues; it does not write code.** Remediation flows through the
  squad's Copilot agent via the SP4 handoff — one intake, two producers.
- **Reuse `HANDOFF_LABEL`; add only `SRE_LABEL` as a provenance marker.** No new
  triage contract.
- **Council slow-path deferred** (charter "later"); SP5 ships the fast path +
  reflection only.
- **`deploy_sre` mirrors `deploy_council`** (render pure, deploy gated by
  `--execute`) — honest and symmetric, no fabricated infra.
