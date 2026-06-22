# WebIQ source agent

Runs web searches (external/market signals) via a real provider and maps each result to
an `EvidenceItem`, served over A2A for the conveyor's S2 investigation station.

Serve it with `dsfctl serve-agent --kind webiq` (ASGI app: `dsf.agents.webiq.main:app`).

## Backend selection (`DSF_MODE`)

- `local` (default) — the deterministic fixture backend, no network.
- anything else (e.g. `live`) — the real web-search backend.

## Environment variables (live mode)

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `WEBIQ_PROVIDER` | no | `tavily` | Search provider. Only `tavily` is supported; any other value raises. |
| `TAVILY_API_KEY` | yes (for `tavily`) | — | Tavily API key. |

## A2A auth (shared)

When `DSF_MODE` is not `local`, the A2A server requires `A2A_BEARER_TOKEN`; callers must
send `Authorization: Bearer <token>`. See `core/src/dsf/a2a/auth.py`.
