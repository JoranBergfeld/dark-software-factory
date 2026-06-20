# Coding Squad: Ralph Watch Loop on Per-Product AKS + KEDA (Design)

**Status:** Accepted (2026-06-19); recorded as ADR 0012 and implemented, including
the live workload-identity wiring (AKS workload identity plus the Key Vault CSI
driver). This is the design charter; the plan and the code followed.

**Scope:** Define how the Coding Squad phase actually runs. Replace the one-shot
`squad triage` provisioner step and the GitHub Copilot Cloud Agent auto-assignment
with a continuously running Ralph watch loop, deployed per product on its own AKS
cluster and scaled by KEDA off the count of open handoff issues. Rely on squad's
built-in `.squad/` memory for knowledge iteration. Add a per-product maturity dial
that governs whether squad pull requests need human review or auto-merge on green
CI. This is the decision path for the execution harness, not an implementation
plan.

---

## 1. Problem

The handoff into the squad is closed (ADR 0007): the council files issues carrying
`squad:ready`, the provisioner creates the labels, and `squad triage --execute
--label squad:ready` is wired. But two things are wrong for a factory that runs on
its own.

- **The squad never really runs.** `squad triage` is a one-shot provisioner step:
  it fires once when the instance is stamped, then nothing picks up later issues.
  There is no standing process watching the repo.
- **The Cloud Agent path is not the squad.** `squad copilot --auto-assign` hands
  triaged issues to GitHub's Copilot Cloud Agent, which runs plain Copilot. The
  squad's whole value, a team of specialist members each with its own charter and
  memory that compounds per run, cannot be selected inside the Cloud Agent. Going
  that way keeps the label wiring but throws away the product it hands off to.

The goal is a standing execution model that runs the actual squad members, picks up
issues whenever they arrive, costs nothing when there is no work, and lets the
operator govern how far it runs unattended.

## 2. Principles

- **DSF owns the harness, squad owns the loop.** This extends the ADR 0007
  boundary. DSF owns the cluster, the scaling, the deployment manifests, the
  identity and secrets, the provisioning steps, and the governance dial. Squad owns
  the members, their `.squad/` memory, Ralph's polling, and the coding. The single
  `squad:ready` label stays the whole contract between them.
- **Run the real squad.** The execution model runs `squad watch --execute` (Ralph),
  which dispatches the squad members with their per-member context, not vanilla
  Copilot.
- **Pay for work, not for waiting.** The loop scales to zero when there are no
  handoff issues and scales up only when there is something to do.
- **Govern, do not micromanage.** Whether a squad pull request needs a human or
  merges on green CI is a per-product dial the operator sets and can change while
  the factory runs, in line with the council's maturity dial.
- **Rely on squad's memory, invent nothing.** Knowledge iteration is squad's
  built-in `.squad/` mechanism. DSF's only added concern is making it durable across
  ephemeral pods.

## 3. Execution model

Each product runs Ralph in watch mode as a long-running Kubernetes `Deployment` on
its own AKS cluster. A KEDA `ScaledObject` scales that Deployment between 0 and 1
replicas off an external metric: the count of open issues carrying `squad:ready`.

```
no ready issues        -> KEDA holds the Deployment at 0 replicas (no cost)
>= 1 ready issue        -> KEDA scales to 1; Ralph polls, builds each member's
                           context snapshot, dispatches `gh copilot -p context.md`,
                           opens pull requests, writes learnings back to `.squad/`
ready issues drain to 0 -> KEDA scales back to 0
```

The replica ceiling is 1. Two Ralph loops on the same repo would race for the same
issues, so the design runs exactly one watcher per product and lets Ralph's own
`--interval` polling and tiered remediation handle throughput inside that single
replica.

**Rejected alternative.** A one-shot `squad triage` Job triggered per event (a KEDA
`ScaledJob`) would also scale to zero, but it discards Ralph's in-loop monitoring,
remediation, and escalation, which are the watch-mode behaviors squad ships. The
standing daemon keeps them.

## 4. Topology, identity, and provisioning

**Topology.** Each product gets its own AKS cluster in the product's own resource
group, matching the dedicated-resource-group charter. On the cluster:

- the Ralph `Deployment` (watch mode),
- the KEDA `ScaledObject` (trigger: open `squad:ready` issue count, min 0, max 1),
- a small issue-count metrics exporter (a sidecar or `CronJob` that queries GitHub
  for the open handoff-issue count and exposes it for KEDA's `metrics-api` scaler to
  read).

**Identity and secrets.** Ralph needs GitHub credentials to read issues, run
`gh copilot`, open pull requests, and push `.squad/` updates. The squad pods assume a
per-product `squad-<product>` ServiceAccount bound to a dedicated managed identity
through an AKS federated credential, and read the GitHub token from the per-product
Key Vault the provisioner already stamps, projected by the Key Vault CSI driver. The
token is never written to the pod's filesystem as a static in-cluster secret.

The provisioner seeds that token by reading the operator's `gh auth token` and
writing it to Key Vault as `github-token` at execute time. Because the renderer and
the CSI `SecretProviderClass` reference the secret by name, swapping to a GitHub App
installation token scoped to the product repo, which is the recommended security
hardening, is a value change in Key Vault rather than a code rewrite. That App path
is not automated here only because GitHub App creation requires a one-time
interactive browser approval and has no headless REST equivalent.

**Provisioning changes** (`cli/src/dsf/instance/provisioner.py`):

- Keep `squad_init` (`squad init --preset default`) and `create_labels` (the
  taxonomy plus the `squad:ready` handoff label) unchanged.
- Remove `squad_copilot` (`squad copilot --auto-assign`). The Cloud Agent path is
  gone.
- Replace the one-shot `squad_triage` step with steps that provision the AKS
  cluster, install KEDA, and render then apply the Ralph deployment manifests. The
  watch loop now runs continuously on the cluster instead of once at provisioning.
- Manifests render the same way the council's `containerapp.yaml` and
  `.env.orchestrator` render today: a pure render step that produces the YAML, then
  `az aks` / `kubectl apply` invoked under `--execute` through the injectable
  runner. On `--execute` the provisioner first seeds the GitHub token into Key Vault,
  then applies the identity manifest (Namespace, ServiceAccount, and the Key Vault
  `SecretProviderClass`) before the namespaced exporter, Deployment, and ScaledObject.

## 5. Knowledge iteration

DSF invents no knowledge store. The `.squad/` directory is squad's memory, and it
is all in git:

- each member has a `charter.md` (identity, expertise, voice) and a `history.md`
  the member appends learnings to every run,
- the team shares a `decisions.md` log,
- `skills/` holds compressed learnings distilled from past work,
- a silent memory-manager member curates the above.

Knowledge compounds because members write back what they learned each run, and the
folder is committed, so a fresh clone gets the team with its accumulated knowledge.

The only AKS-specific concern is durability across ephemeral, scale-to-zero pods. A
pod that disappears must not take the team's memory with it. Two measures cover it:

- Ralph runs with a persistent watch state backend (`--state-backend git-notes`),
  not the default in-memory backend, which loses orchestration state on restart.
- The pod commits and pushes `.squad/` updates back to the repo each run, so the
  next pod, which may be a different one after a scale cycle, starts from the
  compounded knowledge rather than a stale snapshot.

## 6. Governance: the maturity dial

Whether a squad pull request merges on its own is a per-product maturity dial, set
at provisioning and adjustable while the factory runs, mirroring the council's
maturity dial.

- **Low maturity.** Branch protection requires human review. Squad pull requests
  wait for a person, which matches squad's own human-led stance.
- **High maturity.** GitHub auto-merge is enabled. A pull request that passes the
  required CI checks merges without a human.

The dial toggles branch-protection and auto-merge settings on the product repo. It
does not change Ralph's behavior; Ralph always opens pull requests, and the dial
decides what happens to them.

## 7. Testing

The offline-first stance from ADR 0001 and ADR 0005 was about the council runtime,
its stations and its model, memory, and config ports, being exercisable without
real cloud or LLM calls. This phase is provisioning and deployment code, a different
category, so the testing posture is stated honestly rather than borrowed wholesale.

- **Pure-logic unit tests, kept.** Manifest rendering (the Deployment, the
  ScaledObject trigger and bounds, the identity wiring), the provisioner command
  batches (the new AKS, KEDA, and apply steps emit the right commands and
  `squad_copilot` is gone), the issue-count exporter against a faked GitHub query,
  and the maturity dial mapping config to branch-protection or auto-merge commands.
  These are pure functions; testing them needs no network regardless of policy, and
  they are cheap regression protection.
- **The running harness is live-deployment-validated, not offline-proven.** Whether
  Ralph actually runs on a real cluster, picks up real issues, and opens real pull
  requests cannot be shown by the unit suite, and the design does not pretend it
  can. That validation is a live deployment activity, proven by deploying.

## 8. Out of scope

- Live AKS and live GitHub integration. The suite stays a unit suite for the logic
  DSF owns; real-cluster behavior is proven by deployment.
- The SRE-to-squad path. Incident issues reach the squad through the same
  `squad:ready` label (ADR 0007), but the SRE phase is its own design (ADR 0008,
  ADR 0009).

## 9. What changes in this repo

- `docs/adr/0012-coding-squad-ralph-aks-keda.md` (new). Records this decision,
  extends ADR 0007, and supersedes the `squad copilot --auto-assign` choice.
- `docs/phases/coding-squad.md`. Rewrite the invocation and knowledge sections to
  this model and add a mermaid diagram of the loop, as the feature-council phase doc
  has.
- `cli/src/dsf/instance/provisioner.py` and its tests. Drop `squad_copilot`, replace
  the one-shot triage step with the AKS, KEDA, and Ralph deployment steps.
- A new manifest renderer and its templates (Deployment, ScaledObject, exporter)
  under the instance tooling, with render tests alongside the existing runtime
  render tests.
- `infra/`. A per-product AKS plus KEDA module in Bicep, mirroring the existing
  Azure Container Apps module.
