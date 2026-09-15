# Code-derived product intent for `dsf onboard decide`

Research date: **2026-09-15**. Question: [Establish evidence-grounded product intent from repository code](https://github.com/JoranBergfeld/dark-software-factory/issues/198), under [Onboard existing applications with dsf onboard decide](https://github.com/JoranBergfeld/dark-software-factory/issues/189). Findings inform [Define product intent and evidence readiness](https://github.com/JoranBergfeld/dark-software-factory/issues/192).

**Status:** research findings and technical options, not an approved intent policy, storage contract, activation decision, or implementation. No target application was selected. This document does not describe DSF's own product intent as a substitute for an application's intent.

Scope: a **factory agent** automatically examines an existing application's available repository and produces a defensible, source-cited account without requiring the operator to author or supply intent. The attachment remains Decide-only: an isolated Feature Council, a dedicated Azure application environment, proposal-only issues, and external delivery handoff. Repository mutations and consent are owned by [#196](https://github.com/JoranBergfeld/dark-software-factory/issues/196), not this investigation.

## Findings that determine the feasible routes

1. **Automatic evidence-grounded description is feasible; recovery of unknowable intent is not.** Source and tests expose implemented rules and asserted expectations; documentation can record explicit intent. Inferring why those things exist remains a hypothesis. No amount of reading determines undocumented strategy, desired changes, or non-goals. A model can still produce incorrect conclusions from supplied material. [S19] The distinctions below are an analytical discipline, not a claim of an intent-recovery algorithm.
2. **Inventory, acquisition, analysis, and understanding are different claims.** A pinned Git tree can support an auditable path inventory. Every file/chunk needs a recorded disposition. Search results, even with citations, do not prove repository-wide reading; even complete reading does not prove complete understanding. GitHub expressly disclaims exhaustive Code Search and documents independent tree/content limits. [S1]-[S5]
3. **Current .NET DSF has useful seams, not this capability.** It serves three source kinds, validates evidence attribution, and checks proposal reference membership. Foundry IQ retrieves bounded grounding from a configured knowledge base; it neither inventories a Git repository nor proves all code was processed. Current runtime sweeps do not read, sync, or amend charters. [L1]-[L5]
4. **The existing authority conflict must stay visible.** ADR 0017 makes human-owned `.dsf/charter.md` authoritative; ADR 0018 permits optional, human-gated amendments to an existing valid charter, not silent replacement or initial automatic adoption. Producing a non-authoritative research result does not itself change either decision. [L6]-[L8]
5. **A read-only route needs no repository writes.** Installation-scoped `Metadata: read` and `Contents: read` cover repository identity and Git source reads. Issues/PR evidence is optional and separately permissioned. Access must already be consented; the ingestion agent cannot enlarge installation scope. [S12] [S13]
6. **Incremental refresh is practical but not approval inheritance.** Compare complete old/new tree inventories and reuse unchanged blob work. GitHub's comparison endpoint caps changed-file output at 300, so paging commits is not a complete change detector. Changed evidence must produce a new result with explicit invalidations, not silently rewrite governing intent. [S1] [S11]

## 1. Evidence baseline and research boundaries

Local implementation observations are pinned to **`b427b762e7f5a9982b64059c995802d8b8d5920c`**, not the working copy of `main`. Sources include the current .NET source-kind registry, served-agent gatherer, Foundry IQ adapter, conveyor contracts/grounding, charter parser/commands, operator documentation, and ADRs. These are targeted prior-art reads, **not an assertion that this research read every DSF file**. Root `CONTEXT.md` had unrelated unstaged changes and was not modified or used as the pinned implementation baseline.

External facts were checked against official GitHub/Git documentation, GitHub's OpenAPI source, Tree-sitter source, and Microsoft documentation. GitHub REST references below explicitly name **`2026-03-10`**; an implementation must select and test its API version rather than inherit a moving documentation default. The OpenAPI snapshot used was `github/rest-api-description` at **`022b4dc2d10306c8c583368af92e3296598c6ce3`**. It verifies response descriptions/shapes, but its `enabledForGitHubApps` flag alone does not establish token types or permissions; the official App-permission table was checked separately. [S12] [S20]

Planning constraints come from the research ticket and the [human-confirmed identity decision](https://github.com/JoranBergfeld/dark-software-factory/issues/194#issuecomment-5676220098): immutable repository ID; observed owner/name/actual default branch; remote-authoritative factory manifest in product App Configuration; one repository/product and one dedicated application environment. That manifest decision **does not specify storage for inferred intent, raw code, an index, or approval records**.

No target code was executed, no target repository/Azure configuration was changed, no credentials were inspected or extracted, and no private application code was sent to public web/research services. This research neither operated a factory nor certified an application's evidence readiness.

## 2. What the factory can and cannot conclude

Recommended output separates four epistemic classes. These are proposed reporting categories, not a new DSF domain contract.

| Class | Defensible statement | What it does not establish |
| --- | --- | --- |
| **Observed repository behavior** | A pinned handler, data flow, UI, configuration default, or test encodes a particular behavior under stated conditions. Identify static inspection versus an assertion in an unexecuted test. | That the code is deployed, reachable, enabled, correct, used by customers, or desired. Do not describe an unexecuted path as an observed production event. |
| **Inferred purpose** | A plausible user problem, actor, workflow, or product capability supported by linked implementation and other evidence; include alternatives and confidence rationale. | Human approval, actual user demographics, market strategy, or a commitment to retain or expand that behavior. |
| **Explicit recorded intent** | A document explicitly states a goal/non-goal/constraint. Preserve wording, scope, author/review evidence if available, revision, and any supersession notice. Distinguish a repository assertion from verified human-authored/approved intent. | That every README, comment, ADR, or file named `charter` is current, human-authored, or authoritative. A Git signature is not approval of a product strategy. |
| **Unknown** | Evidence is absent, inaccessible, contradictory, unsupported, or insufficient to distinguish competing explanations. | Permission to fabricate a goal, replace the unknown with industry convention, or demand that the operator write a charter to complete the research. |

### Evidence value by source

| Repository material | Useful evidence | Frequent ambiguity |
| --- | --- | --- |
| Entry points, routes, screens, domain operations | Available workflows, roles, validations, integrations, observable outputs encoded in source | Dead paths, incomplete features, experiments, legacy compatibility, runtime dispatch and feature flags |
| Tests, fixtures, snapshots | Concrete asserted behavior, edge cases, vocabulary and invariants under test setup | Unrun/failing/skipped tests; mocks and fixtures are not production facts; existing behavior may be a regression |
| README, ADRs, specifications, existing charter/context | Stated users, rationale, desired behavior, constraints and non-goals | Staleness, copied templates, conflicting audiences/versions, generated prose, unapproved drafts |
| Schemas, migrations, API contracts | Entities, relationships, state constraints, public contracts and historical evolution | Storage shape is not business strategy; migration history is not current runtime state |
| Configuration templates, manifests, infrastructure and pipelines | Declared dependencies, selectable modes, deployment candidates, required configuration names | Defaults differ from effective settings; secrets and runtime state are unavailable; do not resolve secret values |
| Dependency/lock files, generated/vendor material | Versions, integration surface, imported protocol/schema behavior, generated public APIs | Installed library capability does not prove application use; duplicated generated files must not dominate inference |

These are static-analysis conclusions, not experimentally measured inference accuracy. A defensible account triangulates sources and preserves their conditions. Documentation says what somebody recorded; implementation says what this revision encodes; tests say what an assertion expects. None alone decides what should happen next.

**Negative claims need particular restraint.** "No implementation found in the analyzed scope" is not "the product must never support it." Absence of billing code, for example, cannot establish a non-commercial strategy. Only an explicit, appropriately attributed statement can evidence a recorded non-goal; its authority remains separate. Avoid confidence percentages that imply calibration which has not been demonstrated. Microsoft's own transparency guidance warns that models may lack knowledge and produce factually inaccurate information. [S19]

## 3. Current .NET prior art versus policy

| Surface at the pinned DSF revision | Verified capability and limit |
| --- | --- |
| `SourceAgentKinds`, `SourceIntegrationRegistry`, runtime composition | Known kinds are exactly `azuremonitor`, `foundryiq`, `webiq`; adapters are explicitly registered and the composer wires configured A2A endpoints. There is no registered repository/code-intent source kind. This requires future implementation, not merely a new query setting. [L1] |
| `SourceAgentEvidenceGatherer`, `EvidenceItem` | `/gather` replies must match product and source kind, contain an evidence array, and provide nonblank reference/summary strings. `EvidenceItem` has `SourceKind`, `Reference`, `Summary`; it has no typed repository revision, coverage ledger, claim class, conflict, or approval fields. This is a reusable transport/evidence seam, not a complete provenance contract. [L2] |
| `FoundryIqIntegration` | Sends a configured semantic intent to Azure AI Search knowledge-base retrieval using API `2026-04-01`. Rejects non-200/incomplete/error-bearing responses and malformed references; maps extractive chunks to source-scoped references. Search-index references use endpoint/knowledge-base/source/document identity, not an inherent Git commit. Repository provenance would have to be supplied and preserved explicitly. [L3] |
| `S4Grounding` | Drops proposals with zero references or references absent from the run's gathered evidence. It does **not** check that a source entails a claim, that the source is current, or that every repository file was analyzed. Reference membership is weaker than semantic grounding. [L4] |
| Runtime/charter integration | Current operator documentation explicitly says sweeps do not read or amend the charter. The conveyor service/composition has no charter collaborator. ADR descriptions of runtime charter enrichment/amendments must not be advertised as working .NET onboarding capabilities. [L5] |
| `Charter`, `StoredCharter`, `CharterMarkdown` | Typed vision/users/goals/non-goals/metrics/constraints/glossary; sync states `OK`, `STALE`, `MISSING`, `INVALID`; source SHA/ref metadata. Parser requires nonempty vision and users, at least one goal and metric, and expected headings/schema. It validates document shape, not truth, human approval, or complete evidence. Filling mandatory sections with invented strategy would defeat the research objective. [L7] |
| CLI charter commands | `init` collects intent interactively and opens/reuses a PR; it is not automatic code inference. Sync reads a file/ref, is idempotent on **blob SHA**, and preserves the last good charter on missing/invalid input. `sync`/`status` default the ref to `main`; this is prior art to avoid copying into onboarding. A blob SHA identifies file content, not the whole repository revision. [L8] |

**Authority:** ADR 0017 says humans author/amend `.dsf/charter.md`, which is product-intent source of truth; charter input is untrusted and its council influence is advisory. ADR 0018's later exception allows an opt-in amendment PR against an existing `OK` charter with evidence/cooldown/open-PR gates and human approval, never autonomous application. Its Python symbol references and sweep behavior are historical implementation detail; the current .NET limitation above still applies. [L6] [L5]

`charter init`, `charter implement`, and amendment PRs are therefore **not ingestion mechanisms** for this research. In particular, `implement` belongs to build handoff, not attaching Decide to an existing application. [L5] Nothing here authorizes a target-file write or an amendment PR.

## 4. Feasible repository-ingestion routes

All routes first resolve the selected repository identity and a commit, then read that snapshot. A branch is mutable; repeated reads by branch name can mix revisions. Git refs point at commits, commits reference trees, and trees identify blobs/subtrees by object ID. [S1] [S14] [S15]

| Route | Advantages | Concrete limits and suitability |
| --- | --- | --- |
| **GitHub REST trees + blobs** | Explicit object identities, no checkout/build, clear per-object accounting; straightforward read-only App access | Recursive tree response: at most 100,000 entries or 7 MB. On `truncated=true`, restart traversal with non-recursive trees, fetching each subtree. **Omit** `recursive`; even `recursive=false` requests recursion. Inspect every response for truncation/error. Blob GET supports up to 100 MB. Large corpora incur many calls and base64 overhead. Good candidate for an auditable first implementation, not a selected design. [S1] [S3] [S12] |
| **Isolated Git object fetch / bare or no-checkout clone** | Efficient pack transfer, local inventory with `git ls-tree -r -t -l -z --full-tree <commit>`, raw object reads with `git cat-file`; content-addressed reuse | A bare clone has no checkout; a shallow clone limits history, not necessarily the selected snapshot's tree; partial clone can omit blobs until fetched, and sparse checkout is not whole-tree acquisition. Enumerate the commit tree, verify required objects, and account for fetch failures. Harden Git configuration/transport and do not run filters/hooks or target build tools. Better scaling option requiring more operational controls. [S6] [S7] [S13] |
| **Commit-pinned source archive** | Bulk snapshot download without full Git history | Not the canonical coverage ledger. GitHub archives use `git archive`; `export-ignore` can omit tracked files and `export-subst` can transform bytes. LFS inclusion is configurable. Reconcile archive entries and content to the tree/blob inventory, recover discrepancies through object reads, and enforce safe extraction/resource limits. An archive alone cannot prove all tracked source was read. [S8] [S9] |
| **GitHub search / existing indexed retrieval** | Useful navigation, claim-directed follow-up, cross-source retrieval | Not an inventory or snapshot guarantee. REST Code Search and web Code Search have different limits; a query's results are not all files. An existing index needs its own ingestion/provenance ledger and revision isolation. Retrieval should supplement an accounted corpus, not define it. [S4] [S5] [S16] |

### Do not conflate these GitHub limits

| Surface | Documented limit; implication |
| --- | --- |
| Contents directory GET | Upper limit 1,000 files per directory; use Trees API rather than presume a complete directory listing. [S2] |
| Contents file GET | Up to 1 MB: all endpoint features. Between 1-100 MB: raw/object media types only; object `content` is empty with `encoding=none`. Above 100 MB: unsupported. Empty `content` is not proof of an empty file. Download URLs expire and are unsuitable durable citations. [S2] |
| REST Code Search | Up to 1,000 search results, at most 100/page; only default branch, files smaller than 384 KB, at least one search term, possible `incomplete_results`; authenticated code search limited to 10 requests/minute. These are REST limits, not the web UI's limits. [S4] |
| Web Code Search | Non-exhaustive; excludes generated/vendor, empty, binary, non-UTF-8 and files over 350 KiB; truncates lines over 1,024 characters and excludes files with more than one line over 4,096 bytes. Very large repositories may not be indexed; default branch only, results capped at 100. Never treat this as full acquisition or an arbitrary-revision API. [S5] |
| Compare commits | Without paging, up to 250 commits; with paging, changed files appear only on the first page, up to 300 for the entire comparison. Binary diffs can lack `patch`. Fetching every commit page still does not enumerate every changed file. [S11] |

If a tree still cannot be completely enumerated after documented subtree traversal, record an **incomplete inventory** and use a verified Git-object route or stop with that gap. Do not invent pagination for Trees API, declare that 100,000 files is the repository's size, or claim a bounded tool response included omitted material. Tool-output/context truncation is an additional limit beyond the upstream API.

### Scope and coverage ledger

Proposed procedure, independent of where the eventual artifacts are stored:

1. **Identify the snapshot.** Validate the immutable repository ID against the selected factory boundary; record observed owner/name/default branch and acquisition time. Resolve the actual selected ref once to a full commit SHA and root tree SHA. Use those identities for subsequent reads. A default-branch snapshot is not evidence of the deployed Azure revision; record deployment correlation as unknown unless separately established. [S14] [S15]
2. **Enumerate before selecting.** Walk every available tree entry at that revision, including dotfiles, tests, docs, configuration templates, generated/vendor paths, symlink blobs and gitlinks. Record path, mode, type, object ID, byte size where known, and traversal state. Deduplicate blob downloads, not path identities. Do not let a search tool's ignore rules determine the inventory. [S1] [S7]
3. **Classify without hiding exclusions.** Assign every path a disposition and reason; distinguish eligible first-party source, generated/vendor, binary/unsupported, sensitive/excluded, external dependency, unavailable, failed and pending. An exclusion is not a successful read. Repository `.gitignore` governs intentionally untracked files; tracked files remain tracked. Files never committed or not accessible to this principal cannot be recovered from a tree. [S10]
4. **Acquire and verify.** Read eligible blobs, validate object identity and byte lengths, detect pointer files, decode explicitly, and record failures/redactions. A fully downloaded file is not automatically fully analyzed. Bound per-file, total bytes, requests, memory, decoding and retry work; exhausted limits yield a visible partial result, never a shortened success.
5. **Analyze every eligible region.** Traverse all eligible code, not only likely entry points. Use deterministic chunk scheduling with source byte/line ranges and per-chunk completion status; record partial reads, unsupported syntax and model truncation. Preserve separate counts for indexed, supplied-to-model, summarized and claim-checked material. Summaries are lossy intermediate artifacts, not substitutes for source provenance. [S17]
6. **Reconcile at completion.** Every enumerated entry has a disposition; every eligible chunk has a terminal success/failure record. Report coverage by file count **and bytes**, language and subsystem, with raw totals and excluded/unavailable totals. If tree enumeration is incomplete, the global denominator is unknown: do not publish a fabricated percentage. Recheck ref movement only to report staleness; never mix a newer revision into the in-progress snapshot.

Useful distinctions are **inventory complete**, **acquisition complete for declared eligible scope**, **analysis complete for that scope**, and **claims checked**. None means "all intent known." "Read all available code" is supportable only with its revision, scope, complete eligible-source processing ledger, and visible exclusions; generated/vendor/sensitive omissions must not disappear behind a 100% scoped metric.

### Material outside ordinary text blobs

- **Submodules:** tree mode `160000` is a gitlink, not the dependency's contents; record the referenced commit, declared URL and availability. Parent-repository consent does not authorize a second repository or another host. Follow only separately approved sources with verified identity/access; otherwise disclose the dependency gap. Never recursively execute `.gitmodules`-derived fetch commands. [S1] [S2] [S13]
- **Git LFS:** a Git blob may contain only a pointer with object ID and declared size. Acquiring the pointer is not acquiring the asset. Record both Git blob ID and LFS object ID; any future LFS download must verify bytes, access, endpoint and quotas separately. Archive inclusion is configurable, and LFS objects can greatly exceed ordinary blob limits. This research does not establish a universal LFS download/authentication route for every hosting configuration. [S3] [S9]
- **Generated/vendor files:** retain inventory and provenance, classify explicitly, and distinguish a generator from its checked-in output. They may contain important protocol definitions or patched behavior. If excluded from semantic analysis, describe that limit and its effect on inferred capabilities; do not claim all code was analyzed. Do not generate missing output by running the project.
- **Symlinks and unusual paths:** preserve type and original blob bytes; do not follow filesystem links outside the approved snapshot. Contents API can dereference a symlink to a normal repository file, unlike an explicit raw symlink-blob read. Use safe path encoding/NUL-aware Git inventory and guard case/normalization collisions in any local projection. [S1] [S2] [S7]
- **Unavailable material:** omitted history, other branches, unmerged changes, release assets, separate wiki repositories, inaccessible submodules, ignored local files, runtime settings/data and external services are not included merely because someone says "complete repository." List applicable known gaps and an explicit scope for refs/history. Current-snapshot code need not require all historical commits, but material on other refs must not silently become evidence of current behavior.
- **Multiple applications:** inventory can expose unrelated components; one repository does not itself prove one product. An ambiguous product boundary is a finding under the map's one-product limit, not permission to silently select a folder or onboard multiple products.

## 5. Language support and grounded synthesis

### Capability tiers, not an "all languages" promise

| Tier | Feasible tooling | Honest boundary |
| --- | --- | --- |
| Byte/text inventory | Git objects plus explicit decoders | Broad file acquisition is not programming-language understanding. Binary, unusual encodings and oversized content need explicit support or gaps. |
| Syntax-aware extraction | Preinstalled, pinned Tree-sitter grammars: declarations, imports, syntax-based chunks | Tree-sitter builds concrete syntax trees and supports error recovery. An available binding is not an available grammar; a grammar is not a framework-aware analyzer. Record grammar/version, `ERROR` and `MISSING` regions, mixed-language/template gaps. It does not by itself resolve runtime behavior or product purpose. [S21] |
| Language semantic analysis | For example, Roslyn exposes separate C#/Visual Basic syntax, symbol and semantic APIs | Requires the relevant source, references and compilation options to interpret symbols accurately. Missing assemblies, conditional symbols or generated source reduce precision. Use trusted parser/compiler APIs on data, not scripting, arbitrary analyzers/generators, or project build execution. No support for other languages follows merely from DSF being .NET. [S22] |
| LLM-assisted synthesis | Bounded, source-linked chunks and verified follow-up reads | Model fluency is not a supported-language matrix or proof of semantic correctness. Evaluate each declared language/framework tier; unsupported code may be text-analyzed with explicit reduced assurance, not represented as fully resolved. [S19] |

Reflection, dynamic imports, dependency injection, configuration-driven routes, macros, native interfaces and feature flags can leave static relationships conditional or unresolved. An implementation should report those conditions rather than "complete call graph." A no-execution boundary means missing generated/runtime evidence stays missing.

### Synthesis options

| Option | Useful outcome | Tradeoff |
| --- | --- | --- |
| One whole-repository prompt | Simple for a genuinely small, verified-to-fit corpus | Must budget instructions, source and output together; rejected/truncated input is a failure. Does not scale and offers no understanding guarantee even when it fits. [S17] [S19] |
| **Accounted per-file/chunk extraction, then subsystem/product synthesis** | Every eligible source region gets a bounded pass; structured observations carry citations into higher-level summaries | More model calls, resumable work and cross-file reconciliation. Summaries can omit or distort details, so final claims must return to original blobs. Feasible candidate for the requested coverage, not an approved implementation choice. |
| Retrieval-augmented synthesis over a complete versioned corpus | Efficient follow-up questions, contradiction search and focused evidence expansion | Top-ranked retrieval cannot prove all code was read. It needs the independent ledger and initial coverage pass; embedding a file is not the same as analyzing its meaning. [S16] [S17] |
| Syntax/semantic graph plus synthesis | Better connection of entry points, tests, schemas and dependencies | Higher language/framework-specific engineering cost; unresolved edges remain unknown, and graphs cannot recover undocumented strategy. [S21] [S22] |

Foundry IQ is an optional retrieval component, not an alternative to ingestion. Official Azure AI Search documentation describes relevance ranking and returning the best results; the `2026-04-01` retrieve API has output-token/runtime limits and partial responses, not a full-corpus enumeration contract. Its reference/activity records are valuable but need a mapping back to Git repository/commit/blob/ranges. Current DSF validates reference structure, not that a knowledge base is a complete, single-revision code corpus. [S16] [L3]

### Proposed automatic synthesis discipline

1. Extract source-local observations first, preserving conditions, exclusions, explicit statements and their provenance. Treat tests as assertions, documentation as statements, and code as implementation evidence.
2. Group observations by candidate actor/workflow/capability; trace important claims across entry points, implementation, tests, schema and documentation where available. A claim needs enough evidence for its scope, not an arbitrary citation count.
3. Generate inferred-purpose hypotheses separately from explicit recorded intent. State alternatives, confidence rationale and unanswered strategy/non-goals. The factory writes the result; an operator need not supply missing prose.
4. Search the accounted corpus for counterevidence and conflicting definitions, and revisit original source for each material final claim. Verify paths, object IDs, quoted ranges, actual support and scope, not only URL existence. Preserve claim-to-source links through summary/retrieval transformations.
5. Reconcile contradictions as below, then emit a descriptive result and coverage/uncertainty report. Do not create a charter, populate required charter fields by guesswork, or activate a council as a side effect.

**Contradiction handling:** retain both sides with pinned references; classify documentation-versus-code, code-versus-test, competing explicit statements, or revision/environment mismatch. Apply an explicit supersession statement only within its stated scope. Do not make "newest file wins," majority vote, repeated/generated text, or model confidence into an authority rule. A current charter can remain the governing intent while code demonstrably diverges from it; describe the divergence, not a replacement intent. Missing evidence and conflicting evidence are different states. Consequential unresolved conflicts go to the existing human decision surface, without making the operator author an initial intent document. [L6]

## 6. Output and provenance a later decision can use

The following is a **proposed information checklist**, not a JSON schema, database design, App Configuration key contract, or approved intent format.

| Information | Minimum useful content |
| --- | --- |
| Snapshot identity | Factory/product boundary reference; immutable GitHub repository ID; observed owner/name/default branch; exact selected ref, commit SHA and root tree SHA; observation time; GitHub API version/reader version; access scope without credential values |
| Human-readable account | Candidate product purpose, actors, supported workflows/capabilities, encoded constraints, explicit recorded goals/non-goals if found, and unknown strategy; each classified as observed/inferred/explicit/unknown |
| Claim record | Stable claim identifier, exact proposition and scope/conditions; supporting and contradicting source references; inference rationale/alternatives; qualitative confidence with reasons; unresolved questions; no implied approval |
| Source reference | Repository ID, commit SHA, path, mode, blob ID, byte/line ranges and content digest; commit-pinned GitHub blob URL where available; extraction/redaction transforms and mapping back to original bytes; LFS/submodule identity where applicable |
| Coverage evidence | Complete/incomplete inventory; per-path acquisition/classification; per-chunk analysis status; file/byte totals by category, language and subsystem; exclusions, unresolved trees, missing content, unsupported syntax, truncation and limits hit |
| Analysis provenance | Tool/grammar/parser versions, model deployment/version where exposed, prompt/template and pipeline versions, chunk IDs, retrieval query/reference mapping, budgets and completion/error states; enough to audit inputs without promising deterministic model replay |
| Freshness/difference | Prior result identity, old/new commits, changed/removed sources, affected/retracted claims, carried-forward evidence, conflict changes, known branch/deployment drift, last successful acquisition and access status |
| Governance separation | Explicit label that this is generated descriptive evidence, not approved governing intent; existing authoritative-intent provenance if recognized; any later review/approval references kept distinguishable |

A commit-pinned URL is a source locator, not guaranteed perpetual availability or an authorization grant. Access can be revoked and private content must remain private. If audit retention requires retained source fragments or raw snapshots, their location, access controls, retention and deletion rules require a separate decision; citations do not silently authorize copying all code indefinitely.

**Avoid the single-score trap:** keep coverage, source confidence, contradiction severity and governance status separate. "All eligible chunks processed" cannot stand in for "the inferred strategy is correct" or "the council is ready." Likewise, charter `OK` is currently a parse/sync condition, not semantic approval. [L7] [L8]

## 7. Read-only GitHub and factory capability minima

Assume only a **verified existing App installation or browser/admin consent**, as already constrained by the map. Successful operator CLI reads do not prove the factory installation has the same access. Installation tokens are limited to granted repositories/permissions, can be narrowed further, and expire after one hour. Token generation is credential issuance, not permission to mutate the repository. No token values belong in logs, remote URLs, output assets or model inputs. [S13]

| Need | Read capability; scope |
| --- | --- |
| Identify selected repository | `GET /repos/{owner}/{repo}` with `Metadata: read`; verify returned ID and observe current metadata. Installation tokens are supported. [S12] |
| Pin source revision | `GET /repos/{owner}/{repo}/git/ref/{ref}` and `/git/commits/{commit_sha}` with `Contents: read`; validate exact ref/type and extract commit/tree identities. [S12] [S14] [S15] |
| Inventory and acquire ordinary source | `GET .../git/trees/{tree_sha}`, `GET .../git/blobs/{file_sha}`, optionally `GET .../contents/{path}?ref=<commit>`; `Contents: read`. No `Contents: write`, workflow/admin rights, branch creation or PR permission is needed. [S12] |
| Bulk alternative | Archive GET or HTTP-based Git access under `Contents` permission; constrain the reader to reads and approved hosts/objects. Do not copy documentation examples that embed credentials into logged command URLs. [S12] [S13] |
| Optional rationale outside Git files | Issue reads: `Issues: read`; PR reads: `Pull requests: read`. Enumerate comments/reviews/pages as appropriate and record access gaps. These are not required for the repository-code baseline; mutable issue/PR text needs item identity, observation time and retained-content policy, not a fictitious commit pin. [S12] |
| Optional refresh aid | Compare GET under `Contents: read`, with its documented limits; complete tree comparison remains the inventory check. [S11] [S12] |

The official permission table explicitly lists App installation-token support for these REST operations. `Metadata: read` plus `Contents: read` is the ordinary Git-source minimum, **not a guarantee of all submodule/LFS/external data**, runtime telemetry access, model availability, or organization/network compatibility. Public access is not proof of installation membership.

Factory capabilities needed beyond GitHub: identity-bound fetch orchestration; durable/resumable coverage accounting; bounded safe decoding/parsing; approved model access and sufficient context/output budgets; source/claim verification; explicit errors and freshness reporting. A semantic index is optional, not a prerequisite for the descriptive research. Its provisioning and storage would require their own approved design.

Separate the source reader from the issue filer. Proposal-only issue permission and trigger behavior are for the coordination decisions, not reasons to give the ingestion model repository write tools. No repository mutation probe is needed to prove reads.

## 8. Repository content is untrusted data

ADR 0017 already calls charter prose a prompt-injection surface. Microsoft's Prompt Shields documentation similarly identifies attacks through third-party documents and notes that safeguards can be bypassed. Envelopes and filters are useful mitigations, not a security boundary or a proof that all malicious content was detected. [L6] [S18]

Recommended constraints for a future reader:

- **Do not execute the application:** no tests, builds, package restores/install scripts, migrations, notebooks, generator tasks, repository-provided plugins, agent skills or instruction files. Read `AGENTS.md`, `CLAUDE.md`, comments and examples as evidence only; they cannot redefine the worker's tools, authority or destinations.
- **Keep acquisition deterministic and separately authorized:** repository text cannot choose arbitrary URLs, follow external dependencies automatically, change the selected repository/revision, request secrets or widen permissions. Validate redirects and external-object hosts against the approved boundary. Read-only Git credentials still permit exfiltration if the model can send code to arbitrary destinations; restrict outbound tools/network independently.
- **No credential inspection:** known secret-bearing files and sensitive regions are excluded/quarantined before model use; record only a safe disposition, not values or excerpts. Do not resolve Key Vault references, query Actions secrets, inspect environment credentials, or run a secret-hunting task to make the report look complete. If unexpected credential material is encountered, suppress it and expose the resulting evidence gap. Configuration names/contracts may be useful without their values.
- **Use trusted, pinned parsers as data processors:** resource limits, safe archive paths, no external entity/remote-reference resolution, no target-supplied analyzers/macros/code execution. For Git-object ingestion, use controlled configuration/templates and raw object reads, not `--textconv` or `--filters`, which invoke content transformations. [S6]
- **Treat model output as untrusted too:** validate references/structure, distinguish instructions quoted as evidence from actionable commands, and give synthesis no repository-write or activation capability. Generated summaries must not become higher-trust instructions on a later pass.
- **Keep private source inside approved factory processing boundaries:** public web research should contain only generic technology questions, never code, private identifiers, snippets, prompts or embeddings from the target. A future model/search service needs explicit data-handling authorization; "RAG" or "Azure" alone is not such authorization. Do not claim a data residency or retention guarantee for an unspecified deployment.

These are architectural recommendations, not a security audit of a target or an implemented protection claim. Excluding sensitive/unsafe material is compatible with honest coverage only if that exclusion remains visible.

## 9. Feasible incremental refresh

Proposed pull-only-compatible algorithm; cadence, persistence and activation effects remain undecided:

1. Revalidate repository identity/access and observe actual default-branch metadata. Resolve a new immutable commit/tree; record a branch rename, force-push or deployment-correlation gap instead of assuming `main` or ancestry.
2. If the commit/tree is unchanged, reuse applicable acquisition work, but do not treat a code match as a fresh access grant, deployment match, or approval. ETags/conditional authenticated GETs can reduce polling cost where the endpoint supports them. GitHub recommends webhooks rather than polling, and bounded efficient polling when webhooks cannot be used; this research does not introduce webhook setup or target mutations. [S23]
3. Compare complete old/new path-to-object inventories. Reuse unchanged raw blobs and compatible extraction results within the authorized factory/repository boundary; account for additions, deletions, mode changes, moves, changed submodule commits and LFS pointers. Identical blob bytes can be reused, but path/context-sensitive analysis may still need recomputation. Keep indexes/caches revision- and access-scoped so reuse cannot mix products or re-expose revoked evidence. The 300-file compare response is only an optimization hint, never the entire change set. [S1] [S7] [S11]
4. Reanalyze changed sources and dependent claims/subsystem summaries, including counterevidence searches. Changes to a charter, schema, feature flags, entry-point wiring, classification rules, parser/grammar, chunking or model/prompt versions can invalidate more than one file. If dependency impact cannot be bounded, rerun the broader synthesis instead of retaining unjustified claims.
5. Emit a new result linked to the old one: changed/retracted claims, new conflicts, coverage delta, carried-forward evidence and limitations. Preserve old provenance according to the as-yet-undecided retention policy. Never rebase old citations onto current line numbers.
6. On revoked access, failed trees/blobs, throttling, partial extraction or stale index, report the failure explicitly. A last successful result can be identified as historical, not represented as fresh/current authorization. Do not silently broaden credentials or sources to complete refresh.

GitHub documents respecting `retry-after`/rate-reset headers, backing off secondary limits, and not ignoring repeated errors; a private-resource `404` can mean inadequate authorization rather than absence. Apply these to both initial ingestion and refresh. A missing charter or submodule cannot be inferred from a single unauthenticated/unauthorized read. [S23]

Content hashes make input reuse tractable, not LLM conclusions deterministic. Re-running the same model inputs can require fresh validation. No fixed cost/latency, maximum supported repository size, or semantic-quality threshold can be promised before an implementation and representative corpus evaluation exist.

## 10. Decision handoff and acceptance checks

### Questions for the existing HITL ticket, not decisions made here

| Decision on #192 | Research consequence/options |
| --- | --- |
| **Authority and adoption** | May generated evidence remain advisory beside an existing human-owned charter, or may a human adopt an automatically produced result as governing intent? If the latter, explicitly reconcile ADR 0017; ADR 0018's amendment mechanism is not initial no-charter adoption. Do not solve this by making the operator write intent first. |
| **Persistence and provenance** | Where are the generated result, coverage ledger, raw/derived source and any approval recorded, with which retention/access rules? A result could be referenced from the authoritative manifest, but that is an option, not an already chosen schema or App Configuration storage contract. Do not silently use the current charter store for inference. |
| **Readiness and activation** | What unknown strategy, contradictory explicit intent, unsupported languages or missing source is tolerable, and what requires review or prevents council activation? Coverage completion alone is not product/evidence/model readiness. Preserve proposal-only filing and external delivery. |
| **Refresh and review validity** | Which code/intent/conflict changes require renewed review or invalidate previously accepted context? How stale may evidence be? Refresh of descriptive observations must not automatically expand scope or renew authorization. |
| **Supported acquisition/analysis scope** | Which Git route, language/framework tiers, generated/vendor treatment and separately approved external objects are supported initially? What cost/time/size ceilings and incomplete-result UX apply? No arbitrary folder-only sample may masquerade as the requested whole-repository pass. |

**Newly sharpened questions:** whether approval attaches to a particular inferred-result revision or only an existing charter; how approval survives code refresh; and how to expose complete inventory alongside incomplete semantic/language coverage. These fit #192 (with permission/lifecycle coordination where needed); this research creates no additional decision ticket and resolves no human policy.

### Technical acceptance checks for a later implementation

These are recommended checks, not tests run against an unspecified application:

- Non-`main` default branch, repository rename and branch movement during ingestion: one validated repository ID, one pinned snapshot, no mixed-revision claims.
- Truncated recursive tree and an oversized directory: full subtree/object fallback or explicit incomplete inventory, never false completeness.
- Large Contents responses, API/tool/model truncation, encoding errors and request budget exhaustion: exact file/chunk gaps and no empty-success substitution.
- LFS pointers, inaccessible submodules, generated/vendor paths, symlinks, secret exclusions and ignored-but-tracked files: every path accounted for without unauthorized fetch/execution or hidden exclusions.
- Unexecuted tests, stale docs, explicit non-goals contradicted by code, unsupported syntax and missing strategy: correct observation/inference/explicit/unknown classification with both conflict sides cited.
- Fabricated reference, valid reference that does not support the claim, duplicated evidence and moving source URLs: validation rejects or qualifies the material claim.
- More than 300 changed files, deletion/rename and force-push: complete new-tree reconciliation, affected-claim invalidation, no stale citations relabeled as new.
- Injection-bearing repository instructions and generated summaries: no tool/permission/egress escalation, credential disclosure, charter mutation, issue filing or activation from ingestion.

**Limits of this answer:** no target-specific completeness, supported-language accuracy, inference quality, cost, deployment alignment, permissions, data-handling configuration or activation readiness has been demonstrated. The research establishes a feasible auditable workflow and its limits; it does not prove that any particular repository expresses enough intent for a useful council.

## Sources

### Pinned DSF source and policy

- **[L1]** [Known source kinds](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Core/Runtime/SourceAgentKinds.cs), [adapter registry](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/SourceIntegrationRegistry.cs), [runtime composition, lines 43-154](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/ConveyorComposition.cs#L43-L154).
- **[L2]** [Served-agent evidence gatherer](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/SourceAgentEvidenceGatherer.cs), [`EvidenceItem`, line 47](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.FeatureCouncil/Conveyor/ConveyorModel.cs#L47), [gather tests](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/tests/Dsf.Runtime.Tests/SourceAgentGatherTests.cs).
- **[L3]** [Foundry IQ integration and reference validation](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Runtime/FoundryIqIntegration.cs).
- **[L4]** [S4 grounding reference-membership check](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.FeatureCouncil/Conveyor/Stations/S4Grounding.cs).
- **[L5]** [Current .NET operator charter documentation, lines 105-128](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/site/get-started/operate.md#L105-L128), [conveyor service contracts](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.FeatureCouncil/Conveyor/ConveyorPorts.cs).
- **[L6]** [ADR 0017: human-owned authoritative charter, advisory influence, untrusted input](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/adr/0017-product-charter.md), [ADR 0018: optional human-gated amendments](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/docs/adr/0018-living-charter-amendments.md).
- **[L7]** [Charter contracts](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Core/Charters/Charter.cs), [deterministic parser and Git blob hashing, lines 1-140](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Core/Charters/CharterMarkdown.cs#L1-L140).
- **[L8]** [CLI charter ref selection, interactive init, sync and status, lines 1852-2075](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/CliApplication.cs#L1852-L2075), [GitHub charter content read, lines 178-218](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/src/Dsf.Cli/CharterRepositoryClient.cs#L178-L218), [charter command tests](https://github.com/JoranBergfeld/dark-software-factory/blob/b427b762e7f5a9982b64059c995802d8b8d5920c/dotnet/tests/Dsf.Cli.Tests/CharterCommandTests.cs).

### Primary technology references

All read on 2026-09-15. Live documentation can change; named API versions and source snapshots delimit the claims.

- **[S1]** GitHub, [Git Trees: get a tree](https://docs.github.com/en/rest/git/trees?apiVersion=2026-03-10#get-a-tree): recursive limits, truncation recovery, tree modes and object IDs.
- **[S2]** GitHub, [Contents: get repository content](https://docs.github.com/en/rest/repos/contents?apiVersion=2026-03-10#get-repository-content): directory/file limits, media types, ref selection, symlink/submodule behavior, expiring download URLs.
- **[S3]** GitHub, [Git Blobs: get a blob](https://docs.github.com/en/rest/git/blobs?apiVersion=2026-03-10#get-a-blob): base64/raw reads and 100 MB support limit.
- **[S4]** GitHub, [REST Search, including Search code](https://docs.github.com/en/rest/search/search?apiVersion=2026-03-10#search-code): result/paging, default-branch/file-size limits, incomplete results and code-search rate limit.
- **[S5]** GitHub, [About GitHub Code Search: limitations](https://docs.github.com/en/search-github/github-code-search/about-github-code-search#limitations): web search exclusions, truncation and non-exhaustiveness.
- **[S6]** Git, [`git clone`](https://git-scm.com/docs/git-clone): bare/no-checkout, shallow/partial/sparse and submodule behavior; [`git cat-file`](https://git-scm.com/docs/git-cat-file): raw/batch object reads and optional textconv/filter transformations.
- **[S7]** Git, [`git ls-tree` documentation at v2.51.0](https://github.com/git/git/blob/v2.51.0/Documentation/git-ls-tree.adoc): recursive/full-tree, modes/types/sizes and NUL-delimited paths.
- **[S8]** GitHub, [Downloading source code archives](https://docs.github.com/en/repositories/working-with-files/using-files/downloading-source-code-archives); Git, [`git archive` documentation at v2.51.0](https://github.com/git/git/blob/v2.51.0/Documentation/git-archive.adoc): snapshot/archive generation and `export-ignore`/`export-subst`.
- **[S9]** GitHub, [About Git Large File Storage](https://docs.github.com/en/repositories/working-with-files/managing-large-files/about-git-large-file-storage): pointer/object distinction, pointer identity/size, plan-specific limits and configurable archive inclusion.
- **[S10]** Git, [`gitignore` documentation at v2.51.0](https://github.com/git/git/blob/v2.51.0/Documentation/gitignore.adoc): intentionally untracked files; tracked files unaffected.
- **[S11]** GitHub, [Compare two commits](https://docs.github.com/en/rest/commits/commits?apiVersion=2026-03-10#compare-two-commits), also checked in [S20]: 250-commit unpaged limit, 300-file comparison limit, binary-patch caveat.
- **[S12]** GitHub, [Permissions required for GitHub Apps](https://docs.github.com/en/rest/authentication/permissions-required-for-github-apps): repository Contents, Metadata, Issues and Pull requests tables; read access and installation-token support checked in the actual HTML tables.
- **[S13]** GitHub, [Authenticating as an App installation](https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/authenticating-as-a-github-app-installation): installation grant bounds, narrowed repository/permission requests, one-hour tokens and HTTP Git access.
- **[S14]** GitHub, [Git References: get a reference](https://docs.github.com/en/rest/git/refs?apiVersion=2026-03-10#get-a-reference): mutable refs, exact-ref lookup and commit object identity.
- **[S15]** GitHub, [Git Commits: get a commit object](https://docs.github.com/en/rest/git/commits?apiVersion=2026-03-10#get-a-commit-object): commit/tree relationship and response identity.
- **[S16]** Microsoft, [Agentic retrieval overview](https://learn.microsoft.com/en-us/azure/search/agentic-retrieval-overview) and [Knowledge Retrieval - Retrieve, `2026-04-01`](https://learn.microsoft.com/en-us/rest/api/searchservice/knowledge-retrieval/retrieve?view=rest-searchservice-2026-04-01): ranked retrieval, source/activity records, token/runtime limits and partial responses. The overview distinguishes GA programmatic features from preview features; no preview synthesis capability is assumed here.
- **[S17]** Microsoft, [Chunk documents for Azure AI Search](https://learn.microsoft.com/en-us/azure/search/vector-search-how-to-chunk-documents): model input limits, truncation risk, structural/fixed/overlapping chunking and context loss.
- **[S18]** Microsoft, [Prompt Shields](https://learn.microsoft.com/en-us/azure/ai-services/content-safety/concepts/jailbreak-detection): document attacks and residual susceptibility despite safeguards.
- **[S19]** Microsoft, [Azure OpenAI transparency note](https://learn.microsoft.com/en-us/azure/foundry/responsible-ai/openai/transparency-note#considerations-when-choosing-a-use-case): factual-accuracy limits and need to verify suitability for the specific grounded use case.
- **[S20]** GitHub, [OpenAPI source at `022b4dc2d10306c8c583368af92e3296598c6ce3`](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json): selected repository/ref/commit/tree/blob/contents/compare GET descriptions and response contracts. This source includes versioned breaking-change metadata; it is not a substitute for the permission table.
- **[S21]** Tree-sitter, [introduction and available grammars at `1b8407d1e718f2a26e2886c03cc55622d8d1d7bd`](https://github.com/tree-sitter/tree-sitter/blob/1b8407d1e718f2a26e2886c03cc55622d8d1d7bd/docs/src/index.md), [query syntax/error and missing nodes at the same revision](https://github.com/tree-sitter/tree-sitter/blob/1b8407d1e718f2a26e2886c03cc55622d8d1d7bd/docs/src/using-parsers/queries/1-syntax.md).
- **[S22]** Microsoft, [Roslyn compiler API model](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model): C#/Visual Basic syntax, symbols, semantic analysis, compilation inputs, analyzers and scripting/workspace layers.
- **[S23]** GitHub, [REST API best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api): efficient conditional reads, polling/webhook guidance, pagination, throttling/retries and ambiguous private-resource 404s.
