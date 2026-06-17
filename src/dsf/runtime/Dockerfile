FROM python:3.12-slim@sha256:d764629ce0ddd8c71fd371e9901efb324a95789d2315a47db7e4d27e78f1b0e9 AS builder

WORKDIR /build

COPY pyproject.toml uv.lock ./
COPY src/ ./src/

RUN pip install --no-cache-dir --prefix=/install .

FROM python:3.12-slim@sha256:d764629ce0ddd8c71fd371e9901efb324a95789d2315a47db7e4d27e78f1b0e9

RUN groupadd --system --gid 1001 appgroup \
    && useradd --system --uid 1001 --gid 1001 --no-create-home --gid appgroup appuser

WORKDIR /app

COPY --from=builder /install /usr/local

USER appuser

# Per-product feature-council orchestrator. Scope is supplied at runtime via
# DSF_PRODUCT (see .env.orchestrator). The global --mode flag MUST precede the
# subcommand.
CMD ["python", "-m", "dsf.cli", "--mode", "azure", "serve-orchestrator"]
