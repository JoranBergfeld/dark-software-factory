# ADR 0020: WebIQ source agent via the Microsoft WebIQ SDK

- Status: Accepted
- Date: 2026-06-25
- Supersedes: The Foundry "Grounding with Bing Search" approach introduced in issue #85
- Relates to: ADR 0004 (Key Vault secret handling), ADR 0014 (real-only `src/`)

## Context

Greenfield products have no telemetry, so the `webiq` source agent provides real
web research capability. The initial implementation used Azure AI Foundry's
*Grounding with Bing Search* tool via a per-product Foundry project connection:
a `Microsoft.Bing/accounts` resource exposed through a
`Microsoft.CognitiveServices/accounts/projects` connection, provisioned inline
by the ARM deployment.

This approach proved fundamentally unreliable. On brand-new Foundry accounts,
the platform's asynchronous managed-Key-Vault registration lags ARM's
`Succeeded` status on the account resource, so the connection's ApiKey secret
write returns HTTP 500 and ARM never re-drives it — the deployment wedges for
~10 minutes then fails. Both an in-template `dependsOn` delay and an
out-of-band bounded-retry step failed on cold accounts across multiple live
runs (`pet-clinic3`, `pet-clinic4`). The connection write races the
platform-managed resource and loses consistently on cold accounts.

Microsoft **WebIQ** (announced at Build) is a first-party web-research
capability with its own Python SDK (`pip install webiq`, API-key auth),
independent of any per-product Azure resource provisioning.

## Decision

Adopt the Microsoft **WebIQ SDK** as the default web-research backend for the
`webiq` source agent. The API key is managed as a central/owner Key Vault
secret (`webiq-api-key`), seeded into each product Key Vault by `dsf new` and
read at runtime via the Container App managed identity — consistent with ADR
0004 (secret handling) and ADR 0014 (real-only `src/`).

- **Default provider** is now `webiq` (via `dsf.agents.webiq.webiq_sdk` backed
  by `webiq.WebIQAsyncClient`). Tavily remains an optional provider via
  `WEBIQ_PROVIDER=tavily`.
- **Key resolution:** `WEBIQ_API_KEY` env override (local/dev), else read from
  the product Key Vault (secret name `webiq-api-key`, overridable via
  `WEBIQ_API_KEY_SECRET`). The runtime accesses the product vault through the
  Container App managed identity.
- **Provisioning:** `dsf new` adds a `seed_webiq_key` step (right after
  `seed_app_key`) that copies `webiq-api-key` from the owner Key Vault into the
  product Key Vault, mirroring the existing GitHub App key seed. **Prerequisite:**
  an admin must have seeded `webiq-api-key` into the owner vault first.
- **Key Vault policy compliance:** Both `seed_webiq_key` and `seed_app_key`
  write with `--content-type text/plain` and `--expires` 30 days out, because
  the product vaults inherit a management-group Azure Policy that DENIES secret
  writes lacking a content type or expiry. 30 days is the verified-acceptable
  cap, so the key is re-seeded on every `dsf new` and must be re-seeded on
  rotation/expiry.
- **Infrastructure cleanup:** Remove the entire Foundry Grounding-with-Bing
  surface from `infra/main.bicep`: the `Microsoft.Bing/accounts` resource, the
  Foundry project (`Microsoft.CognitiveServices/accounts/projects`), their role
  assignments, the `enableBingGrounding` parameter, the Bing/project connection
  variables, and the Bing/project outputs and container environment variables.
  The Foundry **account** (Azure OpenAI for chat + embeddings) and its model
  deployments remain.
- **CLI:** Remove the per-instance `enable_bing_grounding` spec field and the
  `dsf new --enable-bing-grounding` / `--no-enable-bing-grounding` flags.

## Consequences

- **Positive:** No dependency on the flaky cold-account Foundry connection;
  provisioning no longer wedges. Simpler infrastructure — Bing account, Foundry
  project, and their configuration are gone; only the Foundry account
  (OpenAI) stays.
- **Negative:** `dsf new` now requires the admin to pre-seed `webiq-api-key`
  in the owner vault before provisioning (mirroring the existing
  `github-app-private-key` prerequisite). The seeded secret carries a 30-day
  expiry (forced by the management-group Key Vault policy), so it must be
  re-seeded on rotation/expiry; `dsf new` re-seeds it each run, but a lapsed
  key will break the agent until re-seeded.
- **Tavily fallback:** Environments without a WebIQ key can still use Tavily
  via `WEBIQ_PROVIDER=tavily`.
- **No migration:** DSF provisions fresh per-product repos and the runtime
  consumes the provider constant directly, so there is no stored `foundry`
  provider data to migrate. Any pre-existing demo repo with Bing resources would
  need the infra re-deployed from the updated Bicep.
