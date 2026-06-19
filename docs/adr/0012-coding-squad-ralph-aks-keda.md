# ADR 0012: Coding squad runs as a per-product Ralph watch loop on AKS + KEDA

- Status: Accepted
- Date: 2026-06-19
- Fulfils: charter SP4 execution; builds on ADR 0007 (council->squad handoff),
  ADR 0004 (ACA runtime, contrasted), ADR 0010 (uv-workspace monorepo). Supersedes
  the `squad copilot --auto-assign` decision recorded in ADR 0007.
- Design: `docs/superpowers/specs/2026-06-19-coding-squad-ralph-aks-keda-design.md`.

## Context

ADR 0007 closed the council->squad handoff: S6 stamps `squad:ready` on every routed
issue, the provisioner creates the labels, and `squad triage --execute --label
squad:ready` is wired. Two things are wrong for a factory meant to run unattended:

1. **The squad never really runs.** `squad triage` is a one-shot provisioner step.
   It fires once at stamping; nothing picks up issues filed afterward. There is no
   standing process watching the repo.
2. **The Cloud Agent path is not the squad.** `squad copilot --auto-assign` hands
   triaged issues to GitHub's Copilot Cloud Agent, which runs plain Copilot. The
   squad's value, a team of specialist members each with its own charter and a
   memory that compounds per run, cannot be selected inside the Cloud Agent. That
   path keeps the label wiring but discards the product it hands off to.

## Decision

- **Run Ralph as a standing watch loop, not a one-shot triage.** Each product runs
  `squad watch --execute` (Ralph) continuously. Ralph polls for `squad:ready`
  issues, builds each member's context snapshot, dispatches `gh copilot -p
  context.md`, opens pull requests, and writes learnings back to `.squad/`.
- **Deploy it per product on its own AKS cluster, scaled by KEDA.** Ralph runs as a
  Kubernetes Deployment on a per-product AKS cluster in the product's resource
  group (matching the dedicated-RG charter). A KEDA ScaledObject scales the
  Deployment between 0 and 1 off an external metric: the count of open `squad:ready`
  issues. No work means zero replicas and no cost; one ready issue brings the
  watcher up; a drained queue scales it back to zero. The ceiling is 1 so two Ralph
  loops never race the same issues.
- **Drop the Cloud Agent auto-assignment.** The `squad copilot --auto-assign`
  provisioning step is removed. `squad init` and the handoff-label creation stay.
- **Rely on squad's built-in memory.** Knowledge iteration is squad's `.squad/`
  mechanism (per-member `charter.md` + `history.md`, shared `decisions.md`,
  `skills/`, the silent memory-manager member), all in git. DSF invents no store.
  The only AKS-specific concern is durability across ephemeral pods: Ralph runs with
  a persistent `--state-backend git-notes`, and the pod pushes `.squad/` updates
  back so the next pod starts from the compounded knowledge.
- **Governance is a per-product maturity dial.** Whether a squad pull request needs
  a human is an operator dial, set at provisioning and adjustable while the factory
  runs, mirroring the council's maturity dial. Low maturity requires human review
  (branch protection); high maturity enables GitHub auto-merge on green CI. The dial
  toggles repo settings, not Ralph's behavior.
- **Identity is a scoped GitHub App, not a PAT.** Ralph authenticates with a GitHub
  App installation token scoped to the product repo, delivered through AKS workload
  identity and the Key Vault CSI driver, reusing the per-product Key Vault.

## Consequences

- The squad becomes a live, demand-driven service per product instead of a single
  triage pass at stamping. Issues filed at any time are picked up; idle products
  cost nothing.
- DSF now operates two runtime platforms: Azure Container Apps for the council
  (ADR 0004) and AKS for the squad's Ralph loop. This is a deliberate trade for the
  control the watch loop needs; ACA was considered and set aside.
- The provisioner gains AKS, KEDA, and Ralph-deployment steps and a governance
  step, and loses `squad copilot`. New manifest rendering mirrors the council's
  `containerapp.yaml` render, and a per-product AKS + KEDA Bicep module mirrors the
  existing ACA module.
- The DSF test suite stays a unit suite for the logic it owns (manifest rendering,
  provisioner command batches, the issue-count helper, the dial mapping). Whether
  the loop actually runs on a real cluster is validated by a live deployment, not by
  the offline suite; the design says so plainly rather than over-claiming offline
  coverage (revisiting the ADR 0001 / ADR 0005 framing for this provisioning-side
  phase).
- The SRE-to-squad path is unchanged: incident issues reach the squad through the
  same `squad:ready` label (ADR 0008, ADR 0009).
- Implementation is staged in the follow-up plan
  (`docs/superpowers/plans/2026-06-19-coding-squad-ralph-aks-keda.md`); this ADR
  records the decision.
