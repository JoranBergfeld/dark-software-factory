# GitHub proposal-edit concurrency

Research date: **2026-09-15**. Resolves [#200](https://github.com/JoranBergfeld/dark-software-factory/issues/200), under map [#189](https://github.com/JoranBergfeld/dark-software-factory/issues/189). The human owns the lifecycle/edit policy in [#193](https://github.com/JoranBergfeld/dark-software-factory/issues/193).

## Finding

**No documented, supported, enforced compare-and-swap operation was found for GitHub issue title/body updates.** GitHub explicitly excludes conditional unsafe REST requests unless an endpoint documents an exception; the issue-update contract documents none. GraphQL `UpdateIssueInput` likewise has no expected version, expected timestamp, old content, or equivalent write predicate. Consequently, **GET/check/merge/PATCH cannot guarantee preservation of simultaneous human edits**. This is a supported negative research result, not permission to weaken the preservation requirement. [^R1][^R2][^G1]

GitHub's exact general rule:

> Conditional requests for unsafe methods, such as `POST`, `PUT`, `PATCH`, and `DELETE` are not supported unless otherwise noted in the documentation for a specific endpoint.

Source: [`github/docs:content/rest/using-the-rest-api/best-practices-for-using-the-rest-api.md:115`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/rest/using-the-rest-api/best-practices-for-using-the-rest-api.md#L115).

**Scope of this conclusion:** REST conditional issue writes are outside the documented supported contract; GraphQL issue version predicates are absent from the published schema. Actual handling of an unsolicited `If-Match` header, precise ordering of simultaneous requests, and any undocumented internal/UI conflict mechanism are **unverified**. No live issue creation, edits, conditional-write experiments, credentials, SDK changes or deployments were performed. Reading client/schema source is not proof that an undocumented server precondition is enforced.

## Sources and dates

| Evidence | Exact revision / date |
| --- | --- |
| Official REST OpenAPI description for `api.github.com` | `github/rest-api-description` commit [`022b4dc2d10306c8c583368af92e3296598c6ce3`](https://github.com/github/rest-api-description/commit/022b4dc2d10306c8c583368af92e3296598c6ce3), 2026-09-15 01:05:14 UTC. |
| GitHub documentation and GraphQL schema source | `github/docs` commit [`b32e08ff7345b996e5bb059bfd5bfc11d08bcc36`](https://github.com/github/docs/commit/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36), 2026-09-15 10:01:13 UTC. Both `fpt` and `ghec` `UpdateIssueInput` were inspected; neither contains a conditional-update predicate. |
| GraphQL schema freshness | Latest change to inspected `fpt/schema.docs.graphql`: [`e0873351c5408b92fdc67ce22b95890cb62ed749`](https://github.com/github/docs/commit/e0873351c5408b92fdc67ce22b95890cb62ed749), 2026-09-10 16:39:22 UTC. |
| HTTP semantics | IETF RFC 9110, June 2022, sections 8.8.1, 9.2.2 and 13.1.1-13.1.4. HTTP PATCH: RFC 5789, March 2010, section 2. [^H1][^H2] |
| Governing DSF topology | [Final decision #191, 2026-09-15](https://github.com/JoranBergfeld/dark-software-factory/issues/191#issuecomment-5678928713), read in full. |

These are public documentation/interface sources, not GitHub's private issue-update implementation. No service-side update handler or deployment-specific guarantee was available in the inspected sources. The negative finding rests on the explicit support rule plus the complete relevant request schemas, not on a single missing field in an SDK.

## What the APIs actually provide

| Mechanism | Documented capability | What it does not establish |
| --- | --- | --- |
| REST `GET` with `If-None-Match` or `If-Modified-Since` | Cache validation; unchanged representation can produce `304 Not Modified`. [^R1] | No lock, reservation or condition attached to a later write. |
| REST `PATCH /repos/{owner}/{repo}/issues/{issue_number}` | Optional `title` and `body` values in an `application/json` object, along with separate metadata fields. [^R2] | No endpoint exception for `If-Match`/`If-Unmodified-Since`; no `expected_updated_at`, version, base body or compare-and-swap argument. |
| REST timestamp properties | `updated_at`/`created_at` are returned date-time fields. [^R3] | A returned timestamp is not a write predicate or a documented unique, monotonic content revision. Reading it twice does not close the race. |
| GraphQL `updateIssue` | Input identifies an `Issue` by global ID and supplies optional title/body and metadata; output includes the issue and `clientMutationId`. [^G1] | No expected revision/date/content field. `updatedAt`, `lastEditedAt` and `userContentEdits` are read-side information, not update preconditions. [^G2] |
| JSON Patch `test`, body-range patch or atomic append | None documented by the issue-update schema; title/body are values, not edit-operation lists. [^R2] | The HTTP method name `PATCH` does not promise RFC 6902 JSON Patch, conditional string replacement or server-side three-way merge. |
| GraphQL `clientMutationId` | Documented as an identifier for the client performing the mutation, also present in the payload. [^G1][^G3] | No published deduplication, exactly-once execution, conditional-write or replay-result guarantee. Reusing it is not an idempotency contract. |

The REST update operation lists `200`, `422`, `503`, `403`, `301`, `404` and `410` responses. It does **not** document `409`/`412` as concurrent-title/body conflict outcomes. This omission is not proof such codes can never occur; it means a client cannot depend on a documented stale-write rejection protocol. A received `200` is not certification that no human edit was overwritten. [^R2]

The published OpenAPI includes API-version overlays, including removal of the deprecated singular `assignee` field for `2026-03-10`; none of the inspected title/body update contract introduces a write precondition. The conclusion is about GitHub.com documentation at the pinned revision, not a promise about future versions or every GitHub Enterprise Server release. [^R2]

### Weak ETags and HTTP atomicity

An ETag prefixed `W/` is a **weak validator**: it need not change for every representation-data change. RFC 9110 requires **strong comparison** for `If-Match`, whereas `If-None-Match` supports weak comparison for cache validation. A weak cache tag is therefore not an exact-content write revision; removing `W/` does not upgrade its semantics. Conversely, even a strong-looking GitHub GET ETag does not establish support for conditional issue PATCH. No claim is made here that every GitHub issue response always has a weak ETag. [^H1][^R1]

`If-Unmodified-Since` is an HTTP write-precondition mechanism, but GitHub does not document it for this endpoint. Date-based validators also require care about clock resolution; RFC 9110 explicitly discusses two changes within one second as a weak-validator case. `updated_at == saved_timestamp` is only an observation in application code, not a service-enforced precondition. [^H1][^R2][^R3]

RFC 5789 requires atomic application of a single PATCH document. That is **different from atomically comparing the current issue with an earlier GET and then applying changes**. Two individually atomic updates can still replace each other's values. The same RFC recommends conditional requests for base-dependent patches; GitHub's documented issue API does not supply that supported condition. [^H2][^R1][^R2]

## Consequences for simultaneous human edits

This is a reasoning example permitted by the documented interfaces, not an observed live experiment:

```text
1. Factory GET observes body B.
2. Factory computes M = merge(B, proposed factory changes).
3. Human saves B plus human change H.
4. Factory PATCH sends body M without an enforced expected-base predicate.
5. Factory GET observes M, matching its intended output.
```

A value-replacing update at step 4 can lose H. Step 5 can still look successful. Another preflight GET merely moves the unprotected interval; a post-write GET cannot prove an intervening human change never existed. This remains true if M preserves every human change visible at step 1. Precise GitHub ordering, any internal merge behavior, and stale-editor UI warnings are not established by these public contracts. [^R1][^R2][^G1]

| Technique | Useful for | Hard limit |
| --- | --- | --- |
| Last factory baseline + current raw text + new generated text | Detecting already-visible human edits; building a three-way merge or reporting a known conflict | Cannot protect a human edit made after the final read. |
| Compare full title/body, hash, ETag, timestamp or edit-history cursor before writing | Detecting changes already observed | No comparison is enforced as part of the GitHub write. |
| Update only changed fields | Avoiding requests to replace untouched body/title or unrelated labels/assignees/state | Does not protect concurrent edits within a field that is sent. Do not resend the fetched issue object as an update payload. |
| Mark a factory-owned section inside the body | Identifying regions for an application-level merge | GitHub still receives a new body value; a concurrent human change elsewhere in that body can be lost. Markers confer no field-level ownership or locking. |
| Local queue, lease or optimistic concurrency in factory state | Coordinating factory workers and their local records | Humans editing through GitHub do not participate. A local storage ETag cannot become a GitHub issue ETag. |
| GitHub conversation lock | Moderation of comments/reactions | Not a title/body compare-and-swap primitive; documentation still permits privileged participants to act. Do not use it as an editor mutex. [^D1] |
| Timeline, edit history, webhook observation or after-write inspection | Evidence for diagnosis/reconciliation | Not pre-commit exclusion. Description history can be removed; recovery after an overwrite is not preservation. [^D2] |

**Guarantee boundary:** no documented GitHub-only primitive was found that permits simultaneous human edits to these same fields while proving automated replacements cannot clobber them. A true guarantee would need enforceable coordination covering **all** writers during the read/write interval, or a human-approved workflow that does not automatically replace concurrently human-editable values. A cooperative convention or a human approval obtained before a later unguarded PATCH is not, by itself, enforced exclusion.

This research does not select that workflow. Best-effort merging remains best-effort; it cannot be presented as satisfying an absolute no-overwrite requirement. Not issuing an automatic replacement avoids an overwrite by that factory operation, but how to surface or apply pending changes belongs to #193. Comments, new replacement issues, locks or permission changes are not silently authorized alternatives.

## Lost responses, retries and completion

The distinction is between **request outcome**, **currently observed content**, and **proof of safe preservation**. They are not interchangeable. Neither issue mutation schema declares an idempotency key/exactly-once protocol. RFC 5789 says PATCH is not inherently idempotent; RFC 9110 cautions against automatically retrying non-idempotent operations unless their semantics or non-application are known. [^R2][^R4][^G1][^G3][^H2][^H3]

| Observation | What can be concluded | What remains unsafe or unknown |
| --- | --- | --- |
| PATCH response received with expected issue ID/content | Service acknowledged an update to that resource | No proof that the submitted base was current or that human text was preserved. |
| Timeout, dropped connection, lost success response or crash before receipt persistence | Outcome can be **unknown** | The request may already have committed. Treating it as definitely failed permits a damaging retry. |
| Read-back equals intended content | Desired content is observed at that time | Does not identify which request wrote it, prove exactly-once side effects, or prove no intervening human edit was lost. |
| Read-back differs | Current state differs from the request's intended result | Could be non-application, a successful update followed by another edit, or other interleaving. Blind replay can overwrite the newer change. |
| Read-back matches the old baseline | The baseline is observed again | Does not prove no writes occurred; edit/revert sequences can return to identical content. |
| Creation response lost | No confirmed creation receipt available to the factory | Repeating POST may create another proposal; matching a title/marker/App author is not exclusive proof of the original operation. [^R4][^G3] |
| `404`, redirect or missing lookup | Identity/access/location requires reconciliation | `404` can conceal authorization failure; redirects can follow a moved issue. Neither is permission to create a replacement or write outside the approved repository. [^R5][^D3] |

Repeatedly assigning the same title/body can be idempotent in isolation and still overwrite a human edit that occurred **between attempts**. Likewise, request equality does not prove notifications, history or automation effects occurred exactly once. Delay/backoff for rate limits is necessary operational discipline, not a concurrency remedy. A later recovery policy needs an explicit unresolved/ambiguous outcome rather than assuming success or replaying until a matching GET appears. This is a limitation to account for, not a policy implementation. [^H3][^R5]

## Durable identity and same-factory provenance

GitHub supplies object identity, not a DSF factory-ownership certificate. REST issue responses include numeric `id` (int64), `node_id`, repository-scoped `number`, author information and optional `performed_via_github_app`. A successful create returns an issue and `Location`; GraphQL creation returns an issue as well. These are evidence the factory can associate with its own operation. [^R3][^R4][^G3]

Evidence useful to the later policy, in **existing factory-local trusted state**, includes:

| Evidence | Purpose / limit |
| --- | --- |
| GitHub host, approved durable repository identity, returned issue numeric/global IDs | Bind the resource, rather than trusting a mutable repository name, URL, label or supplied issue number. Re-resolve identity/type/current repository before using the target. |
| Factory identity/lifetime and internal proposal/create-operation identity | Distinguish this factory's work from another factory using the same shared App. These must come from trusted factory records, not from copied issue text. |
| Confirmed creation receipt linked to the request and returned issue | Evidence of which issue this factory actually created; useful only when retained durably. A pending operation record alone is not proof creation succeeded. |
| Raw last-observed and last-confirmed factory-emitted title/body, intended update and outcome evidence | Support provenance, comparisons and recovery without treating generated HTML, a hash alone, or a timestamp as the full merge baseline. |

GitHub recommends persisting global node IDs and supports direct node lookup/type checks. Treat IDs as opaque: GitHub has migrated node-ID formats, so "durable identity" must not mean parsing IDs or assuming their textual encoding never changes. The issue number is explicitly unique **within its repository**, not globally. The inspected sources do not establish unchanged ID encodings across every migration/transfer; any identity or repository mismatch requires reconciliation rather than automatic rebinding. [^D4][^R3][^G2]

Issue transfer is explicitly supported and the old URL redirects. A number/URL is therefore an addressing convenience, not sufficient authority to mutate whatever it resolves to later. A GraphQL `Issue` type check or the REST `pull_request` discriminator also matters because the REST Issues family includes pull requests. Durable repository restriction must still be checked after resolving an object. [^D3][^D4][^R3]

**Same App is not same factory.** Under the accepted topology, multiple factories share App signing authority. App identity, installation ID, bot author, labels, similar titles and copyable markers do not uniquely identify a creating factory. Correlating a confirmed create response with trusted factory-local state supplies stronger evidence; it does not create an atomic transaction spanning local storage and GitHub. A crash/lost response in that gap may leave provenance unresolved. No unsupported exactly-once creation or ownership claim should be inferred from `clientMutationId` or a marker. [^G3][^R4]

The shape and retention of these records are #193's decision. This is **not** a requirement for an owner publication journal, token broker or issue-writing service. It does not seek stronger credential isolation; the shared-key authority limitation has already been accepted.

## Handback boundary

The confirmed #191 topology remains unchanged: per-factory Key Vault copy of the shared App private key, normally repository-limited/permission-downscoped installation tokens, direct factory GitHub operations and workload-separated identities. Stronger isolation and SDK adoption are deferred outside this map.

Factory behavior remains limited to creating proposals and editing **its own verified proposals while preserving human changes**. No automatic comments, closure, assignment, arbitrary existing-issue edits or new publication mechanisms are authorized. The already-confirmed automation compatibility gate applies to updates as well as creation. The open human decision is how to meet that preservation contract given the missing public conditional-write primitive, not whether to assume GET/PATCH is safe.

No production changes, new dependencies, deployments or live mutation experiments were made. This worker publishes only this research artifact and resolves #200; shared map/lifecycle updates remain with the parent.

## Primary-source references

[^R1]: [`github/docs:content/rest/using-the-rest-api/best-practices-for-using-the-rest-api.md:76-115`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/rest/using-the-rest-api/best-practices-for-using-the-rest-api.md#L76-L115). Conditional GET and explicit exclusion of unsafe conditional requests.
[^R2]: [`github/rest-api-description:descriptions/api.github.com/api.github.com.json:64556-64601`](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json#L64556-L64601), with the remaining request body through line 64838; [response/error and version-overlay tail, lines 65067-65109](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json#L65067-L65109). Complete `issues/update` operation inspected at lines 64556-65111. [Rendered official endpoint](https://docs.github.com/en/rest/issues/issues#update-an-issue).
[^R3]: [`github/rest-api-description:descriptions/api.github.com/api.github.com.json:124654-124725`](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json#L124654-L124725), [pull-request discriminator, timestamps, repository and App attribution, lines 124803-124877](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json#L124803-L124877), [repository IDs, lines 120698-120720](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json#L120698-L120720).
[^R4]: [`github/rest-api-description:descriptions/api.github.com/api.github.com.json:63551-63590`](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json#L63551-L63590), [creation receipt, lines 63714-63736](https://github.com/github/rest-api-description/blob/022b4dc2d10306c8c583368af92e3296598c6ce3/descriptions/api.github.com/api.github.com.json#L63714-L63736). Entire `issues/create` request/response inspected at lines 63551-63783.
[^R5]: [`github/docs:content/rest/using-the-rest-api/best-practices-for-using-the-rest-api.md:39-74`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/rest/using-the-rest-api/best-practices-for-using-the-rest-api.md#L39-L74), [authorization-related 404 caveat, lines 131-135](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/rest/using-the-rest-api/best-practices-for-using-the-rest-api.md#L131-L135).
[^G1]: [`github/docs:src/graphql/data/fpt/schema.docs.graphql:70037-70122`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/src/graphql/data/fpt/schema.docs.graphql#L70037-L70122), [`UpdateIssuePayload`, lines 70162-70177](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/src/graphql/data/fpt/schema.docs.graphql#L70162-L70177). [Enterprise Cloud input, same line range](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/src/graphql/data/ghec/schema.docs.graphql#L70037-L70122).
[^G2]: [`github/docs:src/graphql/data/fpt/schema.docs.graphql:20273-20315`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/src/graphql/data/fpt/schema.docs.graphql#L20273-L20315), [`lastEditedAt`, lines 20397-20400](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/src/graphql/data/fpt/schema.docs.graphql#L20397-L20400), [`updatedAt` and edit connection, lines 20870-20903](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/src/graphql/data/fpt/schema.docs.graphql#L20870-L20903).
[^G3]: [`github/docs:src/graphql/data/fpt/schema.docs.graphql:8561-8650`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/src/graphql/data/fpt/schema.docs.graphql#L8561-L8650). `CreateIssueInput` and `CreateIssuePayload`, including `repositoryId`, returned issue and `clientMutationId`.
[^D1]: [`github/docs:content/communities/moderating-comments-and-conversations/locking-conversations.md:16-34`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/communities/moderating-comments-and-conversations/locking-conversations.md#L16-L34).
[^D2]: [`github/docs:content/issues/tracking-your-work-with-issues/using-issues/editing-an-issue.md:14-28`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/issues/tracking-your-work-with-issues/using-issues/editing-an-issue.md#L14-L28). Title timeline and removable description-edit history.
[^D3]: [`github/docs:content/issues/tracking-your-work-with-issues/administering-issues/transferring-an-issue-to-another-repository.md:20-25`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/issues/tracking-your-work-with-issues/administering-issues/transferring-an-issue-to-another-repository.md#L20-L25).
[^D4]: [`github/docs:content/graphql/guides/using-global-node-ids.md:87-124`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/graphql/guides/using-global-node-ids.md#L87-L124); [`content/graphql/guides/migrating-graphql-global-node-ids.md:20-46`](https://github.com/github/docs/blob/b32e08ff7345b996e5bb059bfd5bfc11d08bcc36/content/graphql/guides/migrating-graphql-global-node-ids.md#L20-L46).
[^H1]: IETF [RFC 9110 section 8.8.1](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.8.1), [section 13.1.1 (`If-Match`)](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.1), [section 13.1.2 (`If-None-Match`)](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.2) and [section 13.1.4 (`If-Unmodified-Since`)](https://www.rfc-editor.org/rfc/rfc9110.html#section-13.1.4), June 2022.
[^H2]: IETF [RFC 5789 section 2](https://www.rfc-editor.org/rfc/rfc5789.html#section-2), March 2010. Single-request atomicity, non-idempotence and conditional PATCH recommendation.
[^H3]: IETF [RFC 9110 section 9.2.2](https://www.rfc-editor.org/rfc/rfc9110.html#section-9.2.2), June 2022. Idempotent-method scope, side effects and retry limitations.
