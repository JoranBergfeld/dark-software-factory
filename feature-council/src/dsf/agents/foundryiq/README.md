# FoundryIQ source agent

Retrieves knowledge chunks from an Azure AI Search index (the knowledge index behind
FoundryIQ) and maps each to an `EvidenceItem`, served over A2A for the conveyor's S2
investigation station.

Serve it with `dsf serve-agent --kind foundryiq` (ASGI app:
`dsf.agents.foundryiq.main:app`).

## Backend selection (`DSF_MODE`)

- `local` (default) — the deterministic fixture backend, no network.
- anything else (e.g. `live`) — the real Azure AI Search backend.

## Environment variables (live mode)

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `AZURE_SEARCH_ENDPOINT` | yes | — | Search service endpoint, e.g. `https://<svc>.search.windows.net`. |
| `AZURE_SEARCH_INDEX` | yes | — | Knowledge index name. |
| `AZURE_SEARCH_KEY` | yes | — | Admin/query API key (`api-key` header). |
| `AZURE_SEARCH_CONTENT_FIELD` | no | `content` | Document field used as the chunk summary. |
| `AZURE_SEARCH_REF_FIELD` | no | `url` | Document field used as the chunk citation. |

## A2A auth (shared)

When `DSF_MODE` is not `local`, the A2A server requires `A2A_BEARER_TOKEN`; callers must
send `Authorization: Bearer <token>`. See `core/src/dsf/a2a/auth.py`.
