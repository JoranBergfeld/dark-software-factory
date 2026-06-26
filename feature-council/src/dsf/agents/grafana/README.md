# Grafana source agent

Pulls active Grafana-managed alerts (and, when a spec carries a PromQL `query` +
`datasource_uid`, instant query results) and maps each to an `EvidenceItem`, served over
A2A for the conveyor's S2 investigation station.

Serve it with `dsf serve-agent --kind grafana` (ASGI app: `dsf.agents.grafana.main:app`).

## Backend selection (`DSF_MODE`)

- `local` (default) — the deterministic fixture backend, no network.
- anything else (e.g. `live`) — the real Grafana HTTP backend.

## Environment variables (live mode)

| Variable | Required | Default | Purpose |
| --- | --- | --- | --- |
| `GRAFANA_URL` | yes | — | Base URL of the Grafana instance. |
| `GRAFANA_TOKEN` | yes | — | Grafana API bearer token. |

## A2A auth (shared)

When `DSF_MODE` is not `local`, the A2A server requires `A2A_BEARER_TOKEN`; callers must
send `Authorization: Bearer <token>`. See `core/src/dsf/a2a/auth.py`.
