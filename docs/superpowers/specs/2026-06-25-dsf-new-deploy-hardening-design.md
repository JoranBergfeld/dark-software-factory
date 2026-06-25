# `dsf new` deploy hardening: Bing-grounding opt-out + bounded deploy poller

## Problem

`dsf new` step `provision_azure` runs one `az deployment group create` over
`infra/main.bicep` with `--no-wait`, then `DeploymentProgressPoller.stream`
polls the deployment to a terminal state, printing a line whenever a resource
**changes** state.

A real run wedged: 14 of 15 resources reached `Succeeded`, but the Grounding
with Bing Search connection (`Microsoft.CognitiveServices/accounts/projects/connections`,
`...bing-conn...`) stayed `Running` for 9+ minutes. Azure's own Foundry
account-RP returned `HTTP 500 InternalServerError` while materializing the
connection's API-key secret:

```
PUT .../<aif-account>@AML/secrets:putbatch
  -> GET account-rp/.../token?resource=https://vault.azure.net&trustMode=True  => 500
```

ARM keeps the operation `Running` (it does not fail fast) while the RP retries
the 500. Two gaps turned a transient platform fault into an indefinite hang:

1. **The poller has no overall timeout** (`deploy_progress.py` loops until ARM
   returns a terminal state). The CLI hangs as long as ARM does.
2. **`dsf new` cannot opt out of Bing grounding.** `infra/main.bicep` already
   gates the Bing account + Foundry project + connection behind
   `enableBingGrounding` (default `true`), but `_provision_azure_command` never
   passes the parameter, so the operator cannot skip the resource that broke.

This is not a template bug — the connection is declared correctly and the
failure is a server-side 500. The fix is resilience: make the hang bounded and
give the operator an opt-out.

## Goals

- Bound `provision_azure` with a wall-clock timeout that names the wedged
  resource(s) and cancels the stuck ARM deployment so a re-run starts clean.
- Let `dsf new` skip Bing grounding (and its Foundry project + connection)
  without hand-running Bicep.
- Preserve existing behavior by default (Bing grounding on; only the timeout is
  new, with a generous-but-bounded default).

## Non-goals

- Diagnosing or working around the Azure-side 500 itself (platform issue).
- Automatic retry of the deployment. On timeout we cancel and surface a clear
  error; the operator decides whether to re-run or re-run with
  `--no-enable-bing-grounding`.
- Per-resource ("stall") timeouts. A single total wall-clock bound is enough.

## Design

### A. Expose `enableBingGrounding` through `dsf new`

The Bicep parameter already exists (`infra/main.bicep:83`, default `true`). Thread
an operator flag down to it:

- **`InstanceSpec`** (`cli/src/dsf/instance/spec.py`): add
  `enable_bing_grounding: bool = True`.
- **CLI** (`cli/src/dsf/cli/factory.py`): add to `dsf new`
  ```
  --enable-bing-grounding / --no-enable-bing-grounding   (default: enabled)
  ```
  via `argparse.BooleanOptionalAction`; pass `enable_bing_grounding=args.enable_bing_grounding`
  into `InstanceSpec` in `_cmd_new`.
- **Provisioner** (`cli/src/dsf/instance/provisioner.py`): in
  `_provision_azure_command`, append
  `f"enableBingGrounding={'true' if s.enable_bing_grounding else 'false'}"` to the
  `-p` params. (Azure CLI coerces the literal `true`/`false` to the Bicep `bool`.)

When disabled, the Bicep `if (enableBingGrounding)` gates drop the Bing account,
the Foundry project, the connection, and the related role assignment — i.e. the
exact resource that wedged. `WEBIQ_BING_CONNECTION_ID` / `AZURE_AI_PROJECT_ENDPOINT`
resolve to empty strings (already handled in the template), so the runtime
deploys without web research.

### B. Bound the deploy poller

In `cli/src/dsf/instance/deploy_progress.py`:

- **New error:** `DeploymentTimeoutError(RuntimeError)` — `str()` names the
  still-running resource operation(s).
- **Injected clock:** add a `monotonic: Callable[[], float] = time.monotonic`
  constructor parameter (mirrors the injected `sleep`, keeps tests offline and
  deterministic).
- **Timeout resolution:** `_resolve_timeout()` reads env
  **`DSF_DEPLOY_TIMEOUT`** (seconds); default **600 (10 minutes)**. A value
  `<= 0` disables the bound (infinite wait) for the rare legitimately-long
  deployment. An explicit constructor `timeout: float | None` overrides the env.
- **Loop change:** in `stream`, capture `start = self._monotonic()`. Each
  iteration, after `_emit_changes`, if the timeout is enabled and
  `self._monotonic() - start >= timeout` while the deployment state is still
  non-terminal:
  1. Best-effort `az deployment group cancel -g <rg> -n <name>` (run with
     `check=False`, swallow exceptions — a failed cancel must never mask the
     timeout).
  2. Raise `DeploymentTimeoutError` whose message names the operations from the
     last poll whose `provisioningState` is not terminal (e.g. the
     `...projects/connections ...bing-conn...` op), reusing the same
     `resourceType resourceName` labelling as `_failure_detail`.

The provisioner needs no change for the timeout — the poller resolves
`DSF_DEPLOY_TIMEOUT` itself, exactly as it already resolves
`DSF_DEPLOY_POLL_INTERVAL`.

### C. Error surfacing

`provision_azure` already converts a raised poller error into a failed step via
`_format_step_error`. `DeploymentTimeoutError`'s message (wedged resource names)
therefore flows to the existing `provisioning STOPPED at 'provision_azure': ...`
line with no extra wiring.

## Testing (TDD, all offline)

- **`cli/tests/instance/test_deploy_progress.py`**
  - Timeout fires: deployment stays `Running` past the bound → `stream` raises
    `DeploymentTimeoutError`; message names the still-`Running` resource; an
    `az deployment group cancel` call was issued. Drive elapsed time with an
    injected `monotonic` returning scripted increasing values.
  - A cancel that raises/non-zero does not mask the `DeploymentTimeoutError`.
  - Success reached before the timeout is unaffected (no cancel, returns outputs).
  - `_resolve_timeout`: env parsing, default 600, `<= 0` disables.
- **`cli/tests/instance/test_provisioner.py`**
  - `_provision_azure_command` includes `enableBingGrounding=true` by default and
    `enableBingGrounding=false` when `spec.enable_bing_grounding` is `False`.
- **`cli/tests/cli/test_factory.py`**
  - `dsf new --no-enable-bing-grounding` builds a spec with
    `enable_bing_grounding=False`; the default leaves it `True`.

## Verification gates

- `uv run pytest -q`
- `uv run ruff check .`
- `uv run lint-imports` (expect "4 kept, 0 broken")

## Docs

Add a short recovery note to `docs/site/get-started/operate.md`: if
`provision_azure` hangs on a Foundry/Bing connection, the deploy is bounded by
`DSF_DEPLOY_TIMEOUT` (default 10 min) and re-running with
`--no-enable-bing-grounding` skips the connection.

## Files touched

- `cli/src/dsf/instance/spec.py` — `enable_bing_grounding` field
- `cli/src/dsf/cli/factory.py` — `--enable-bing-grounding/--no-...` flag + plumb
- `cli/src/dsf/instance/provisioner.py` — `enableBingGrounding=` param
- `cli/src/dsf/instance/deploy_progress.py` — timeout + cancel + new error
- `cli/tests/instance/test_deploy_progress.py` — timeout tests
- `cli/tests/instance/test_provisioner.py` — param tests
- `cli/tests/cli/test_factory.py` — flag test
- `docs/site/get-started/operate.md` — recovery note
