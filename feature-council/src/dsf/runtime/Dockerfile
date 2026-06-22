FROM python:3.12-slim@sha256:d764629ce0ddd8c71fd371e9901efb324a95789d2315a47db7e4d27e78f1b0e9 AS builder

WORKDIR /build

COPY core/ ./core/
COPY feature-council/ ./feature-council/

RUN pip install --no-cache-dir --prefix=/install ./core ./feature-council

FROM python:3.12-slim@sha256:d764629ce0ddd8c71fd371e9901efb324a95789d2315a47db7e4d27e78f1b0e9

RUN groupadd --system --gid 1001 appgroup \
    && useradd --system --uid 1001 --gid 1001 --no-create-home --gid appgroup appuser

WORKDIR /app

COPY --from=builder /install /usr/local

USER appuser

# Per-product feature-council orchestrator (the dsfctl instance-control CLI).
# Scope and endpoints are supplied at runtime via DSF_PRODUCT and the Azure
# runtime env (see .env.orchestrator); build_services wires the real bundle.
# --loop keeps the container alive, sweeping the enabled sources every
# DSF_SWEEP_INTERVAL seconds (default 300) and surviving per-tick errors.
CMD ["dsfctl", "serve-orchestrator", "--loop"]
