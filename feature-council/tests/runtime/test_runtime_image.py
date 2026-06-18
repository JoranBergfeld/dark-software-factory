"""The orchestrator runtime image mirrors the agent Dockerfile pattern."""

from __future__ import annotations

from pathlib import Path

DOCKERFILE = (
    Path(__file__).resolve().parents[3]
    / "feature-council"
    / "src"
    / "dsf"
    / "runtime"
    / "Dockerfile"
)


def test_runtime_dockerfile_exists():
    assert DOCKERFILE.is_file()


def test_runtime_dockerfile_is_two_stage_nonroot_pinned():
    text = DOCKERFILE.read_text(encoding="utf-8")
    # two-stage build on a digest-pinned slim base:
    assert "AS builder" in text
    assert "python:3.12-slim@sha256:" in text
    # runs as the non-root appuser (uid 1001), like the agent images:
    assert "USER appuser" in text
    assert "--uid 1001" in text


def test_runtime_dockerfile_cmd_runs_azure_sweep_worker():
    text = DOCKERFILE.read_text(encoding="utf-8")
    # the global --mode flag MUST precede the subcommand:
    assert 'CMD ["dsfctl", "--mode", "azure", "serve-orchestrator"]' in text
