# Float per-resource progress during `provision_azure` (Design)

**Status:** Accepted (2026-06-22). Design charter for surfacing live per-resource
deployment progress during step 5 of `dsf new`. The plan and code follow.

**Scope:** Step 5 of the `dsf new` provisioner (`provision_azure`) runs a single
`az deployment group create` over `infra/main.bicep`, which stamps 15+ Azure
resources and takes several minutes. Today it runs with `capture_output=True`, so
the console is silent for the whole deployment — the operator cannot tell whether
it is progressing or hung. Float the bicep's per-resource deployments to the
console as live sub-step lines, the same way the 11 top-level steps already show.

---

## 1. Problem

`Provisioner._execute_step` runs `provision_azure` as one blocking, output-captured
subprocess:

```python
proc = self._run(step.command, check=True, capture_output=True, text=True)
# step.command == ["az","deployment","group","create", ..., "--query","properties.outputs","-o","json"]
azure_result = self._parse_outputs(proc.stdout)
```

Between the `▶ [5/11] provision_azure` line and its `✓`/`✗`, nothing prints for
minutes. The deployment is many separate resource operations (Cosmos, App Config,
Key Vault, AI Foundry + 2 model deployments, AKS, the Container App, role
assignments, …) but none of that is visible. On failure the operator gets one giant
`DeploymentFailed` JSON blob rather than "this specific resource failed, here's
why".

## 2. Principles

- **Default-on.** Streaming matches how the top-level steps already display; no flag
  to remember. Dry-run is unaffected (there is no deployment).
- **Additive, not invasive.** The existing step-level `on_event` contract and the
  success/outputs parsing stay intact. Progress is a new, separate channel.
- **Offline-testable.** Like the rest of the provisioner, the new unit drives Azure
  only through the injected `run` callable and an injected `sleep`, so tests stay
  hermetic with canned JSON.
- **Better failure UX as a side effect.** The same per-resource data we poll for
  progress lets us report the specific failed operation and its message.

## 3. Approach

Chosen: **`--no-wait` + poll** (over running the blocking create in a background
thread). Starting the deployment asynchronously and polling sequentially keeps the
flow single-threaded — deterministic and easy to test — and lets us drive both the
progress lines and a precise failure message from one source of truth (the
deployment's operation list). The blocking-thread alternative would preserve today's
exact create call but makes tests timing-dependent; rejected.

Flow for `provision_azure`:

1. `az deployment group create … --no-wait` (no `--query/-o json`; it returns once
   the deployment is accepted).
2. Poll loop every `interval` seconds:
   - `az deployment operation group list -g <rg> -n <name> -o json`
   - Diff each operation's `properties.provisioningState` against the last seen
     state; for newly-`Running` and newly-terminal (`Succeeded`/`Failed`) resource
     operations, emit a formatted line.
   - `az deployment group show -g <rg> -n <name> --query properties.provisioningState -o tsv`
     to detect the terminal state of the whole deployment.
3. On `Succeeded`: `az deployment group show … --query properties.outputs -o json`,
   parsed by the existing `_parse_outputs`.
4. On `Failed`: raise `DeploymentFailedError` (new). `apply` already catches any
   `Exception` from a step, so this records `result=failed` and STOPS the line
   exactly as today; `_format_step_error` surfaces the exception's `str()`, which
   names the failed operation(s) and their `statusMessage`.

The deployment name (`dsf-<product>`) and resource group (`rg-dsf-<product>`) are the
same values already used to build the create command.

## 4. Components

- **`DeploymentProgressPoller`** (new, `cli/src/dsf/instance/deploy_progress.py`) — a
  small bounded unit. Constructed with the injected `run` callable, an injected
  `sleep`, an `emit(line: str)` callback, and the poll `interval`. Method
  `stream(resource_group, deployment_name) -> dict[str, str]` runs the poll loop and
  returns the parsed outputs, or raises `DeploymentFailedError` (carrying the failed
  operations' messages). It owns: operation-list parsing, per-resource state diffing,
  line formatting, terminal detection. It is unit-tested with a fake `run` returning a
  scripted sequence of operation-list JSON and a fake `sleep`.
  - **Line format:** `· <resourceType> <resourceName>: <state> (<duration>)`, e.g.
    `· Microsoft.DocumentDB/databaseAccounts cosmos-test1abcd: ✓ Succeeded (1m04s)`.
    `Running` uses `…`, `Succeeded` uses `✓`, `Failed` uses `✗`. Duration comes from
    the operation's `properties.duration` (ISO-8601, e.g. `PT64S`) when present, and
    is omitted from the line when absent.
  - Operations whose `targetResource` is missing/the deployment itself are skipped.

- **`Provisioner.apply(..., on_progress: Callable[[str], None] | None = None)`** —
  new optional progress channel, default no-op, threaded down to `_execute_step`.
  The `on_event` step callback is unchanged.

- **`Provisioner._execute_step`** — the `provision_azure` branch switches to the
  `--no-wait` create then drives a `DeploymentProgressPoller`, passing `on_progress`
  as `emit`. `_provision_azure_command` is split into a no-wait create command and a
  separate outputs `show` command (built from the same spec values).

- **`factory._cmd_new`** — passes `on_progress=lambda line: print(f"[dsf]     {line}",
  flush=True)`, indented under the `▶ [5/11]` line, alongside the existing
  `on_event=_print_step_event`. `_print_step_event` is now the shared step handler
  used by both `_cmd_new` and `_cmd_delete` (consolidated in PR #69); it is unchanged
  here. `on_progress` is wired only in `_cmd_new`, so the shared handler and the delete
  flow are unaffected.

- **Poll interval** — default 5s; overridable via `DSF_DEPLOY_POLL_INTERVAL`
  (parsed once, like the runtime's `DSF_SWEEP_INTERVAL`).

## 5. Testing

- `DeploymentProgressPoller` unit tests (fake `run` + fake `sleep`): emits a
  `Running` then a terminal line per resource; skips the deployment's own operation;
  formats durations; returns parsed outputs on success; raises
  `DeploymentFailedError` with the failed op's message on failure; tolerates an empty
  first poll (operations not yet listed) without crashing.
- `Provisioner` tests: `provision_azure` issues a `--no-wait` create and an outputs
  `show`; `on_progress` lines are forwarded; a failed deployment still records
  `result=failed` and stops the line (existing contract preserved).
- `factory` test: `on_progress` output is indented under the step line during an
  execute run.

## 6. Out of scope

- No progress for the other (fast) steps. No spinner/TTY redraw — plain append-only
  lines (works in CI logs too). No change to dry-run. No retry/backoff changes to the
  deployment itself. The deferred `publicNetworkAccess` reachability work is
  unrelated.
- `dsf delete` teardown (merged PR #69) is a separate flow: it drives `az group delete`
  and registry/repo removal, not a `deployment group create` with pollable per-resource
  operations, so this poller does not apply to it. Streaming teardown progress is a
  possible later follow-up, out of scope here.

## 7. What changes in this repo

- New `cli/src/dsf/instance/deploy_progress.py` (+ tests under `cli/tests/instance/`).
- `cli/src/dsf/instance/provisioner.py`: `apply`/`_execute_step` progress channel and
  the `provision_azure` no-wait + poll switch; split `_provision_azure_command`.
- `cli/src/dsf/cli/factory.py`: wire `on_progress`.
- Docs: a line in `infra/README.md` / `docs/RUNBOOK.md` noting the live progress.
