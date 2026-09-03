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
DOTNET_PROPS = Path(__file__).resolve().parents[3] / "dotnet" / "Directory.Build.props"


def test_runtime_dockerfile_exists():
    assert DOCKERFILE.is_file()


def test_runtime_dockerfile_is_two_stage_nonroot_pinned():
    text = DOCKERFILE.read_text(encoding="utf-8")
    # two-stage build on digest-pinned .NET bases:
    assert "AS builder" in text
    assert "mcr.microsoft.com/dotnet/sdk:10.0@sha256:" in text
    assert "mcr.microsoft.com/dotnet/aspnet:10.0@sha256:" in text
    assert "dotnet publish src/Dsf.Runtime/Dsf.Runtime.csproj" in text
    # runs as the non-root appuser (uid 1001), like the agent images:
    assert "USER appuser" in text
    assert "--uid 1001" in text


def test_runtime_dockerfile_cmd_runs_sweep_worker():
    text = DOCKERFILE.read_text(encoding="utf-8")
    # RuntimeDependencies.Production wires the real per-product bundle from env.
    # --loop keeps the deployed container alive, sweeping enabled sources on an
    # interval so the ACA revision stays healthy.
    assert 'ENTRYPOINT ["dotnet", "dsf-runtime.dll"]' in text
    assert 'CMD ["serve-orchestrator", "--loop"]' in text


def test_runtime_image_starts_at_dotnet_cutover_version():
    text = DOTNET_PROPS.read_text(encoding="utf-8")
    assert "<Version>0.0.1</Version>" in text
