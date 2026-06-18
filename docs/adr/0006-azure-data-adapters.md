# ADR 0006: Azure data adapters — injected gateway seams, optional extra, graceful degradation

- Status: Accepted
- Date: 2026-06-18
- Fulfils: ADR 0001 (ports) and ADR 0005 (honest local implementations); supersedes nothing.

## Context

SP3 wired a real GitHub client and tracer in `azure` mode but kept config,
memory, and model on the in-memory implementations behind a deferred-adapter
seam. SP3b lands the real Azure data adapters.

## Decision

- Implement `AppConfigStore` (Azure App Configuration), `CosmosMemoryStore`
  (Cosmos DB), and `AzureOpenAIModelClient` (Azure OpenAI) behind the existing
  `ConfigStore` / `MemoryStore` / `ModelClient` ports — no caller changes.
- Each adapter consumes a **narrow gateway** `Protocol` (1-3 methods) instead of
  the raw SDK. The default gateway wraps the SDK and is built lazily from an
  endpoint + `DefaultAzureCredential`; tests inject a dict-backed in-memory
  gateway. This keeps adapters free of SDK object/exception quirks and keeps the
  whole suite offline — the same seam idea as `RealGitHubClient(_run=...)`.
- Azure SDKs are an **optional `azure` extra**, lazy-imported inside the gateway
  builders. Importing an adapter module never requires the SDK; only building a
  real gateway does (raising a clear error if the extra is absent).
- `build_services('azure')` selects each real adapter **only when its endpoint is
  configured**, falling back to the in-memory sibling otherwise — so `azure` mode
  runs mid-rollout and the offline test/eval posture is unchanged.

## Consequences

- Real Azure data paths exist behind the ports, unit-tested offline; live-Azure
  integration testing is out of scope (billable; covered structurally by SP2).
- Cosmos `query_similar` keeps token-overlap ranking; native vector search is
  deferred.
- The `[deterministic]` synthesizer echo only comes from `DeterministicModelClient`;
  the real model returns real content, so the fallback simply never triggers.
