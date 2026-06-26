# Feature-council orchestrator runtime

The per-product runtime image (`dsf-runtime`). The two-stage `Dockerfile` bundles `core/`
+ `feature-council/` and runs the orchestrator worker:

```
CMD ["python", "-m", "dsf.runtime.control", "serve-orchestrator", "--loop"]
```

DSF is pull-only: each tick sweeps the enabled source agents through the conveyor. `--loop`
keeps the container alive, sweeping every `DSF_SWEEP_INTERVAL` seconds (default 300) and
surviving per-tick errors so the always-on Azure Container App revision stays healthy.

The image is built and pushed to `ghcr.io/<owner>/dsf-runtime` by the `build-runtime` job
in `.github/workflows/agents-images.yml`. The GHCR package must be **public** (the Container
App pulls anonymously).

## Environment variables

`build_services()` (`core/src/dsf/container.py`) resolves these from the environment and
**requires** `DSF_PRODUCT` plus every endpoint below — it raises (naming what is unset) and
never falls back to a stub. `infra/main.bicep` wires them onto the orchestrator container.

| Variable | Required | Purpose |
| --- | --- | --- |
| `DSF_PRODUCT` | yes | Product slug that scopes the factory. |
| `AZURE_APPCONFIG_ENDPOINT` | yes | App Configuration endpoint (critic/agent flags + thresholds). |
| `AZURE_COSMOS_ENDPOINT` | yes | Cosmos DB document endpoint (blackboard + memory). |
| `AZURE_OPENAI_ENDPOINT` | yes | Azure OpenAI endpoint. |
| `AZURE_OPENAI_DEPLOYMENT` | yes | Azure OpenAI chat deployment name. |
| `AZURE_OPENAI_EMBEDDING_DEPLOYMENT` | yes | Azure OpenAI embedding deployment name. |
| `AZURE_CLIENT_ID` | no | User-assigned managed identity for `DefaultAzureCredential` (ADR 0004). |
| `AZURE_KEYVAULT_URI` | no | Key Vault URI (secrets read at runtime via the managed identity). |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | no | Application Insights / OpenTelemetry export. |
| `DSF_SWEEP_INTERVAL` | no | Seconds between sweeps in `--loop` mode (default 300). |

Secrets (bearer/GitHub tokens) are not baked into the image or env — they are read at runtime
from Key Vault via the Container App's managed identity (ADR 0004).
