# Dark Software Factory — Intake Line

Inspired by manufacturing "dark factories," this repo automates the hardest part of the
SDLC: **deciding what to build.** It's a mostly-autonomous, multi-product pipeline that
listens to operational and market signals, investigates them with rigorous grounding,
runs proposals past an adversarial critic council, and files labeled GitHub issues with a
full evidence trail. The only human gate is downstream, approving the spec PR.

```
 signals ──▶ [S1 Triage] ─▶ [S2 Investigation] ─▶ [S3 Synthesis] ─▶ [S4 Grounding gate]
          ─▶ [S5 Critic Council] ─▶ [S6 Routing] ─▶ [S7 Filing] ──▶ labeled GitHub issue
```

Investigation gathers evidence from **Sentry, Grafana, FoundryIQ, WebIQ** (each a portable
A2A agent deployed near its source). A 7-member critic council (grounding, value,
duplication, feasibility, strategic-fit, cost-to-build, security/compliance) scores and
can veto. A **Control Center** UI toggles critics/agents/triggers and a global dry-run
kill switch at runtime. A **learning loop** feeds human PR verdicts back as retrievable
lessons and council-weight calibration.

## Quickstart

```bash
make install   # uv venv (Python 3.12) + editable install
make test      # full suite (in-memory, no cloud, no LLM)
make dryrun    # run the whole line on a sample signal, dry-run
make evals     # golden-set eval gate
```

Then explore the Control Center:

```bash
uv run python -m dsf.cli control-center   # http://localhost:8081
```

## Status

- **Runnable locally, end-to-end, in dry-run** against in-memory fakes — every component
  built behind a port. The full conveyor drives a signal to a (simulated) filed issue with
  grounding enforced and no network calls. 167 tests pass.
- **Azure-ready, not deployed.** Bicep/`azd` IaC and Azure backends are authored but
  require your credentials and explicit `azd up`. No billable resources are provisioned by
  the local flow.

## Docs

- Design spec: `docs/superpowers/specs/2026-06-15-dark-software-factory-intake-design.md`
- Implementation plan: `docs/superpowers/plans/2026-06-15-dark-software-factory-intake.md`
- **Runbook (how to run & deploy): `docs/RUNBOOK.md`**
- Architecture decisions: `docs/adr/0001-architecture-decisions.md`

## Layout

`src/dsf/` — `contracts` (shared models) · `ports` + `fakes` · `config` (flags + product
registry) · `memory` (tiers, dedup, consolidation) · `a2a` · `agents/<source>` ·
`council` (synthesizer + critics + decision) · `orchestrator` (conveyor + stations) ·
`triggers` · `learning` · `evals` · `observability` · `control_center`.
`infra/` — Bicep/azd + homelab compose.
