# ADR 0002 — Homelab runtime, Azure backing services only

Status: Accepted · Date: 2026-06-16 · Supersedes the Container Apps hosting in ADR 0001 §1's deployment assumption

## Context

The agent + orchestrator runtime must run in the user's Proxmox homelab, not in
Azure. The driver is explicit: the user does not want to integrate the homelab
and Azure networks (VNet peering / site-to-site VPN), which risks IP conflicts
and operational coupling. Azure is still wanted — but only for the **brain/data
services** (Foundry models, FoundryIQ, Cosmos, App Configuration, App Insights,
Key Vault), reached outbound.

## Decision

1. **Azure hosts backing services only.** `infra/main.bicep` provisions Cosmos,
   App Configuration, Key Vault, Log Analytics + App Insights, and a signal
   ingestion buffer (Event Grid topic → Service Bus queue). It provisions **no
   compute** — no Container Apps, no managed environment.

2. **The runtime is homelab-hosted containers.** Agents are built into images and
   published to GHCR; the user hosts them (and the orchestrator/control-center)
   on Proxmox however they choose. Deployment is deliberately out of repo scope.

3. **Connectivity is egress-only.** Homelab containers call Azure public service
   endpoints over authenticated HTTPS. Nothing is pushed into the homelab: signal
   interrupts are consumed by **polling the Service Bus queue outbound**. No VNet
   peering, no inbound, no shared address space → no IP conflicts.

4. **Auth is an Entra service principal, not Managed Identity.** Managed Identity
   only works inside Azure. The homelab workload authenticates with an Entra app
   registration (client credential / workload-identity federation). Its object id
   is passed to the Bicep as `workloadPrincipalId`, which receives the data-plane
   roles (Cosmos Data Contributor, App Config Data Reader, Key Vault Secrets User,
   Service Bus Data Receiver). Empty = provision without role assignments.

## Consequences

- The Bicep is smaller and cheaper (no Container Apps environment). The earlier
  per-service Container Apps and the `containerapp.bicep` module were removed.
- A small amount of homelab plumbing is the user's responsibility: pulling images,
  the SP credential, and (if real-time interrupts are wanted) a poller on the
  Service Bus queue. Scheduled sweeps need none of this.
- If the user later wants Azure-hosted compute after all, re-introducing Container
  Apps is additive and does not change the contracts or the agent images.
