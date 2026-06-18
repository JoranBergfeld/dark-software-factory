# Deployable-agent registry — design (#25)

- Status: Approved
- Date: 2026-06-18
- Issue: #25 (tech-debt). First slice of #26 (app split).

## Problem

The set of A2A-serveable source agents is hardcoded as `_AGENT_MODULES` inside
the CLI (`src/dsf/cli/control.py`). The CLI is the wrong owner: every new source
agent forces a CLI edit, and the knowledge of "which agents can be served" lives
far from the agents themselves.

## Decision

Move the single source of truth into `src/dsf/agents/registry.py`; the CLI
**consumes** it.

- **`src/dsf/agents/registry.py`** — an explicit mapping of agent name →
  ASGI app **import path** (string), e.g. `"sentry" → "dsf.agents.sentry.main:app"`.
  Strings only: the agent `main` modules build the app at import time, so storing
  paths keeps the registry import-side-effect-free; uvicorn imports the target
  lazily when serving. Public API:
  - `serveable_agents() -> list[str]` — sorted agent names (for `--kind` choices
    and error messages).
  - `app_path(name) -> str | None` — the import path, or `None` if unknown.
- **`dsf.cli.control._cmd_serve_agent`** drops the local `_AGENT_MODULES` dict and
  uses `app_path()` + `serveable_agents()`.

### Approach

Explicit dict (not derived from the enum). Clear, no magic, one obvious place to
edit. A drift-guard test ties it back to `SourceKind` so the two cannot silently
diverge.

### Scope

The five A2A source agents only — `SENTRY`, `GRAFANA`, `FOUNDRYIQ`, `WEBIQ`,
`TICKETS`. The SRE agent is intentionally **excluded**: it is a scheduled sweep
(`dsfctl sre-sweep`) deployed as its own Container App, not an A2A-served app.

## Testing

- Registry keys equal `{k.value.lower() for k in SourceKind}` (a new source agent
  must be registered or explicitly excluded — no silent drift).
- Every registered path resolves to an importable ASGI `app`.
- The CLI serves via the registry: a known kind dispatches to its registered path;
  an unknown kind errors and lists `serveable_agents()`. No hardcoded agent dict
  remains in the CLI.

## Out of scope

The broader `/dsf` `/cli` `/control-center` `/feature-council` split (#26). This
change deliberately keeps the registry under `dsf.agents` so the later move is
mechanical.
