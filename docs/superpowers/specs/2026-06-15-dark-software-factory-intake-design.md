# Dark Software Factory — Feature Intake Line (Design)

**Status:** Approved (2026-06-15)
**Scope:** The *intake* segment of an automated SDLC. Turns raw signals into rigorously-grounded, deduplicated, labeled GitHub issues. Ends at the (human-approved) specification PR opened downstream; everything past that is out of scope for this repository.

---

## 1. Vision

Inspired by manufacturing "dark factories" — production lines that run with minimal human intervention — the **Dark Software Factory** automates the hardest part of the SDLC: deciding *what to build*. This repository implements the **intake line**: a mostly-autonomous, multi-product pipeline that listens to operational and market signals, investigates them with rigorous grounding, subjects proposals to an adversarial critic council, and files labeled GitHub issues with a full evidence trail.

The system is **fully dark at intake**: the council files issues with no human pre-check. The *only* human gate is downstream, approving the specification PR a coding agent opens from the issue. All weight therefore rests on grounding rigor, deduplication, and a learning loop that absorbs human verdicts.

## 2. Core decisions (locked)

| Decision | Choice |
|---|---|
| Deliverable | Runnable agent system (not just blueprint) |
| Runtime | Portable containers (deploy near sources); Microsoft Agent Framework as in-container SDK |
| Coordination | A2A (agent-to-agent HTTP) + Agent Cards; NAT'd/homelab agents reachable via outbound tunnel |
| Triggers | Scheduled sweeps + autonomous signal interrupts |
| Human gate | Only at the spec PR (fully dark intake) |
| v1 sources | Sentry, Grafana, FoundryIQ, WebIQ (real); Tickets (stubbed contract) |
| Products | Multi-product from day one (routing layer + product registry) |
| Topology | Hybrid (C): deterministic conveyor + agentic investigation/council workcells |
| Memory | Unified institutional memory on Cosmos DB (working TTL tier + long-term + vector) |
| Control center | First-class v1 web UI, backed by App Configuration + Feature Flags |
| Learning | Tiered — retrieval memory now, calibration/prompt-opt periodically, fine-tuning deferred |

## 3. Architecture overview

```
            ┌───────────────────── CONTROL PLANE (cloud) ─────────────────────┐
 triggers   │  Conveyor Orchestrator ──drives──▶ [S1]…[S7]                     │
 ─────────▶ │       │                                                          │
 sched +    │       └─ Blackboard (run state) · Product Registry · Memory      │
 signal     │                                                                  │
            │  Control Center UI ──▶ App Configuration + Feature Flags         │
            └───────────────┬──────────────────────────────────────────────────┘
                            │ A2A (HTTP, outbound-tunneled where NAT'd)
        ┌───────────────────┼─────────────────────────────┐
        ▼                   ▼              ▼               ▼
   Sentry Agent      Grafana Agent   FoundryIQ Agent   WebIQ Agent  (+ Tickets, stubbed)
   (cloud)           (homelab/NAT)   (cloud/private)   (cloud)
```

Hosting is portable containers, but **Azure AI Foundry remains the brain-services backbone** (models, guardrails, FoundryIQ knowledge, tracing, evals) reached outbound from agents.

## 4. The conveyor — stations

Each station has one job, reads/writes the blackboard, and emits an audit record. Stations 2 and 5 are agentic workcells; the rest are deterministic. A proposal may be **killed at Station 4 or 5**; killed runs are logged with rationale (the audit trail of a dark system), not filed.

1. **Intake & Triage** *(deterministic)* — Normalize trigger → `Run`. Attach product/source scope hints from the Product Registry. Debounce against in-flight runs (working memory).
2. **Investigation** *(agentic workcell)* — Dispatch relevant source agents over A2A in parallel; each returns structured `EvidenceItem`s (claim + raw citation + provenance). Homelab agents participate via tunnel. A down agent → partial evidence, explicitly flagged (never fabricated coverage).
3. **Synthesis** *(agentic)* — Synthesizer clusters evidence into candidate `Proposal`s, first retrieving relevant Decision-Memory lessons.
4. **Grounding Verification** *(hard gate)* — Verifier checks every claim traces to a real `EvidenceItem` citation. Ungrounded claims stripped; unsupported proposals killed.
5. **Critic Council** *(agentic workcell)* — Independent critics, distinct lenses, parallel; each returns `{score, veto?, rationale}`. Decision rule: any hard veto kills; else weighted score must clear the per-product confidence threshold.
6. **Product Routing & Labeling** *(deterministic)* — Map surviving proposal → product/repo via Registry; assign repo's label taxonomy (type/area/severity).
7. **Issue Filing** *(deterministic)* — Final dedup against existing issues, then create GitHub issue: problem + proposed change + grounded evidence appendix + provenance. Record issue URL on blackboard; close run.

## 5. Components & contracts

### 5.1 Source agents (Investigation workcell)
One portable container each, A2A endpoint + Agent Card, shared contract: given a `Run` scope → `EvidenceItem[]`.

| Agent | Home | Backend | Emits |
|---|---|---|---|
| Sentry | cloud | sentry MCP | error/issue/trace evidence (frequency, regressions, impacted users) |
| Grafana | homelab (tunneled) | Grafana MCP | metric/log anomalies, saturation, latency trends |
| FoundryIQ | cloud/private | Foundry knowledge | internal/company context, prior decisions, roadmap fit |
| WebIQ | cloud | web research | external/industry/competitive signal |
| Tickets | *stubbed* | (future) | support-ticket clusters — contract defined, impl deferred |

`EvidenceItem = { id, sourceAgent, claim, rawCitation, provenance{timestamp, queryUsed}, confidence, productHints[] }`. **Raw citation is mandatory** — it is what the grounding gate verifies.

### 5.2 Synthesizer & Critic Council
- **Synthesizer** clusters evidence → `Proposal`s, retrieving Decision-Memory lessons first.
- **Council** = 7 independent critics (each enable/disable-able, per-product, via Control Center):
  1. Grounding (redundant cross-check of the hard gate)
  2. Value/Impact
  3. Duplication/Prior-art (dedup index + existing issues + past rejections)
  4. Feasibility/Risk
  5. Strategic-fit (FoundryIQ roadmap/company knowledge)
  6. Cost-to-build
  7. Security/Compliance (screens proposal content before filing)
- **Decision rule:** any hard veto kills; else weighted score ≥ per-product confidence threshold. Weights/threshold are the calibration loop's tuning target. Every decision writes rationale to the accept/kill log.

### 5.3 Core contracts (blackboard objects)
`Run` → `EvidenceItem[]` → `Proposal` → `CouncilVerdict` → `RoutedIssue`. Each is a versioned JSON schema in `/contracts`, shared by all agents so containers stay decoupled and independently testable. A2A envelopes wrap these.

### 5.4 Product Registry & Dedup
- **Product Registry** (config-as-data, in-repo + overridable): per product → `{ githubRepo, labelTaxonomy, foundryIQScope, sentryProjects, grafanaDashboards, confidenceThreshold }`. Drives scoping (S1) and routing (S6).
- **Dedup** — vector retrieval over (open issues + past proposals + past rejections), queried at S1/S5/S7. Part of the unified memory store.

## 6. Observability, evals, learning

### 6.1 Foundry leverage
Models & guardrails (centralized governance, content safety, prompt versioning); FoundryIQ knowledge backend; **Observability** (OpenTelemetry GenAI traces → Foundry + App Insights, per run/station/agent with token/cost); **Evaluations** (groundedness, relevance, agent evaluators + custom).

### 6.2 Dashboard (two layers)
- **Foundry portal** — out-of-the-box trace + eval views (deep-debug surface).
- **Factory Operations dashboard in Grafana** (read-only) — runs in-flight by station, throughput, **kill/rejection log**, acceptance rate, cost-per-issue, time-to-issue, dedup-hit rate, per-source evidence yield. Reads App Insights + blackboard.

### 6.3 Evaluations
- **Offline golden-set evals (CI gate)** — historical signals → expected (proposal quality, routing, labels, groundedness). Blocks deploy on regression.
- **Continuous online evals** — sample live runs, auto-score groundedness + council calibration, alert on drift → dashboard.

### 6.4 Learning loop (closes on PR outcomes)
A **PR Feedback Watcher** (GitHub webhook, event-driven, outside the per-run line) captures the human verdict on the spec PR **and the proposed-vs-final spec diff**, plus issue-triage actions (label edits, closed-as-wontfix). Three tiers:
1. **Immediate — Decision Memory (retrieval).** Outcomes distilled into product-scoped **Lessons**, retrieved at runtime by synthesizer/critics/router. Effective next run; no retraining.
2. **Periodic — calibration + prompt optimization.** Track each critic's score vs. eventual verdict; adjust weights/threshold. Labeled outcomes augment the golden set; periodic prompt-opt runs.
3. **Deferred — fine-tuning.** Only once tiers 1–2 prove insufficient.

## 7. Control Center & Institutional Memory

### 7.1 Control Center (v1 web UI)
Backed by **App Configuration + Feature Flags** (single source of truth, read at station boundaries → effective next run, no redeploy). Controls: per-critic enable/disable (incl. per-product); source-agent enable/disable; trigger pause (interrupt vs. scheduled); per-product thresholds/weights (shows current + calibration proposals to accept); **global dry-run kill switch** (run the full line, stop short of filing). Agents advertise toggle-state in their Agent Card. The Grafana dashboard stays read-only; the Control Center is the write surface.

### 7.2 Institutional Memory — one store, two tiers (Cosmos DB)
- **Working memory (short-term)** — TTL containers. Active blackboard run state + sliding window of recent signals/rejections for debounce/in-flight dedup.
- **Long-term memory (durable)** — structured records (proposals, verdicts, PR outcomes, calibration stats, golden examples) + **native Cosmos vector search** for dedup/lessons/prior-art retrieval. (Azure AI Search is a deferred swap-in if retrieval outgrows Cosmos.)
- **Consolidation ("sleep") process** — on run close and on PR-outcome arrival, distill the episode into durable Lessons + embeddings, promoting working → long-term. This is the learning loop's write path.

## 8. Infrastructure & deployment

- **Compute:** Azure Container Apps for cloud pieces (orchestrator, cloud agents, synthesizer, critics, Control Center UI, signal-ingestion endpoint). Homelab agents run the same images on Proxmox, A2A-reachable via outbound tunnel (Tailscale/Cloudflare) — no inbound ports.
- **Orchestrator:** Microsoft Agent Framework; run state persisted to the Cosmos blackboard; stations idempotent + checkpointed so a crashed run resumes.
- **Triggers:** scheduled sweep = Container Apps cron job; signal interrupt = Sentry/Grafana alert → webhook → Event Grid → ingestion endpoint, debounced via working memory.
- **Platform services:** Cosmos DB (unified memory) · App Configuration + Feature Flags (control center) · Key Vault (all secrets) · Azure AI Foundry (models, FoundryIQ, evals, tracing) · App Insights + Grafana (observability) · GitHub App (least-privilege: issues:write + PR read; filing + PR feedback webhook).
- **Identity:** Managed Identity / Entra; A2A authenticated (bearer + tunnel encryption); agents reject unauthenticated callers.
- **IaC:** Bicep via `azd` (`azd up` provisions the cloud side); homelab agents via compose.

## 9. Error handling & guardrails

- **Degraded investigation** — down source agent → partial evidence, flagged; grounding gate still enforces citations on the remainder.
- **A2A resilience** — per-agent timeouts + circuit breaker; line continues with available workcells.
- **Cost guardrails** — per-run token/cost budget cap; bounded investigation/council; cost-per-issue on dashboard.
- **Dry-run kill switch** (Control Center) — full line, no filing; safe rehearsal mode.

## 10. Security

Key Vault + Managed Identity; least-privilege GitHub App; egress-only homelab tunnels; content-safety on model calls; security/compliance critic screens proposal content before filing.

## 11. Testing & quality

- **Unit** — each agent's evidence extraction + every contract schema.
- **Contract tests** — A2A envelope + JSON-schema conformance per agent.
- **Station integration tests** — recorded source fixtures (VCR-style), deterministic.
- **Council logic tests** — veto + threshold decision rules.
- **Golden-set evals (CI gate)** — doubles as quality regression gate.
- **End-to-end dry-run** — full line with mocked sources, asserts issue payload without filing.

## 12. Repository layout

```
/contracts        shared JSON schemas + models (Run, EvidenceItem, Proposal, Verdict, RoutedIssue)
/orchestrator     conveyor + station logic + blackboard
/agents/<source>  one portable container per source agent (+ shared A2A lib)
/council          synthesizer + critics + decision rule
/control-center   web UI (toggles, thresholds, calibration review) + config backend
/memory           Cosmos access + working/long-term tiers + consolidation/learning loop
/evals            golden sets + evaluators (offline + continuous)
/infra            Bicep / azd
/docs             this design + ADRs
```

## 13. Implementation principle for "runnable without cloud"

Every external dependency (Cosmos, App Configuration, Foundry models, GitHub, each MCP source) sits behind a **provider interface** with a **local/in-memory implementation** alongside the Azure one. This lets the entire intake line run **end-to-end in dry-run** locally (and in CI) with no Azure subscription, while the Azure implementations and IaC are present and ready for `azd up`. Live cloud deployment, real external integrations, and real issue filing require explicit human authorization and credentials and are intentionally out of the autonomous build scope.
