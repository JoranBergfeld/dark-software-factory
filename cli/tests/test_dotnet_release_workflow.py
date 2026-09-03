from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parents[2]
WORKFLOW = REPO_ROOT / ".github" / "workflows" / "dotnet-release.yml"

FINAL_NATIVE_SMOKE_MATRIX = {
    "linux-x64": "ubuntu-latest",
    "linux-arm64": "ubuntu-24.04-arm",
    "osx-x64": "macos-15-intel",
    "osx-arm64": "macos-14",
    "win-x64": "windows-latest",
    "win-arm64": "windows-11-arm",
}


def _workflow_text() -> str:
    return WORKFLOW.read_text(encoding="utf-8")


def test_dotnet_release_has_final_native_smoke_matrix_for_every_rid():
    workflow = _workflow_text()

    assert "final-native-smoke-test:" in workflow
    assert "needs: collect-final-release-artifacts" in workflow
    assert "dsf-cli-final-release-bundle" in workflow
    assert "./dotnet/eng/smoke-test-release-artifact.sh" in workflow

    smoke_job = workflow.split("final-native-smoke-test:", maxsplit=1)[1].split(
        "\n  publish-github-release:", maxsplit=1
    )[0]
    for rid, runner in FINAL_NATIVE_SMOKE_MATRIX.items():
        assert f"rid: {rid}" in smoke_job
        assert f"os: {runner}" in smoke_job


def test_dotnet_release_publish_jobs_wait_for_final_native_smoke():
    workflow = _workflow_text()

    github_publish_job = workflow.split("publish-github-release:", maxsplit=1)[1].split(
        "\n  publish-nuget:", maxsplit=1
    )[0]
    nuget_publish_job = workflow.split("publish-nuget:", maxsplit=1)[1]

    assert "needs: final-native-smoke-test" in github_publish_job
    assert "final-native-smoke-test" in nuget_publish_job


def test_final_smoke_extracts_windows_zip_without_gnu_tar():
    workflow = _workflow_text()

    smoke_job = workflow.split("final-native-smoke-test:", maxsplit=1)[1].split(
        "\n  publish-github-release:", maxsplit=1
    )[0]

    assert "Expand-Archive" in smoke_job
    assert 'tar -xf "$archive"' not in smoke_job
