# Frozen Python Parity Matrix

Captured: `2026-09-03T08:24:03.665+02:00`

Authoritative means later .NET tests may assert against the linked files without executing Python.
Deferred/non-authoritative rows name shape evidence only; live effects need separate migration decisions.

| Surface | Authority | Evidence kinds | Evidence |
| --- | --- | --- | --- |
| `dsf new` | authoritative | command-behavior, exit-behavior, dry-run-plan, persisted-record | `evidence/commands/dsf-new-dry-run-write-plan.json`<br>`evidence/commands/dsf-new-invalid-prefix.json`<br>`evidence/dry-run-plans/dsf-new-paritydemo-write-plan.json`<br>`evidence/persisted-records/instance-manifest-paritydemo.json` |
| `dsf list` | authoritative | command-behavior, exit-behavior, machine-readable-output | `evidence/commands/dsf-list-json-no-owner-index.json`<br>`evidence/commands/dsf-parser-surface-snapshot.json` |
| `dsf bootstrap` | deferred | command-behavior | `evidence/commands/dsf-parser-surface-snapshot.json` |
| `dsf offboard` | authoritative | command-behavior, exit-behavior | `evidence/commands/dsf-offboard-dry-run-missing-manifest.json`<br>`evidence/commands/dsf-parser-surface-snapshot.json` |
| `dsf delete/deprovision` | authoritative | command-behavior, exit-behavior | `evidence/commands/dsf-delete-missing-manifest.json`<br>`evidence/commands/dsf-parser-surface-snapshot.json` |
| `dsf run` | authoritative | command-behavior, exit-behavior, request-shape, schema-snapshot | `evidence/commands/dsf-runtime-run-missing-env.json`<br>`evidence/request-shapes/runtime-signal-to-run.json`<br>`evidence/schemas/Run.json` |
| `dsf sweep` | authoritative | command-behavior, exit-behavior | `evidence/commands/dsf-runtime-sweep-missing-env.json`<br>`evidence/commands/dsf-parser-surface-snapshot.json` |
| `dsf serve-orchestrator` | authoritative | command-behavior, exit-behavior | `evidence/commands/dsf-parser-surface-snapshot.json`<br>`evidence/commands/dsf-runtime-sweep-missing-env.json` |
| `dsf serve-agent` | authoritative | command-behavior | `evidence/commands/dsf-parser-surface-snapshot.json` |
| `dsf charter init` | deferred | command-behavior, request-shape | `evidence/commands/dsf-parser-surface-snapshot.json`<br>`evidence/request-shapes/github-repo-client-recorded-requests.json` |
| `dsf charter sync` | deferred | command-behavior, persisted-record | `evidence/commands/dsf-parser-surface-snapshot.json`<br>`evidence/persisted-records/blackboard-run-record.json` |
| `dsf charter status` | deferred | command-behavior | `evidence/commands/dsf-parser-surface-snapshot.json` |
| `dsf charter implement` | deferred | command-behavior, request-shape | `evidence/commands/dsf-parser-surface-snapshot.json`<br>`evidence/request-shapes/github-repo-client-recorded-requests.json` |
| `dsf charter watch` | deferred | command-behavior, request-shape | `evidence/commands/dsf-parser-surface-snapshot.json`<br>`evidence/request-shapes/github-repo-client-recorded-requests.json` |
| `dsf-control-center` | authoritative | command-behavior, exit-behavior | `evidence/commands/dsf-control-center-parser-snapshot.json` |
| `control-center GET /api/state` | authoritative | machine-readable-output, request-shape, persisted-record | `evidence/machine-readable-outputs/control-center-api-state-response.json`<br>`evidence/request-shapes/control-center-http-requests.json`<br>`evidence/persisted-records/control-center-state-record.json` |
| `control-center POST /toggle` | authoritative | request-shape, persisted-record | `evidence/request-shapes/control-center-http-requests.json`<br>`evidence/persisted-records/control-center-state-record.json` |
| `control-center POST /set-value` | authoritative | request-shape, persisted-record | `evidence/request-shapes/control-center-http-requests.json`<br>`evidence/persisted-records/control-center-state-record.json` |
| `blackboard contracts` | authoritative | schema-snapshot, persisted-record | `evidence/schemas/Run.json`<br>`evidence/schemas/Proposal.json`<br>`evidence/schemas/EvidenceItem.json`<br>`evidence/schemas/CouncilVerdict.json`<br>`evidence/schemas/RoutedIssue.json`<br>`evidence/schemas/AuditRecord.json`<br>`evidence/schemas/CriticScore.json`<br>`evidence/schemas/Provenance.json`<br>`evidence/persisted-records/blackboard-run-record.json`<br>`evidence/persisted-records/blackboard-checkpoint-record.json` |
