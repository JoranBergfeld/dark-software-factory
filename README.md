# Dark Software Factory

> An autonomous software factory: software that decides what to build, builds it, and
> keeps it running. People stay outside the process and govern it.

Dark Software Factory (DSF) runs the software development loop on agents: a **Feature
Council** decides what to build, a **Coding Squad** builds it, and an **SRE Agent** operates
it and feeds production back to the start. People sit outside the loop and govern it through a
harness of guardrails, policy, and configuration. This repository is the blueprint — one
command stamps out a complete, isolated factory per product.

```mermaid
flowchart LR
    signals(["market and operational signals"]) --> FC["Feature Council<br/>decide what to build"]
    FC -->|issues| CS["Coding Squad<br/>build it"]
    CS -->|PRs| SRE["SRE Agent<br/>operate and feed back"]
    SRE --> prod(["production"])
    SRE -->|fix-forward incidents| CS
    SRE -->|signals and lessons| FC
```

## Read the docs →

The concept and how-to-use guides live on the documentation site:

**<https://joranbergfeld.github.io/dark-software-factory/>**

It covers the loop, each phase, the governance harness, and how to provision and operate a
factory. The site source lives under `docs/site/`; architecture decisions stay in
[`docs/adr/`](docs/adr/).

## Build

DSF is a `uv` workspace with four members, all sharing the one PEP 420 `dsf` namespace:

| Member | Package | Role |
|---|---|---|
| `core/` | `dsf-core` | shared base: contracts, ports, config, memory, model, observability, a2a |
| `feature-council/` | `dsf-feature-council` | the runtime: agents, council, orchestrator, triggers, evals |
| `cli/` | `dsf-cli` | factory CLI (`dsf`) + per-product provisioning |
| `control-center/` | `dsf-control-center` | governance web UI (FastAPI + Jinja) |

Canonical commands:

```bash
make install       # uv sync --all-packages
make test          # uv run pytest -q
make lint          # uv run ruff check .
make lint-imports  # enforce cross-member import boundaries (gates CI)
```

Run the docs site locally:

```bash
uv run --group docs mkdocs serve              # preview at http://127.0.0.1:8000
uv run --group docs mkdocs build --strict     # what CI builds
```

New here? Start with the
[Quickstart](https://joranbergfeld.github.io/dark-software-factory/get-started/quickstart/).
