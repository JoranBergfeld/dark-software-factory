#!/usr/bin/env python3
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
from datetime import UTC, datetime
from pathlib import Path


GENERATED_DIRS = {"release-metadata", "native-metadata"}
SPDX_SIGNATURE_SUFFIX = ".spdx.json.sig"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--artifact-root", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--commit", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--private-key", required=True)
    args = parser.parse_args()

    artifact_root = Path(args.artifact_root).resolve()
    metadata_root = artifact_root / "release-metadata"
    native_root = artifact_root / "native-metadata"
    metadata_root.mkdir(parents=True, exist_ok=True)
    native_root.mkdir(parents=True, exist_ok=True)

    assets = collect_assets(artifact_root)
    write_hashes(metadata_root, assets, artifact_root)
    write_sboms(metadata_root, assets, artifact_root, args)
    write_native_metadata(native_root, assets, args)
    write_provenance(metadata_root, assets, artifact_root, args)
    write_public_key(metadata_root, Path(args.private_key))
    return 0


def collect_assets(artifact_root: Path) -> list[Path]:
    assets = [
        path
        for path in artifact_root.rglob("*")
        if path.is_file() and path.relative_to(artifact_root).parts[0] not in GENERATED_DIRS
    ]
    if not assets:
        raise SystemExit(f"No immutable release assets found in {artifact_root}")
    return sorted(assets)


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def write_hashes(metadata_root: Path, assets: list[Path], artifact_root: Path) -> None:
    lines = [f"{sha256(path)}  {path.relative_to(artifact_root).as_posix()}" for path in assets]
    (metadata_root / "SHA256SUMS").write_text("\n".join(lines) + "\n", encoding="utf-8")


def write_sboms(metadata_root: Path, assets: list[Path], artifact_root: Path, args: argparse.Namespace) -> None:
    for asset in assets:
        relative = asset.relative_to(artifact_root).as_posix()
        sbom = {
            "spdxVersion": "SPDX-2.3",
            "dataLicense": "CC0-1.0",
            "SPDXID": "SPDXRef-DOCUMENT",
            "name": f"dsf-cli-{args.version}-{relative}",
            "documentNamespace": f"https://github.com/{args.repository}/releases/{args.version}/{relative}",
            "creationInfo": {
                "created": datetime.now(UTC).replace(microsecond=0).isoformat().replace("+00:00", "Z"),
                "creators": ["Tool: dotnet/eng/generate-release-metadata.py"],
            },
            "packages": [
                {
                    "name": relative,
                    "SPDXID": "SPDXRef-Package-dsf-cli",
                    "versionInfo": args.version,
                    "downloadLocation": "NOASSERTION",
                    "filesAnalyzed": False,
                    "checksums": [{"algorithm": "SHA256", "checksumValue": sha256(asset)}],
                    "licenseConcluded": "NOASSERTION",
                    "licenseDeclared": "NOASSERTION",
                    "copyrightText": "NOASSERTION",
                }
            ],
        }
        sbom_path = metadata_root / f"{safe_name(relative)}.spdx.json"
        sbom_path.write_text(json.dumps(sbom, indent=2, sort_keys=True) + "\n", encoding="utf-8")
        sign_file(sbom_path, Path(args.private_key), metadata_root / f"{safe_name(relative)}{SPDX_SIGNATURE_SUFFIX}")


def sign_file(input_path: Path, private_key: Path, signature_path: Path) -> None:
    if not private_key.exists():
        raise SystemExit(f"Missing Ed25519 private key: {private_key}")
    subprocess.run(
        [
            "openssl",
            "pkeyutl",
            "-sign",
            "-rawin",
            "-inkey",
            str(private_key),
            "-in",
            str(input_path),
            "-out",
            str(signature_path),
        ],
        check=True,
    )


def write_public_key(metadata_root: Path, private_key: Path) -> None:
    subprocess.run(
        [
            "openssl",
            "pkey",
            "-in",
            str(private_key),
            "-pubout",
            "-out",
            str(metadata_root / "release-verification-key.pem"),
        ],
        check=True,
    )


def write_provenance(
    metadata_root: Path,
    assets: list[Path],
    artifact_root: Path,
    args: argparse.Namespace,
) -> None:
    provenance = {
        "buildType": "https://github.com/dark-software-factory/dotnet-cli-release/v1",
        "builder": {"id": "github-actions"},
        "invocation": {"runId": args.run_id},
        "metadata": {"repository": args.repository, "commit": args.commit, "version": args.version},
        "subjects": [
            {"name": path.relative_to(artifact_root).as_posix(), "digest": {"sha256": sha256(path)}}
            for path in assets
        ],
    }
    (metadata_root / "provenance.json").write_text(
        json.dumps(provenance, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )


def write_native_metadata(native_root: Path, assets: list[Path], args: argparse.Namespace) -> None:
    by_name = {path.name: (path, sha256(path)) for path in assets}
    package_url = f"https://github.com/{args.repository}/releases/download/v{args.version}"
    linux_amd64 = first_hash(by_name, "linux-x64")
    win_x64 = first_hash(by_name, "win-x64")
    osx_arm64 = first_hash(by_name, "osx-arm64")

    (native_root / "winget-portable.yaml").write_text(
        f"""PackageIdentifier: DarkSoftwareFactory.Cli
PackageVersion: {args.version}
Installers:
  - Architecture: x64
    InstallerType: portable
    InstallerUrl: {package_url}/dsf-cli-win-x64.zip
    InstallerSha256: {win_x64}
ManifestType: installer
ManifestVersion: 1.9.0
""",
        encoding="utf-8",
    )
    (native_root / "homebrew-cask.rb").write_text(
        f"""cask "dsf-cli" do
  version "{args.version}"
  sha256 arm: "{osx_arm64}"
  url "{package_url}/dsf-cli-osx-arm64.tar.gz"
  name "Dark Software Factory CLI"
  binary "dsf"
end
""",
        encoding="utf-8",
    )
    (native_root / "debian-control").write_text(
        f"""Package: dsf-cli
Version: {args.version}
Architecture: amd64
Maintainer: Dark Software Factory
Description: Dark Software Factory CLI
SHA256: {linux_amd64}
""",
        encoding="utf-8",
    )
    (native_root / "rpm.spec").write_text(
        f"""Name: dsf-cli
Version: {args.version}
Release: 1
Summary: Dark Software Factory CLI
License: NOASSERTION
Source0: {package_url}/dsf-cli-linux-x64.tar.gz
# OpenPGP: sign this source metadata before manual RPM repository submission.
%description
Dark Software Factory CLI.
""",
        encoding="utf-8",
    )


def first_hash(by_name: dict[str, tuple[Path, str]], token: str) -> str:
    for name, (_, digest) in by_name.items():
        if token in name:
            return digest.upper()
    return "NOASSERTION"


def safe_name(value: str) -> str:
    return "".join(character if character.isalnum() else "-" for character in value).strip("-")


if __name__ == "__main__":
    raise SystemExit(main())
