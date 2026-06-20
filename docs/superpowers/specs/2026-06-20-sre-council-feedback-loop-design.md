# SRE to Council Feedback Loop: Operational Council Sources (Design)

**Status:** Accepted (2026-06-20); to be recorded as ADR 0013 and implemented. This
is the design charter; the plan and the code follow.

**Scope:** Close the SRE to Council feedback loop (the "slow path") that phase 3
left deferred. The managed Azure SRE Agent observes production and files incident
issues; production also emits telemetry. This design feeds both back into the
Feature Council as ordinary council sources, so the council can decide whether a
recurring incident or a telemetry signal warrants a systemic feature or fix. It
adds two operational `SourceKind`s and their source agents, an `incident` label
marker, the provisioning and runbook wiring, and the documentation cleanup. It is
the decision path, not an implementation plan.

---

## 1. Problem

The three phases close two of three loops. Phase 1 (Feature Council) turns signals
into specs. Phase 2 (Coding Squad) turns `squad:ready` issues into pull requests.
Phase 3 (the managed Azure SRE Agent) observes production and can file incidents,
but nothing carries what it learns back into the council. The phase-3 narrative
(`docs/phases/sre-agent.md`) names this the "slow path" and marks it deferred.

The result is a factory that can build and ship but cannot reflect: a fault that
recurs in production never becomes a hardening proposal, and live telemetry never
informs what the council chooses to build next. The loop is open.

Two further hazards shape the solution:

- **Self-ingestion.** The council files its own FEATURE and FIX issues into the
  product repository carrying `squad:ready`. A naive "read the repo's issues"
  source would re-ingest council output and spin.
- **Single-agent judgment.** Deciding that an incident is systemic enough to act on
  is exactly the synthesis-and-validation judgment the deliberative council already
  performs (ADR 0011). A lone SRE step should not make that call.

## 2. Principles

- **Operational evidence is just more evidence.** The council already pulls
  observability sources (`SENTRY`, `GRAFANA`). Incidents and telemetry are more of
  the same. They produce `EvidenceItem`s and ride the existing S1 to S7 line:
  synthesis, the validation jury, the confidence threshold, dry-run governance, and
  FEATURE versus FIX routing are unchanged. No parallel pipeline, no mini-council.
- **Keep the SRE Agent managed.** DSF does not rebuild an SRE runtime (ADR 0009
  stands). The SRE Agent stays the managed Azure product, onboarded interactively.
  DSF's only added concern is the contract that lets its output reach the council.
- **One marker is the whole contract.** A single `incident` label, stamped by the
  SRE Agent and pulled by the incidents source, is the entire interface. It also
  breaks the self-ingestion cycle, because council-filed issues never carry it.
- **Recurrence intelligence lives in the backend.** Whether an incident is a
  one-off or a recurring pattern is decided inside the incidents backend, which
  surfaces a recurrence as a single higher-confidence item. The conveyor decides
  what to do with it; it gains no new stage.
- **Council members are separate entities.** Each source agent stays its own
  entity. The telemetry source's live Azure access is its own seam, refined later;
  it is not bound to a shared council identity now. The offline fixture path works
  regardless.
- **Implement fully, defer honestly.** Everything in this design is built now. The
  only deferred item is the live telemetry role binding, and it is deferred as an
  explicit, documented seam, not a half-built feature.

## 3. Architecture

Two new `SourceKind`s join the enum in `core/src/dsf/contracts/enums.py`:

- `AZUREMONITOR` — production telemetry from Azure Monitor / Application Insights.
- `INCIDENTS` — incident issues the SRE Agent files into the product repository.

Each gets a council source agent under `feature-council/src/dsf/agents/<kind>/`,
following the `grafana` exemplar exactly:

- `backend.py` — a `<Kind>FixtureBackend` (offline and dry-run; replays
  `tests/fixtures/<kind>_evidence.json`) and a live backend (azure mode; an
  injected client, mirroring grafana's `mcp_call` seam). Both implement
  `gather(run_scope) -> list[EvidenceItem]` and degrade gracefully: disabled flag
  or backend error yields an empty `A2AResponse(degraded=True)`.
- `main.py`, `client.py`, `__init__.py` — the ASGI app, client, and package
  surface, as the other agents have them.

Both register in `feature-council/src/dsf/agents/registry.py` `DEPLOYABLE_AGENTS`,
whose keys must equal `SourceKind.value.lower()` (the existing parity test enforces
this and will cover the two new keys automatically). They are enabled in
`config/defaults.json` under `agents` like the others:

```json
"AZUREMONITOR": { "enabled": true },
"INCIDENTS":    { "enabled": true }
```

Because the scheduled sweep scopes a run to every enabled `SourceKind`, enabling
them is all it takes for the council to pull them. Before a product is onboarded,
their scope is empty and they degrade to empty evidence, which is harmless.

### 3.1 The `incidents` source

The live backend lists open issues in the product repository that carry the
`incident` label, via the council's GitHub client seam. The label is a new
system-level constant beside the handoff label:

```python
# core/src/dsf/contracts/handoff.py
HANDOFF_LABEL = "squad:ready"
INCIDENT_LABEL = "incident"
```

**Recurrence is decided here.** The backend groups the labelled issues by a stable
signature (product plus a normalized title or area) and emits one `EvidenceItem`
per signature. A signature seen multiple times is surfaced as a single item with
higher `confidence` and a claim that names the recurrence (for example, "checkout
5xx recurred 4 times in 14 days"); a one-off is surfaced with low confidence. The
conveyor's per-product threshold then decides whether the recurring pattern clears
the bar for a proposal. No new conveyor stage is added.

### 3.2 The `azuremonitor` source

The live backend queries Azure Monitor / Application Insights, scoped by a new
`Product` field `azure_monitor_scope` (the Application Insights or Log Analytics
resource id for the product). Each result maps one-to-one onto an `EvidenceItem`
with `provenance=Provenance(query_used, source_kind=AZUREMONITOR)`. The fixture
backend replays `tests/fixtures/azuremonitor_evidence.json` offline.

`Product` (in `core/src/dsf/config/registry.py`) gains:

```python
azure_monitor_scope: str = ""
```

The `incidents` source needs no new registry field: it already has
`Product.github_repo`, and the marker is the `INCIDENT_LABEL` constant.

## 4. Data flow

```
Production
  ├── Azure SRE Agent ── files incident issue ── labels it `incident`
  │                                                      │
  │                                          incidents source (pull, scheduled)
  │                                                      │
  └── telemetry (App Insights) ── azuremonitor source ──┤
                                                         ▼
                            Council scheduled sweep (enabled SourceKinds)
                                                         ▼
                          S1 triage → S2..S5 synthesis + jury → S6 routing
                                                         ▼
                       FEATURE or FIX issue filed with `squad:ready`
                                                         ▼
                                Coding Squad (Ralph watch loop)
```

The council files FIX and FEATURE issues into the same repository the incidents
source reads, but those carry `squad:ready`, not `incident`, so the incidents
source never re-ingests them: the marker breaks the cycle. A recurring incident
that has already produced a proposal is stopped from producing a duplicate by the
existing proposal dedup (S5 writes a `kind="proposal"` record; a later run that
re-derives the same proposal dedups against it).

## 5. Provisioning and CLI wiring

No new top-level CLI command. The sources are registry-driven (consistent with the
de-hardcoded agent registry, issue #25): adding them to `DEPLOYABLE_AGENTS` is the
single source of truth, and the council runtime serves them automatically.

- **`provisioner.create_labels`** also creates the `incident` label (idempotent
  `gh label create --force`), alongside the product taxonomy and `squad:ready`, so
  SRE filing never fails on a missing label.
- **`runtime_render._product_from_spec`** sets `azure_monitor_scope` from the spec
  or the Azure provisioning outputs; blank offline, which degrades gracefully.
- **`runtime_render._render_sre_onboarding_md`** gains two lines in the runbook:
  the SRE Agent must stamp the `incident` label on every incident issue it files,
  and a note that the council now learns from resolved incidents and from
  telemetry. The `onboard_sre_agent` step stays render-only (no `az` calls).

## 6. Per-agent Azure access (deferred seam)

The `azuremonitor` live backend needs read access to the product's telemetry
(Monitoring Reader or Log Analytics Reader on the Application Insights or workspace
scope). Per the separate-entities decision, this is the telemetry agent's own
identity seam and is documented as a follow-up for the first real azure-mode
council deployment. It is not bound to a shared council identity in this work. The
offline and dry-run path is fully functional through the fixture backend; azure
mode without the role degrades gracefully and honestly (logged error, empty
evidence, `degraded=True`).

## 7. Documentation cleanup (current-state only)

These references describe the current system and are stale; they are corrected as
part of this work:

- `docs/adr/0009-leverage-azure-sre-agent.md` flow lines 49 and 64 say
  `squad triage --execute`; retarget to the Ralph watch loop (ADR 0012).
- `feature-council/src/dsf/orchestrator/stations/s6_routing.py:64` comment says
  "squad triage keys on this"; retarget to the Ralph watch loop.
- `docs/phases/sre-agent.md` (the deferred slow-path framing), the README loop
  diagram, and the RUNBOOK are updated to describe the now-built feedback loop and
  the two operational sources.

Historical specs, plans, and the superseded ADR 0008 are point-in-time records and
are left unchanged.

A new ADR 0013 records the closed loop, the two operational `SourceKind`s, and the
`incident` marker.

## 8. Testing

All tests stay offline and the whole suite stays green.

- **Fixture-backend gather tests** for both sources: a populated scope yields mapped
  `EvidenceItem`s with the right `source_kind`; an empty or disabled scope yields
  `degraded=True` with no items.
- **Recurrence aggregation test** for `incidents`: repeated signatures collapse into
  one higher-confidence item; a one-off stays low-confidence.
- **Registry parity** is covered by the existing `DEPLOYABLE_AGENTS` versus
  `SourceKind` test once the two keys are added.
- **`create_labels` test**: the rendered or executed label set includes `incident`.
- **Offline end-to-end**: an operational `EvidenceItem` (incident or telemetry)
  flows S1 to S7 and produces a routed, `squad:ready`-labelled issue under the
  non-dry-run switches, with the proposal dedup holding on a second sweep.

## 9. Research grounding

The deliberative council (ADR 0011) already grounds its synthesize-then-validate
design in the multi-agent debate and self-consistency literature (for example,
Du et al., "Improving Factuality and Reasoning in Language Models through Multiagent
Debate", 2023; Wang et al., "Self-Consistency Improves Chain of Thought Reasoning",
2022). This design deliberately adds no new judgment machinery: operational evidence
is routed into that same validated pipeline rather than judged by a single agent,
which is the whole point of Approach A. Pull-based, scheduled intake over a governed
buffer (the phase-1 redesign) keeps the council in control of when it reflects on
operations, rather than being pushed by production events.

## 10. Out of scope

- Rebuilding any bespoke SRE runtime (ADR 0009 stands; the SRE Agent is managed).
- Binding telemetry access to a shared council identity (section 6 seam).
- Any new conveyor stage, ops-specific synthesis, or separate ops mini-council
  (rejected approaches B and C).
- Vector or semantic ranking of operational evidence beyond the existing backend
  mapping.
