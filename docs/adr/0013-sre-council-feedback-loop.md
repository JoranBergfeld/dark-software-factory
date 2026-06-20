# 13. SRE-to-council feedback loop via operational council sources

Date: 2026-06-20

## Status

Accepted. Builds on ADR 0009 (leverage the managed Azure SRE Agent), ADR 0011
(deliberative council), and ADR 0012 (coding-squad Ralph watch loop).

## Context

The factory could build (council) and ship (squad) but could not reflect: the
managed Azure SRE Agent observed production and filed incidents, yet nothing
carried what it learned back into the council. Phase 3 named this the slow path
and left it deferred, so recurring production faults never became systemic
hardening proposals and live telemetry never informed what the council built next.

Two hazards shaped the decision. First, the council files its own issues into the
product repo with `squad:ready`, so a naive "read the repo" source would re-ingest
council output and loop. Second, judging an incident as systemic is exactly the
synthesize-then-validate work the deliberative council already does (ADR 0011); a
lone SRE step should not make that call.

## Decision

Feed operations into the council as ordinary sources. Add two `SourceKind`s,
`INCIDENTS` and `AZUREMONITOR`, each with a source agent following the existing
fixture-plus-live backend pattern. Their `EvidenceItem`s ride the existing S1 to
S7 conveyor; synthesis, the validation jury, the confidence threshold, dry-run
governance, and FEATURE/FIX routing are unchanged (no new stage, no mini-council).

A single `incident` label is the whole SRE-to-council contract: the SRE Agent
stamps it (per the onboarding runbook), the `incidents` source pulls only issues
carrying it, and because council-filed issues carry `squad:ready` and never
`incident`, the loop cannot self-ingest. Recurrence intelligence lives in the
`incidents` backend, which collapses a repeated signature into one higher-
confidence item; the conveyor's threshold then decides. The `azuremonitor` source
pulls Application Insights telemetry scoped by `Product.azure_monitor_scope`.

The telemetry agent's live Azure access (Monitoring Reader) stays its own identity
seam, refined later; the offline fixture path is fully functional.

## Consequences

- The third loop closes: recurring incidents and telemetry become council
  proposals through the same validated pipeline, not single-agent judgments.
- `create_labels` now creates the `incident` label; the SRE runbook instructs the
  agent to stamp it; provisioning threads `azure_monitor_scope` into the registry.
- New operational sources are registry-driven (DEPLOYABLE_AGENTS + AGENT_BUILDERS);
  the scheduled sweep gathers them automatically once enabled in config.
- The live telemetry role binding is an explicit, documented follow-up, not a
  half-built feature.
