# Azure SRE Agent integration — replace the bespoke SRE agent (design)

- Date: 2026-06-18
- Status: Proposed
- Issue: [#30](https://github.com/JoranBergfeld/dark-software-factory/issues/30)
- Supersedes: the bespoke SP5 implementation (ADR 0008) — the runtime, not the goal.
- Builds on: per-product Azure provisioning (`src/dsf/instance/`), SP4's
  `HANDOFF_LABEL = "squad:ready"` handoff contract, the product registry (S6 routing).

## Problem

SP5 shipped a **bespoke** SRE agent (`src/dsf/sre/`, ~360 LOC: an
`observe → detect → fix_forward → reflect` sweep run via `dsfctl sre-sweep` and
deployed as a custom `dsf-sre-<product>` Container App). It hand-rolls — against
fixture telemetry — exactly what the **Azure SRE Agent** product already does:
connect to a resource group's observability, autonomously investigate incidents,
and file GitHub issues / open PRs.

**Directive (firm):** DSF must *leverage the Azure SRE Agent product*, not a
custom implementation. So the bespoke runtime is the wrong shape and is removed;
DSF's job shrinks to **preparing each product instance for, and guiding the
onboarding of, an Azure SRE Agent** — keeping the factory's one-command
instance-stamping coherent.

## What the Azure SRE Agent is (grounding)

A managed Azure service (created via the wizard at `sre.azure.com`). On creation
it deploys a managed identity, Log Analytics workspace, Application Insights, role
assignments, and the SRE Agent resource itself. Onboarding then **connects a
GitHub repo** (browser OAuth or PAT) and **grants the agent Reader on Azure
resources** (subscription or resource group). It investigates incidents (Azure
Monitor / App Insights / PagerDuty / ServiceNow) and creates issues / opens PRs.

**Key constraint:** onboarding is **wizard- and OAuth-driven**. There is no clean
headless `az`/Bicep path for the GitHub-connector OAuth or the interactive
resource-access grant today. DSF therefore automates only what it genuinely owns
and **guides** the interactive remainder (Approach A).

## Goals / non-goals

**Goals**
- Remove the bespoke SRE runtime entirely (`src/dsf/sre/`, `dsfctl sre-sweep`, the
  custom Container App deploy) — no `Fake`/stub SRE logic left in `src/`.
- Replace the provisioner's `deploy_sre` step with **`onboard_sre_agent`**, a
  **render-only** step which:
  - **always** renders a deterministic, per-instance onboarding runbook
    (`sre-onboarding.md`) telling the operator exactly how to stand up the Azure
    SRE Agent for this product;
  - has nothing to headlessly execute (the wizard/OAuth is interactive); the
    prerequisites the agent needs — the product RG, the product repo, and the
    `squad:ready` label — are already established by the earlier `provision_azure`,
    `create_repo`, and `create_labels` steps. On `--execute` it simply renders and
    reports `onboarding ready`; on dry-run it renders and reports `rendered
    (dry-run)`.
- Preserve the closed loop: Azure SRE Agent → issues/PRs in the product repo →
  `squad:ready` → the **same** coding squad triages them.
- Stay fully offline-testable via the injectable `run` callable.
- Record the decision in a new ADR superseding ADR 0008; update the charter §6 SP5
  row and the RUNBOOK SRE section.

**Non-goals**
- ARM/Bicep-deploying the SRE Agent resource or scripting the GitHub-connector
  OAuth (rejected Approach B — fragile, partly impossible today).
- Any custom incident detection / fix-forward logic in DSF (the product does it).
- Changing the council→squad handoff contract (`squad:ready` is unchanged).

## Design

### Removal

- Delete `src/dsf/sre/` (`agent.py`, `detect.py`, `models.py`, `wiring.py`,
  `main.py`, `__init__.py`) and `tests/sre/`.
- `src/dsf/cli/control.py`: remove `_cmd_sre_sweep` and the `sre-sweep` subparser.
- `src/dsf/instance/runtime_render.py`: remove `SreBundle` + `render_sre_bundle`.
- `src/dsf/instance/provisioner.py`: remove the `deploy_sre` step and its
  `az containerapp update --name dsf-sre-<product>` execute branch.
- `src/dsf/cli/factory.py`: update the `--execute` help text (drop "SRE bring-up").
- `src/dsf/agents/registry.py`: keep the SRE-exclusion note but drop the stale
  "deployed as its own Container App" wording.
- ADR 0008: mark **Superseded by ADR 0009**.

### Addition

- **`render_sre_onboarding(manifest, *, repo_root) -> SreOnboarding`** in
  `instance/runtime_render.py`. Writes `runtime/<product>/sre-onboarding.md` whose
  body is derived deterministically from the instance spec:
  - the `sre.azure.com` onboarding entry point and the target **product resource
    group** (`spec.resource_group()`) + **region** (`spec.location`) — the
    subscription is chosen in the wizard;
  - the **repository to connect** (the product repo, `spec.github_repo()`) under
    *Connect your code*;
  - the **resource group to grant Reader** (the product RG) under *Add Azure
    resource access*;
  - a model-provider note (Azure OpenAI for EU data-residency tenants);
  - a reminder that incident issues must keep the `squad:ready` label so the
    coding squad triages them.
  Returns the runtime dir + the written path (mirrors `RuntimeBundle`).
- **Provisioner step `onboard_sre_agent`** (replaces `deploy_sre`), placed where
  `deploy_sre` was (after `deploy_council`). In `apply`:
  - always calls `render_sre_onboarding(...)`; on dry-run sets
    `result = "rendered (dry-run)"`;
  - on `--execute`, renders and sets `result = "onboarding ready"`. There is
    nothing to headlessly execute — the prerequisites (`provision_azure`,
    `create_repo`, `create_labels`, which already creates `squad:ready`) run
    earlier in the plan. No fabricated `az sre-agent` calls.

### Data flow (contract preserved)

```
Azure SRE Agent (onboarded against the product RG + repo)
  → investigates incidents (App Insights / Azure Monitor)
  → files issues / opens PRs in the product repo, labelled squad:ready
  → squad triage --execute  → Copilot coding agent → PR → human review
```

DSF's responsibility: provision prerequisites + emit the onboarding runbook. The
council→squad loop is untouched.

## Testing

- `tests/instance/test_runtime_render.py`: `render_sre_onboarding` writes the file
  and references the correct product RG, region, and repo; assert no stale
  `sre.containerapp.yaml` / `dsf-sre-<product>` strings remain.
- `tests/instance/test_provisioner.py`: the plan contains `onboard_sre_agent` (not
  `deploy_sre`); both dry-run and `--execute` render the runbook and file no
  Container App — assert `onboard_sre_agent` never calls
  `az containerapp update --name dsf-sre-*`. The `squad:ready` label is still
  created by the existing `create_labels` step (unchanged).
- `tests/cli/test_control.py`: remove the `sre-sweep` tests; assert `sre-sweep` is
  no longer a subcommand.
- Delete `tests/sre/`. Whole suite stays green and offline; `grep -rn "dsf.sre" src`
  returns nothing.

## Docs

- **ADR 0009** — "Leverage the Azure SRE Agent product (supersede the bespoke SP5
  agent)"; sets ADR 0008 status to Superseded.
- **RUNBOOK** SRE section rewritten: how to onboard the Azure SRE Agent for a
  product instance (point at the rendered `sre-onboarding.md`).
- **Charter §6**: SP5 row + the "SRE agent tech" decision updated to "leverage the
  Azure SRE Agent product"; the dsf-native framing retired.

## Rollout

Single branch `refactor/azure-sre-agent` off `main`, TDD per task
(removal → renderer → provisioner step → docs), green + ADR before merge.
Closes #30. Independent of PR #29 (#25); minor `control.py` overlap (different
functions).
