# ADR 0001 — Foundational architecture decisions

Status: Accepted · Date: 2026-06-15

These record the key choices made while designing the intake line, so future work
understands the *why*, not just the *what*. The full design is in
`docs/superpowers/specs/2026-06-15-dark-software-factory-intake-design.md`.

## 1. Single Python package, per-agent service entrypoints

**Decision:** One installable package `dsf` with focused modules; each source agent is a
thin ASGI entrypoint (`dsf.agents.<kind>.main:app`) with its own Dockerfile.

**Why:** The agents must be *portable containers deployed near their sources* (a homelab
Grafana agent, cloud Sentry agent, etc.). A separate package per agent would create
import/versioning friction across shared contracts. One package keeps the contracts and
A2A library coherent and independently testable; the container boundary (Dockerfile +
entrypoint) provides the portability, not a package boundary.

## 2. Ports + in-memory fakes for every external dependency

**Decision:** Model, memory, config, GitHub, source backends, and tracing are all
`typing.Protocol` ports with deterministic in-memory fakes (`DSF_MODE=local`) and Azure
implementations (`DSF_MODE=azure`).

**Why:** The whole line must run end-to-end in dry-run with no Azure subscription and no
LLM — for local development, CI, and the eval gate. It also makes the system testable
without mocking frameworks and lets cloud impls be swapped in without touching logic.

> **Update (ADR 0005):** The "in-memory fakes" naming and the `dsf.fakes` package are
> superseded by [ADR 0005](0005-honest-local-implementations.md). The ports +
> offline-by-default posture stands; the implementations are now honest,
> domain-co-located classes (`InMemoryConfigStore`, `InMemoryMemoryStore`,
> `NoOpTracer`, `RecordingGitHubClient`, `DeterministicModelClient`,
> per-agent `*FixtureBackend`) and pure test-doubles live under `tests/`. Other
> ADR 0001 decisions are unaffected.

## 3. Hybrid conveyor: deterministic stations, agentic workcells

**Decision:** A fixed, auditable 7-station conveyor; only investigation (S2) and the
critic council (S5) are agentic.

**Why:** Intake is *fully dark* — issues are filed with no human pre-check. That demands
an auditable backbone with hard gates (grounding) and bounded cost, while still allowing
genuine agent reasoning where it adds value. A free agent swarm was rejected as too hard
to audit/bound for unsupervised filing.

## 4. Cosmos DB as unified memory (working + long-term + vector)

**Decision:** One Cosmos account backs working memory (TTL), long-term records, and
vector retrieval (dedup/lessons). Azure AI Search is deferred.

**Why:** Cosmos now has native vector search; using it for everything minimizes moving
parts. Retrieval needs are not yet sophisticated enough to justify a separate search
service — keep it simple, swap in AI Search later if needed.

## 5. A2A over HTTP with outbound tunnels for NAT'd agents

**Decision:** Agents expose A2A HTTP endpoints + Agent Cards; homelab/NAT agents are
reached via an outbound tunnel (Tailscale/Cloudflare), not open inbound ports. Locally,
the orchestrator calls agents in-process via `httpx.ASGITransport`.

**Why:** A2A is the emerging standard and composes with Microsoft Agent Framework.
Tunnels avoid exposing homelab services. In-process ASGI calls let the full distributed
topology run inside one test with zero network.
