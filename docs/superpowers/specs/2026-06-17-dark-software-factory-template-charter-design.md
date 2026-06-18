# Dark Software Factory — Template & Instance Charter (Design)

**Status:** Proposed (2026-06-17) — pending review
**Scope:** Establishes the north-star goal for this repository: evolve it from the *intake line* into the **template + CLI** that stamps out a complete, **isolated autonomous software factory per product** — feature council, coding squad, SRE agent, and dedicated Azure resources, wired together by one CLI. This document is a **charter + decomposition + roadmap**, not an implementation plan. Each sub-project (SP) gets its own spec → plan → implementation cycle. The first sub-project (SP1) is planned immediately after this charter is approved.

---

## 1. Vision

Inspired by manufacturing "dark factories," this repository today implements the **intake line** — it decides *what to build* and files grounded, deduplicated, labeled GitHub issues (see `2026-06-15-dark-software-factory-intake-design.md`). The charter extends that vision end-to-end:

> **This repo becomes a reusable template and CLI that provisions a full, self-contained software factory for any product.** One command stands up the product's repository (with a coding squad ready to work), a feature council scoped to that product, an SRE agent watching its production, and the Azure resources to back them — all isolated and wired into a closing loop.

The factory is **mostly dark**: humans stay accountable for priorities, approvals, and final merges, but the machinery of *deciding what to build*, *implementing it*, and *responding to production* runs autonomously and learns over time.

### The full loop (per product, isolated)

```
   ┌──────────────────────── ISOLATED PRODUCT FACTORY ────────────────────────┐
   │                                                                           │
   │   observe production                                                      │
   │        │                                                                  │
   │        ▼                                                                  │
   │   SRE Agent ──(prod incidents: fix-forward)──▶ Coding Squad ──▶ PRs ──┐    │
   │        │                                            ▲                 │    │
   │        │ (prod signals, later)                      │ (issues)        ▼    │
   │        └────────────▶ Feature Council ──────────────┘            deploy    │
   │                          ▲                                          │      │
   │                          └──────── observe production ◀─────────────┘      │
   │                                                                           │
   │   Dedicated Azure resource group (Cosmos, App Config, Key Vault, …)        │
   └───────────────────────────────────────────────────────────────────────────┘
```

Every product gets its **own** copy of this loop. No signals, memory, or context are shared between products — isolation keeps each council's reasoning scoped and auditable.

## 2. Core decisions (locked in this brainstorm)

| Decision | Choice |
|---|---|
| Repository purpose | **Template + CLI** for instantiating product factories (not a single running factory) |
| Isolation model | **Fully isolated factory per product** — own council, SRE, Azure RG, product repo |
| Feature Council | The existing intake line + critic council, **scoped to a single product** per instance (name TBD) |
| Coding Squad | [`bradygaster/squad`](https://github.com/bradygaster/squad) initialized in the product repo; consumes council-filed issues; Copilot coding agent + `.squad/` knowledge loop |
| SRE Agent | **New, phased.** Observe prod → fix-forward into Squad; **later** emit signals to the council; self-reflect |
| Handoff | Council → Squad via **GitHub issues** (label taxonomy already modeled in product config) |
| Instance composition | New **product repo** (Squad-initialized) + dedicated **factory runtime** (council + SRE) deployed *outside* the repo + dedicated **Azure RG**, all wired |
| Instantiation approach | **A — CLI-as-orchestrator** over `gh` + `squad` + `az`, idempotent + `--dry-run`; **evolve toward B** (declarative manifest + reconciler) as instances multiply |
| Onboarding modes | **Greenfield first** (`dsf new`), **brownfield** (`dsf onboard <existing-repo>`) as a fast follow |
| Runtime hosting | The council runtime runs as an **Azure Container App** on a user-assigned managed identity in the product's RG (ADR 0004, supersedes ADR 0002) |

## 3. Vocabulary

| Term | Meaning |
|---|---|
| **Template** | This repository — the factory blueprint (code, IaC, CLI). |
| **Instance** | One isolated, deployed factory dedicated to a single product. |
| **Feature Council** | The intake line (7-station conveyor + adversarial critic council) scoped to one product. *(Name under review — see Open Decisions.)* |
| **Coding Squad** | A Squad team living in the product repo that triages and implements council-filed issues. |
| **SRE Agent** | The production-watching agent that fix-forwards incidents to the Squad and (later) feeds the council. |
| **`dsf` CLI** | The orchestrator that creates and manages instances. |

## 4. Architecture

### 4.1 The template (this repo)

This repository holds: the feature-council codebase (`src/dsf/**`, today's intake line), the SRE-agent codebase (new), the per-product Azure IaC (`infra/**`, parameterized), and the `dsf` CLI (existing `run|sweep|serve-*|control-center`, extended with `new|onboard|status|...`). It is the single source of truth that the CLI reads to stamp instances.

### 4.2 An instance (per product)

```
 PRODUCT REPO (new or onboarded)            FACTORY RUNTIME (deployed, scoped to product)
 ┌───────────────────────────┐             ┌───────────────────────────────────────────┐
 │ product source code        │   issues   │ Feature Council (conveyor + critic council) │
 │ .squad/ (Coding Squad)     │◀───────────│   scoped to THIS product only               │
 │   squad triage --execute   │            │ SRE Agent (observe → fix-forward → signals) │
 │   @copilot coding agent    │──PRs──────▶ │                                             │
 └───────────────────────────┘             └───────────────────┬───────────────────────┘
                                                                │ outbound
                                            ┌───────────────────▼───────────────────────┐
                                            │ DEDICATED AZURE RG (Cosmos, App Config,     │
                                            │ Key Vault, App Insights, ingestion buffer)  │
                                            └─────────────────────────────────────────────┘
```

- **Product repo** stays clean: it contains product code plus Squad state (`.squad/`). Coding happens here.
- **Factory runtime** (council + SRE) runs as a deployed service *pointed at* the product repo — consistent with ADR 0002 (runtime separate from product code).
- **Azure RG** is dedicated per instance; reuses the existing Bicep, parameterized per product.

### 4.3 Subsystem models

**Feature Council** *(exists; needs single-product scoping + Azure-mode productionization).* The 7-station conveyor and critic council already run end-to-end in dry-run on in-memory fakes. To serve an instance it must: (a) be scoped to exactly one product (no multi-product registry mixing), and (b) gain a real `build_services('azure')` path (today raises `NotImplementedError`).

**Coding Squad** *(integration).* `squad init` scaffolds `.squad/` into the product repo. `squad triage --execute` polls GitHub issues (those the council files) and dispatches the Copilot coding agent; team members persist learnings (the reflection/knowledge loop). The council's label taxonomy must align with Squad's triage expectations.

**SRE Agent** *(new, phased).* Observes production telemetry (reusing the existing Sentry/Grafana A2A backends already in `src/dsf/agents/`). Two outputs: a **fast path** that fix-forwards production incidents directly into the Squad, and a **slow path** (later) that emits operational signals into the feature council. Maintains its own reflection store to improve over time.

**Azure** *(per-instance).* A dedicated resource group per instance from the existing Bicep (Cosmos, App Config, Key Vault, App Insights, Event Grid → Service Bus ingestion buffer), parameterized by product. The CLI invokes the deployment and feeds outputs back into the instance config.

**`dsf` CLI** *(the spine).* `dsf new <product>` orchestrates, idempotently and with `--dry-run`:
1. Create the product repo (`gh repo create`) or target an existing one (brownfield).
2. `squad init` + configure the team, routing, and Copilot coding agent.
3. Provision the dedicated Azure RG (`az`/`azd`) from parameterized Bicep.
4. Deploy the feature-council + SRE runtime (GHCR containers per ADR 0002), scoped to the product.
5. Write per-product factory config and wire all four together.

## 5. Instance lifecycle (target surface)

- `dsf new <product>` — greenfield: create everything.
- `dsf onboard <repo>` — brownfield: wire an existing repo into a new factory.
- `dsf status <product>` — report what exists/what's healthy.
- `dsf upgrade <product>` — roll template changes into an instance.
- `dsf destroy <product>` — tear an instance down.

(Greenfield `new` is SP1; the rest land in later sub-projects.)

## 6. Decomposition & roadmap

Each is its own spec → plan → implementation cycle.

| SP | Sub-project | Outcome |
|---|---|---|
| **SP1 ✅** | **`dsf new` greenfield walking skeleton** *(done)* | Instance contract/manifest; create product repo + `squad init` + per-product factory config; **dry-run stubs** for Azure/council/SRE wiring. A real, demoable instance shell end-to-end. |
| SP2 ✅ | Per-product Azure provisioning *(done)* | Parameterize Bicep; CLI provisions a dedicated RG and captures outputs into instance config. |
| SP3 ✅ | Feature council productionization *(done)* | `build_services('azure')` (real GitHub client + App Insights tracer; model/memory/config on in-memory implementations behind a seam); single-product scoping via `Run.scope_product_hints`; orchestrator runtime image + per-product council rendered as an Azure Container Apps descriptor and deployed on `--execute`. |
| SP3b ✅ | Real Azure data adapters *(done — ADR 0006)* | Replace the in-memory implementations behind the azure seam with real App Configuration, Cosmos, and Azure OpenAI adapters (App Configuration first), each behind a narrow injected gateway so the suite stays offline. |
| SP4 | Coding-squad handoff hardening | Align label taxonomy/triage; `squad triage --execute`; Copilot coding agent; verify knowledge loop. |
| SP5 | SRE agent | Observe prod (reuse Sentry/Grafana backends) → fix-forward to Squad → (later) signals to council → self-reflection. |
| — | Naming refresh *(cross-cutting)* | Rename "feature council" and related terms. |
| — ✅ | CLI / runtime split *(cross-cutting, done)* | `dsf` (factory CLI, `dsf.cli.factory`) creates instances; `dsfctl` (instance control, `dsf.cli.control`) operates the feature-council runtime. Two console scripts in one package (ADR 0003). |

> **Deferred:** brownfield onboarding (former SP6) and instance lifecycle & ops (former SP7) are removed from the active roadmap and tracked as GitHub issues [#23](https://github.com/JoranBergfeld/dark-software-factory/issues/23) and [#24](https://github.com/JoranBergfeld/dark-software-factory/issues/24). They re-enter the roadmap after the SP3b → SP4 → SP5 push.

## 7. First sub-project (SP1)

**`dsf new` greenfield walking skeleton.** Establishes the **instance contract** and the CLI lifecycle spine: given a product name, create the product repo, initialize the Coding Squad in it, and emit per-product factory configuration — with Azure provisioning, council deployment, and SRE deployment represented as **dry-run/stubbed steps** so the full sequence is exercised before the heavy subsystems land. This yields a real instance shell you can grow incrementally (SP2–SP5 fill in the stubs). A detailed implementation plan follows this charter.

## 8. Open decisions (recommendations noted; revisit per sub-project)

1. **Runtime hosting per instance.** *(Resolved: ADR 0004.)* The council runtime runs as an **Azure Container App** on a user-assigned managed identity in the product's RG, created by `main.bicep` and image-rolled by `dsf new --execute`. (The earlier homelab target, ADR 0002, has been retired.)
2. **SRE agent tech.** Start **dsf-native** (reuse Sentry/Grafana A2A backends + a reflection store); consider an "SRE Squad" later. Resolve in SP5.
3. **Naming.** "Feature council" and related terms to be renamed; treat as a parallel cross-cutting task.
4. **Brownfield depth.** Greenfield first (SP1); decide brownfield's exact scope (Squad-into-existing-repo, history-aware council priming) when brownfield onboarding is picked up ([#23](https://github.com/JoranBergfeld/dark-software-factory/issues/23)).

## 9. Constraints carried forward (from existing ADRs)

- **ADR 0001:** single `dsf` package, ports + in-memory fakes for every external dependency, hybrid deterministic/agentic conveyor.
- **ADR 0002 → superseded by ADR 0004:** the runtime now runs *inside* Azure as a Container App on a user-assigned managed identity (the homelab target is retired); Azure hosts both the backing services and the runtime.
- **Dry-run-first:** the template and CLI must be exercisable end-to-end with no billable resources (`--dry-run`), mirroring the intake line's no-cloud/no-LLM test posture.
