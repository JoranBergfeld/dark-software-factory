# ADR 0021: Centralize the product registry into App Configuration

- Status: Accepted
- Date: 2026-06-24
- Relates to: ADR 0014 (real-only `src/`, no offline fallbacks), ADR 0007 (council → squad handoff)

## Context

The list of products the line serves — each product's repo, label taxonomy,
source-agent scopes (Sentry projects, Grafana dashboards, FoundryIQ scope, Azure
Monitor scope), and confidence threshold — lived in a single local file,
`config/products.json`, read and written through a file API (`load_registry`,
`register_product`, `route_product`, and the teardown writers). That file was the
one piece of per-product configuration that did NOT live in the product's App
Configuration, where the rest of its config (flags, critic weights, thresholds)
already sits.

This was fragile in three ways. It was out-of-band from the rest of config, so a
freshly provisioned factory could come up without its record present in whatever
checkout happened to be running the line. `route_product` matched a run's free-text
product hints against registry keys with word-boundary heuristics, which could
mis-route or, when a hint matched nothing, silently sweep unscoped (the
`pets-corp-2` failure mode). And the writers mutated a tracked file at provisioning
and teardown time, mixing runtime state into the repo.

## Decision

- **The `Product` record lives in the product's own App Configuration.** It is
  stored as UNLABELLED JSON-encoded dotted keys — `product.github_repo`,
  `product.label_taxonomy`, `product.foundryiq_scope`, `product.sentry_projects`,
  `product.grafana_dashboards`, `product.azure_monitor_scope` — and the confidence
  threshold reuses the existing `threshold.<product>` key. Unlabelled is correct
  because the per-product store is already product-scoped; a label would be
  redundant.
- **A single accessor reads it:** `dsf.config.flags.product_record(cfg, product)`
  returns the `Product`, reusing the existing `get_value`/`threshold` accessors —
  no `ConfigStore` port change.
- **`dsf new` seeds the record** into the per-product App Configuration (a
  `seed_product_record` provisioning step, `az appconfig kv set`), replacing the
  old file write. Teardown no longer deletes a registry file.
- **Operators read from the owner App Config index.** The control-center product
  list and the `dsf charter` CLI repo resolution call
  `dsf.config.owner_index.list_products` / `repo_for_product`, gated on
  `DSF_OWNER_APPCONFIG_ENDPOINT`.
- **S2 always scopes to the factory's own product, and S6 always routes to it.**
  The `route_product` hint-matching and the "unregistered product → skip" branch
  are removed; each factory serves exactly one product, so scoping and routing are
  deterministic.
- **Fail loud, clean slate.** A missing record raises (`product_record` raises
  `ValueError`; the station then audits an `ERROR` terminal) rather than sweeping
  unscoped. `config/products.json`, the file registry API (`load_registry`,
  `route_product`, `register_product`, `unregister_product`, `deregister_product`),
  and the file-writer provisioning steps are deleted outright — no migration
  shim — consistent with ADR 0014.

## Consequences

- The control-center and `dsf charter` now require `DSF_OWNER_APPCONFIG_ENDPOINT`
  to list products / resolve a repo; with it unset, the control-center product
  list is empty and `dsf charter` falls back to an explicit `GITHUB_REPOSITORY`.
- There is no local-file way to add or inspect a product. To add or change a
  product record you reprovision (or set the `product.*` keys in its App
  Configuration directly).
- S6 routing is simpler and can no longer mis-route via hint heuristics: every
  surviving proposal goes to the factory's one product repo + taxonomy.
- Tests seed the record with the `dsf_testing.config_with_product_record(...)`
  builder (mirroring what the provisioner writes) instead of a registry file.
