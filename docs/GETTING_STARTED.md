# Getting started

Dark Software Factory (DSF) is the **blueprint**, not a running factory. You install the
tooling once, then stamp out an isolated factory per product with a single command. This
guide takes you from a fresh clone to your first provisioned factory and how to operate it.

For the big picture (the decide → build → operate loop and the governance harness) read the
[README](../README.md). For day-to-day operations see the [Runbook](RUNBOOK.md); for the
"why" behind the design, the [ADRs](adr/).

## Prerequisites

- **Python 3.12+** and [**uv**](https://docs.astral.sh/uv/). Every command runs through
  `uv run` — never call bare `python`/`pip`/`pytest`.
- The [**GitHub CLI**](https://cli.github.com/) (`gh`), authenticated with `gh auth login`.
  DSF uses it to infer the repo owner (when you omit `--owner`) and to create the product
  repo and its Coding Squad.
- For a real (non `--dry-run`) provision: the [**Azure CLI**](https://learn.microsoft.com/cli/azure/)
  (`az`) logged in to a subscription (`az login`), and the `squad` CLI on your `PATH`.

## Install

```bash
make install   # uv sync --all-packages
```

Verify your checkout is healthy:

```bash
make test          # uv run pytest -q
make lint          # uv run ruff check .
make lint-imports  # enforce the cross-member import boundaries
```

## Create your first factory

The factory CLI is `dsf`. Provisioning a product needs only `--product`:

```bash
uv run dsf new --product <product>
```

Two inputs are inferred so you don't have to pass them:

- **`--owner`** defaults to your gh-authenticated account (resolved via `gh api user`). When
  omitted, DSF prints a warning naming the account; in an interactive terminal it also asks
  you to confirm before creating the repo there. Pass `--owner <org>` to target an
  organization instead.
- **`--name-prefix`** (the base for Azure resource names) defaults to your `--product` key,
  sanitized and randomized to a 12-character, Azure-safe prefix.

**Preview before you commit.** `dsf new` executes for real by default; `--dry-run` prints the
what-if plan and provisions nothing:

```bash
uv run dsf new --product <product> --dry-run                # preview only
uv run dsf new --product <product> --dry-run --write-plan   # preview + persist the manifest
```

The fuller form, pinning everything explicitly:

```bash
uv run dsf new \
  --product microbi \
  --owner my-org \
  --name-prefix microbi \
  --visibility private \
  --location swedencentral \
  --squad-maturity low      # 'low' routes every PR to a human; 'high' auto-merges on green CI
```

Run `uv run dsf new --help` for the full flag list.

### What gets provisioned

A complete, isolated factory for the product:

- a GitHub repo (`<owner>/<product>`) with the DSF label taxonomy and a **Coding Squad**,
- a dedicated Azure resource group (`rg-dsf-<product>`) with the runtime deployed from
  `infra/main.bicep`,
- the product registered in the routing registry (`config/products.json`),
- an **SRE Agent** wired to its production.

The persisted manifest lives under `config/instances/<product>.json`; re-running `dsf new`
for the same product is idempotent (it reuses the persisted name prefix).

## Operate the factory

Once a factory exists, the control CLI is `dsfctl`. It is **pull-only**: it gets work by
sweeping source agents, not from a pushed inbox.

```bash
uv run dsfctl run --signal <signal.json>   # run the intake line for one signal (--dry-run to preview)
uv run dsfctl sweep                         # run a scheduled sweep of the sources
uv run dsfctl serve-orchestrator            # one tick: sweep sources and drive the line
uv run dsfctl serve-agent --kind sentry     # serve a source agent over A2A
```

The runtime is real-only: it requires `DSF_PRODUCT` plus the Azure endpoints to be set (see
[`.env.example`](../.env.example)) and fails fast, naming what is missing, rather than
falling back to a stub. See the [Runbook](RUNBOOK.md) for the full operational flow.

## Govern the factory

People stay outside the loop and govern from the harness. The **Control Center**
(`uv run dsf-control-center`) is a live console for turning critics, source agents, and
triggers on or off, setting per-product confidence thresholds, and flipping the global
dry-run kill switch.
