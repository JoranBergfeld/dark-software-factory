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

EXPOSE 8080

CMD ["uvicorn", "dsf.agents.foundryiq.main:app", "--host", "0.0.0.0", "--port", "8080"]