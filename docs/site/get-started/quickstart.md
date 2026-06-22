# Quickstart

Dark Software Factory (DSF) is the **blueprint**, not a running factory. You install the
tooling once, then stamp out an isolated factory per product with a single command. This
guide takes you from a fresh clone to a healthy checkout.

For the big picture (the decide → build → operate loop and the governance harness) read
[The loop](../concept/the-loop.md) and [The harness](../concept/the-harness.md). For the
"why" behind the design, see the
[ADRs](https://github.com/JoranBergfeld/dark-software-factory/tree/main/docs/adr).

## Prerequisites

- **Python 3.12+** and [**uv**](https://docs.astral.sh/uv/). Every command runs through
  `uv run` — never call bare `python`/`pip`/`pytest`.
- The [**GitHub CLI**](https://cli.github.com/) (`gh`), authenticated with `gh auth login`.
  DSF uses it to infer the repo owner (when you omit `--owner`) and to create the product
  repo and its Coding Squad.
- For a real (non `--dry-run`) provision: the
  [**Azure CLI**](https://learn.microsoft.com/cli/azure/) (`az`) logged in to a subscription
  (`az login`), and the `squad` CLI on your `PATH`.

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

Next: [provision a factory](provision-a-factory.md).
