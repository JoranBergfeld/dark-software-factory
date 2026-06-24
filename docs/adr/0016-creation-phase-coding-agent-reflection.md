# ADR 0016: Creation phase runs on the Copilot Coding Agent + a DSF-owned reflection loop on shared Cosmos memory

- Status: Accepted
- Date: 2026-06-22
- Fulfils: charter SP4 execution; #71. Supersedes ADR 0012 (coding squad as a per-product
  Ralph watch loop on AKS + KEDA). Keeps ADR 0007 (council->creation handoff label). Builds on
  ADR 0004 (ACA runtime), ADR 0006 (Azure adapters), ADR 0011 (deliberative council), ADR 0014
  (real-only `src/`).
- Design: `docs/superpowers/specs/2026-06-22-creation-phase-coding-agent-reflection-design.md`.

## Context

ADR 0012 ran the creation phase as the community Squad product, a Ralph watch loop on a
per-product AKS cluster scaled by KEDA. It worked, but it forced a fragile credential model:
the operator's full-scope `gh auth token` (a PAT) was seeded into the product Key Vault and
projected into the Ralph pod, giving that pod the power to push code and open PRs (#54,
`security/severity:high`). A long-lived, broad, static secret is the wrong primitive no matter
how narrowly we scope it, and the harness also carried operational debt (#55 issue-exporter
stall, #56 public AKS API server) and a second runtime platform.

The GitHub **Copilot Coding Agent** runs under GitHub's own managed, ephemeral identity, so DSF
never has to hold a credential that can write code. The one capability it lacks that Squad had
is the reflection loop — the compounding, self-improving knowledge that grounds each new task.
The council already owns the substrate for that loop: `CosmosMemoryStore` backs the
`MemoryStore` port with embedding retrieval and `get_lessons`, and `record_outcome` /
`feedback_watcher` already distill PR outcomes into product-scoped lessons.

## Decision

- **The Coding Agent is the primary executor.** Council S7 files the routed issue and assigns
  it to the Coding Agent. No DSF-held credential has code-push or PR-open power. The ADR 0007
  handoff label (`squad:ready`) is unchanged; only the executor behind it changes.
- **Identity, not tokens.** DSF->GitHub actions (file, assign, advisory review, set branch
  protection) run under **one DSF GitHub App**, created once interactively and installed
  per product repo; installation tokens are short-lived and repo-scoped. DSF->Cosmos runs under
  an Entra managed identity. The only remaining secret is the App private key, which merely
  mints scoped ephemeral tokens and is centrally revocable. No PATs.
- **Deterministic controls gate merges.** Whether a Coding Agent PR merges is decided by a
  real branch-protection ruleset (required status checks + required reviews) driven by the
  per-product **maturity dial** (`squad_maturity` -> `creation_maturity`): low requires a human
  approval + green CI; high auto-merges on required checks. LLM reflection is advisory and
  iterative only and holds no merge authority. This replaces #54's no-op governance.
- **One shared memory, namespaced.** The council, creation, and operation phases use the single
  Cosmos store, partitioned by a logical `namespace` (`council`, `coding/<member>`,
  `operation`); un-namespaced council calls default to `council`. The reflection loop is
  rebuilt as a DSF capability by extending `record_outcome`, not inherited from Squad.
- **Coding members are personas, not executors.** Six config-defined members (Architect,
  Implementer, Test-writer, Security-reviewer, Docs-writer, silent Memory-curator), each a
  charter used as pre-PR grounding and post-PR reflection, with a namespaced history slice.
  Building a second coding agent is a non-goal.
- **Grounding via MCP in ACA.** A Cosmos-backed MCP server in the existing ACA runtime (under
  the runtime managed identity) serves lessons + charters to the Coding Agent. Runtime
  consolidates back to ACA only.

## Consequences

- Removes the ADR 0012 harness: per-product AKS, the Ralph Deployment, the KEDA ScaledObject,
  the issue-exporter, the squad federated credential, and the `github-token` PAT in Key Vault.
  Resolves #54; moots #55 and #56.
- Adds a per-owner DSF GitHub App (one-time interactive bootstrap), a `GitHubAppClient` in
  `core`, a Cosmos-backed MCP server, the reflection/write-back job, and the assign-to-Coding-
  Agent + branch-protection wiring in provisioning. `squad_render.py` and its tests are deleted.
- Delivered in seven staged plans (design doc). Stage 1 (remove the harness) is independent and
  unblocks the rest; identity/handoff (2->3) and memory/grounding/reflection (4->5, 4+2->6)
  then run as two tracks. #54 is resolved once Stages 1 and 3 land; Stage 7 lands the docs and
  completes #71.
- **What** gets committed to memory and **when** is deliberately not redesigned here. #71
  reuses the existing write paths and adds the namespace; the write-policy reassessment (and
  its operator-facing docs) is tracked in #73.
- The unit suite stays a unit suite (`dsf_testing` doubles): provisioner batches, Bicep-render
  removals, App token-minting, namespaced memory, MCP tool contracts. Live assignment and ACA
  hosting are validated by deployment, stated plainly (ADR 0012 / ADR 0014 framing).
