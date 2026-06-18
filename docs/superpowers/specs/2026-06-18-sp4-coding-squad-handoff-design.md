# SP4 — Coding-squad handoff hardening (design)

- Date: 2026-06-18
- Status: Proposed
- Charter: §6 SP4 ("Align label taxonomy/triage; `squad triage --execute`; Copilot coding agent; verify knowledge loop").
- Builds on: SP1 (`dsf new` + provisioner), SP3 (council runtime), the existing S6 routing / S7 filing stations and the PR feedback-watcher learning loop.

## Problem

The feature council already routes accepted proposals to a product repo and files
GitHub issues with type/severity labels (S6 → S7). The provisioner already runs
`squad init` and `squad copilot --auto-assign`. But the **handoff between the
council and the coding squad is not actually closed**, and three concrete gaps
break it in practice:

1. **The product repo has no labels.** Nothing creates the product's label
   taxonomy in GitHub. `gh issue create --label bug --label sev-high` *fails* when
   those labels do not exist in the repo ("could not add label: not found"), so
   the very first real (non-dry-run) filing breaks.
2. **There is no universal "ready for the squad" signal.** Issues get a `type`
   and a `severity` label, but nothing tells `squad triage` *which* issues are
   council output it should pick up. The council's labels and the squad's triage
   expectations are not aligned on a shared key.
3. **`squad triage` is never wired.** The charter's handoff mechanism
   (`squad triage --execute`, which polls council-filed issues and dispatches the
   Copilot coding agent) has no provisioner step — the squad is initialized but
   never told to start triaging.

The knowledge loop (squad reflections in `.squad/` + the council's PR
feedback-watcher → lessons) exists on the council side but is undocumented as a
single closed loop, so "verify knowledge loop" has nothing to verify against.

## Goals / non-goals

**Goals**
- Make the council→squad handoff *work end to end* on a real instance: labels
  exist, every council-filed issue carries a single well-known **handoff label**,
  and the squad is wired to triage exactly those issues.
- Keep the contract **explicit, shared, and testable** (one source of truth for
  the handoff label; offline unit tests for routing + provisioning).
- Document the closed knowledge loop so it can be reasoned about and verified.

**Non-goals**
- Implementing or forking `bradygaster/squad` itself (external CLI; invoked
  through the injectable runner, never in tests).
- Live-GitHub or live-squad integration testing (out of scope; the suite stays
  fully offline — ADR 0001/0006).
- Auto-syncing `config/products.json` from `dsf new` (a separate registry-write
  gap, tracked elsewhere — SP4 does not depend on it because the handoff label is
  a system constant, not per-product data).

## Design

### 1. The handoff label is a system-level contract (not per-product data)

Introduce a single shared constant — the **handoff label** — that the council
stamps on *every* issue it files and that the squad triages on:

```python
# src/dsf/contracts/handoff.py
HANDOFF_LABEL = "squad:ready"
HANDOFF_LABEL_DESCRIPTION = "Council-filed issue ready for coding-squad triage"
HANDOFF_LABEL_COLOR = "1d76db"
```

Making it a constant (not a per-product taxonomy entry) means the contract cannot
drift per product and S6 does not depend on each product's `label_taxonomy`
containing the right magic key. Both the **runtime** (S6 routing) and the
**instance tooling** (provisioner) import this one module — `contracts/` is the
neutral shared layer, so this respects the runtime↔instance boundary.

### 2. S6 routing stamps the handoff label on every routed issue

`_labels_for(proposal, product)` keeps deriving the per-product `type` +
`severity` labels, then always appends `HANDOFF_LABEL`. Result: every
`RoutedIssue` (and therefore every filed issue) carries `squad:ready` in addition
to its descriptive labels. Dedup/body/everything else is unchanged.

### 3. Provisioner creates the labels (idempotently) before triage

Add a **`create_labels`** step to `InstanceProvisioner.plan()`, ordered after
`create_repo` (repo must exist) and before `squad_init`. It creates, with
`gh label create --force` (upsert — idempotent on re-runs):
- every label in the instance's `label_taxonomy` (type/area/severity), and
- the `HANDOFF_LABEL` (with its description + color).

Because `plan()` is pure and one `ProvisionStep` carries one `command`, the step
emits a small ordered list of `gh label create` commands via a new
`commands: list[list[str]]` field on `ProvisionStep` (today a step has a single
`command`). `apply()` runs each in order under `--execute`; dry-run records them.

### 4. Provisioner wires `squad triage --execute` to the handoff label

Add a **`squad_triage`** step after `squad_copilot`:

```
["squad", "triage", "--execute", "--label", HANDOFF_LABEL]
```

This is the charter's handoff mechanism: poll the issues the council files (those
carrying `squad:ready`) and dispatch the Copilot coding agent. Same injectable-
runner + dry-run treatment as the other squad steps.

### 5. Knowledge loop — document + assert the closed loop

The loop, end to end:

```
council S6/S7 → files issue (squad:ready) → squad triage --execute →
Copilot coding agent → PR → human review → council PR feedback-watcher →
record_outcome() → Lesson (MemoryStore.get_lessons) → next council run
```

SP4 documents this as one closed loop (ADR 0007 + RUNBOOK section) and adds a
focused test asserting the council half is wired: a filed issue carries
`HANDOFF_LABEL`, and a merged-PR webhook for that product produces a retrievable
lesson. The squad's internal `.squad/` reflection is external and is covered by
documentation, not by a fake in-repo test.

## Components touched

| Unit | Change | Boundary |
|---|---|---|
| `src/dsf/contracts/handoff.py` (new) | `HANDOFF_LABEL` + metadata constant | shared contract |
| `src/dsf/orchestrator/stations/s6_routing.py` | append `HANDOFF_LABEL` in `_labels_for` | runtime |
| `src/dsf/instance/spec.py` (`ProvisionStep`) | add `commands: list[list[str]]` for multi-command steps | instance |
| `src/dsf/instance/provisioner.py` | `create_labels` + `squad_triage` steps; run multi-command steps | instance |
| `docs/adr/0007-council-squad-handoff.md` (new) | record the handoff contract + closed loop | docs |
| `docs/RUNBOOK.md`, charter §6 | document loop; flip SP4 row ✅ | docs |

## Testing

All offline, via the injectable runner / recording GitHub client:
- **S6:** routed issues always include `HANDOFF_LABEL` alongside type/severity.
- **Provisioner plan:** step order now includes `create_labels` (after
  `create_repo`, before `squad_init`) and `squad_triage` (after `squad_copilot`);
  `create_labels` emits one `gh label create --force` per taxonomy label + the
  handoff label; `squad_triage` command shape carries `--label squad:ready`.
- **Provisioner apply:** under `--execute` the fake runner sees each label-create
  command and the triage command; dry-run records them without invoking.
- **Loop:** filed issue carries `HANDOFF_LABEL`; a merged-PR event for the product
  yields a lesson via `MemoryStore.get_lessons`.
- Full suite + `ruff` + eval gate green; offline dry-run of the station line green.

## Decisions (resolved)

- **Handoff label = `squad:ready`** (system constant, blue `1d76db`). A single
  universal signal beats per-product config because it cannot drift and keeps S6
  independent of each product's taxonomy contents.
- **Label creation lives in provisioning, not in S7 filing.** Creating labels at
  issue-filing time would couple the hot path to label management and repeat work
  every run; provisioning is the right, idempotent home.
- **`squad triage --execute` is a provisioner step**, matching the charter
  verbatim, rather than a long-running watcher owned by this repo (the squad owns
  its own scheduling; we wire and kick it).
