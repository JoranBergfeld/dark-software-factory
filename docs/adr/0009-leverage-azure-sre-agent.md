# ADR 0009: Leverage the Azure SRE Agent product (supersede the bespoke SP5 agent)

- Status: Accepted
- Date: 2026-06-18
- Issue: [#30](https://github.com/JoranBergfeld/dark-software-factory/issues/30)
- Supersedes: **ADR 0008** (the bespoke SRE agent runtime). Builds on ADR 0004
  (ACA runtime / per-product Azure), ADR 0005 (no `src/` fakes), ADR 0007
  (council→squad handoff).

## Context

ADR 0008 (SP5) added a **bespoke** SRE agent: a deterministic
`observe → detect → fix_forward → reflect` sweep in `src/dsf/sre/`, run via
`dsfctl sre-sweep`, deployed as a custom `dsf-sre-<product>` Container App, and
observing **fixture** Sentry/Grafana telemetry. It hand-rolls — and only against
fixtures — what Microsoft's **Azure SRE Agent** product already does for real:
connect to a resource group's observability, autonomously investigate incidents,
and file GitHub issues / open PRs.

The directive is firm: **DSF must leverage the Azure SRE Agent product, not a
bespoke implementation.** The goal of ADR 0008 (close the production→squad loop)
stands; its *mechanism* (custom runtime) is replaced.

The Azure SRE Agent is onboarded through a **wizard** at `sre.azure.com` with
**browser-OAuth GitHub connection** and **interactive Azure resource-access
grants**. There is no clean headless `az`/Bicep path for those steps today, so
full IaC automation of onboarding is not achievable.

## Decision

- **Remove the bespoke runtime.** Delete `src/dsf/sre/`, `dsfctl sre-sweep`, the
  `SreBundle`/`render_sre_bundle` renderer, the `deploy_sre` provisioner step, and
  the `dsf-sre-<product>` Container App deploy. No SRE detection/fix-forward logic
  remains in `src/`.
- **Prepare + guide, don't fake-automate (Approach A).** The provisioner gains an
  **`onboard_sre_agent`** step (replacing `deploy_sre`) that:
  - **always renders** a deterministic, per-instance onboarding runbook
    (`runtime/<product>/sre-onboarding.md`) — the pre-scoped `sre.azure.com`
    target (product resource group + region), the repository to connect,
    the resource group to grant Reader, a model-provider note, and the
    `squad:ready` reminder;
  - has **nothing to headlessly execute** — the wizard/OAuth onboarding is
    interactive, and the prerequisites the agent needs (product RG, product repo,
    `squad:ready` label) are already established by the earlier `provision_azure`,
    `create_repo`, and `create_labels` steps. It issues **no fabricated**
    `az sre-agent create` command and never deploys a Container App.
- **Preserve the handoff contract.** Once onboarded, the Azure SRE Agent files
  issues/PRs into the product repo; keeping the `HANDOFF_LABEL = "squad:ready"`
  label means the **same** coding-squad Ralph watch loop (ADR 0012) picks them up — one
  intake for council and SRE, unchanged.
- **Stay offline-testable.** Rendering and the prerequisite steps run through the
  injected `run` callable; the suite asserts the runbook references the correct
  product RG/region/repo and that the step never deploys a Container App.

## Onboarding flow

```
dsf provision → create_labels (squad:ready) → onboard_sre_agent → renders sre-onboarding.md
        │
operator follows the runbook at sre.azure.com:
        create agent (product RG) → connect product repo (OAuth/PAT) → grant Reader on product RG
        │
Azure SRE Agent → investigates incidents → files issues/PRs (squad:ready + incident)
        → coding-squad Ralph watch loop (ADR 0012) → PR → review/auto-merge
        → incident issues also feed the feature council (ADR 0013, slow path)
```

## Consequences

- DSF stops maintaining a parallel, fixture-only SRE engine; production SRE is the
  Azure product's job, with real observability and real remediation.
- The interactive onboarding seam (wizard + OAuth) is **explicit and documented**
  rather than hidden behind brittle scripting — honest about what is and isn't
  automatable today.
- The closing loop is unchanged from the consumer's side: SRE-originated work still
  reaches the squad through `squad:ready`.
- If/when Azure ships a headless provisioning path for the agent + connector, the
  `onboard_sre_agent` step can automate more without changing this decision.
- The charter §6 SP5 row and the RUNBOOK SRE section are updated to describe the
  product-leverage approach; ADR 0008 is marked Superseded.
