# Sentry source agent

Pulls Sentry issues (regressions, high-frequency errors, newly seen issues) and maps
each to an `EvidenceItem`, served over A2A so the conveyor's S2 investigation station
can gather from it.

Serve it with `dsfctl serve-agent --kind sentry` (ASGI app: `dsf.agents.sentry.main:app`).

## Backend selection (`DSF_MODE`)

- `local` (default) — the deterministic fixture backend (`tests/fixtures/sentry_evidence.json`),
  no network.
- anything else (e.g. `live`) — the real Sentry backend. It prefers the **MCP** path when
  `SENTRY_MCP_URL` is set, else the **REST** path when `SENTRY_AUTH_TOKEN` is set, else it
  raises (live mode never fabricates coverage).

## Environment variables (live mode)

MCP path:

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `SENTRY_MCP_URL` | yes | — | Sentry MCP server (Streamable-HTTP) URL. Selects the MCP path. |
| `SENTRY_MCP_TOKEN` | no | — | Bearer token for the MCP endpoint. |
| `SENTRY_ORG` | no | — | Default organization slug when the run scope omits it. |

REST path (used when `SENTRY_MCP_URL` is unset but `SENTRY_AUTH_TOKEN` is set):

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `SENTRY_AUTH_TOKEN` | yes | — | Sentry API bearer token. Selects the REST path. |
| `SENTRY_BASE_URL` | no | `https://sentry.io` | Sentry API base URL. |
| `SENTRY_ORG` | no | — | Default organization slug. |
| `SENTRY_PROJECT` | no | — | Default project slug (org-wide search when omitted). |

## A2A auth (shared)

When `DSF_MODE` is not `local`, the A2A server requires `A2A_BEARER_TOKEN`; callers must
send `Authorization: Bearer <token>`. See `core/src/dsf/a2a/auth.py`.
