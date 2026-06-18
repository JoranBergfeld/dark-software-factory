# ADR 0004: Azure Container Apps runtime with a user-assigned managed identity

- Status: Accepted
- Date: 2026-06-18
- Supersedes: ADR 0002 (homelab runtime, Azure backing services only)

## Context

ADR 0002 hosted the factory runtime (orchestrator + source agents) in a homelab
behind NAT, reaching Azure backing services outbound only, authenticating with an
Entra service principal (`workloadPrincipalId`) because managed identity only works
inside Azure. The owner has retired the homelab: the runtime now runs in Azure.

## Decision

- Host the per-product feature-council orchestrator on **Azure Container Apps** in
  the product's resource group, alongside the backing services it consumes.
- Provision a **user-assigned managed identity** that holds the data-plane roles
  (Key Vault Secrets User, App Configuration Data Reader, Cosmos data-plane, Service
  Bus Data Receiver). A user-assigned identity (not system-assigned) has a stable
  principalId known before the Container App, avoiding a dependency cycle between the
  app's env (which wires in the Cosmos / App Configuration endpoints) and those
  resources' role assignments.
- `infra/main.bicep` provisions the Container Apps environment + orchestrator app
  (no ingress — it is a worker) with the identity attached and env wired from the
  sibling resources. `provision_azure` deploys it; `deploy_council` reconciles the
  app image via `az containerapp update`.
- Remove the homelab compose bundle, the tunnel sidecar, and the `workloadPrincipalId`
  service principal entirely.

## Consequences

- No inbound exposure; the orchestrator and agents are co-located in Azure and reach
  data sources over their existing authenticated endpoints.
- `DefaultAzureCredential` selects the user-assigned identity via `AZURE_CLIENT_ID`.
- The per-product runtime bundle rendered by `render_runtime_bundle` is now an ACA
  `containerapp.yaml` (+ a resolved `.env.orchestrator` for inspection) instead of a
  docker-compose file.
- ADR 0002 is superseded and kept for history.
