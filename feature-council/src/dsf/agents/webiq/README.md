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
| `WEBIQ_PROVIDER` | no | `webiq` | Search provider: `webiq` (Microsoft WebIQ SDK) or `tavily`. Any other value raises. |
| `WEBIQ_API_KEY` | no | — | WebIQ API key override. When unset, the key is read from Key Vault. |
| `WEBIQ_API_KEY_SECRET` | no | `webiq-api-key` | Key Vault secret name holding the WebIQ API key. |
| `AZURE_KEYVAULT_URI` | yes (for `webiq`, when `WEBIQ_API_KEY` unset) | — | Product Key Vault URI the API key is read from. |
| `WEBIQ_MAX_RESULTS` | no | `5` | Max web results per query. |
| `TAVILY_API_KEY` | yes (for `tavily`) | — | Tavily API key. |

## A2A auth (shared)

When `DSF_MODE` is not `local`, the A2A server requires `A2A_BEARER_TOKEN`; callers must
send `Authorization: Bearer <token>`. See `core/src/dsf/a2a/auth.py`.
