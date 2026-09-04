# Verify a release

Verify release assets before installing DSF outside a disposable environment. The GitHub
release publishes the global-tool package, self-contained CLI archives, release metadata, SBOMs,
native package metadata, and provenance.

## Assets to expect

- `DarkSoftwareFactory.Cli.<version>.nupkg` — signed NuGet global tool package.
- `dsf-cli-linux-x64.tar.gz`, `dsf-cli-linux-arm64.tar.gz`, `dsf-cli-osx-x64.tar.gz`,
  `dsf-cli-osx-arm64.tar.gz`, `dsf-cli-win-x64.zip`, `dsf-cli-win-arm64.zip`.
- `release-metadata/SHA256SUMS` — SHA-256 hashes for final immutable artifacts and native
  package metadata.
- `release-metadata/release-verification-key.pem` — Ed25519 public key for detached SBOM
  signature verification.
- `release-metadata/*.spdx.json` — SPDX SBOM for each artifact.
- `release-metadata/*.spdx.json.sig` — Ed25519 detached signature for each SPDX SBOM.
- `release-metadata/provenance.json` and GitHub build-provenance attestation.
- `native-metadata/winget-portable.yaml`, `homebrew-cask.rb`, Debian metadata, and RPM
  metadata for downstream native package submission.

## Verify artifact hashes

Download the artifact and `release-metadata/SHA256SUMS` into the same release directory, then
check the artifact hash:

```bash
sha256sum --check release-metadata/SHA256SUMS --ignore-missing
```

The selected `dsf-cli-<rid>` archive and native package metadata must report `OK`.

## Verify SBOM signatures and public key

Use the published public key to verify each SBOM's detached signature:

```bash
openssl pkeyutl -verify -rawin \
  -pubin -inkey release-metadata/release-verification-key.pem \
  -in release-metadata/dsf-cli-linux-x64.tar.gz.spdx.json \
  -sigfile release-metadata/dsf-cli-linux-x64.tar.gz.spdx.json.sig
```

Repeat for the SBOM matching the artifact you plan to install. The command exits non-zero if
the Ed25519 signature, public key, or SBOM bytes do not match.

## Inspect the SBOM

Open the matching SPDX SBOM and confirm:

- `spdxVersion` is `SPDX-2.3`,
- the package name matches the artifact,
- the artifact checksum matches `SHA256SUMS`,
- NuGet components and `DEPENDS_ON` relationships are present.

## Verify provenance and attestation

Check `release-metadata/provenance.json`:

- `metadata.repository` matches the GitHub repository,
- `metadata.commit` matches the release target commit,
- `metadata.version` matches the tag,
- every subject digest matches the corresponding `SHA256SUMS` entry.

Then verify the GitHub build-provenance attestation for the downloaded artifact:

```bash
gh attestation verify dsf-cli-linux-x64.tar.gz \
  --repo JoranBergfeld/dark-software-factory
```

The attestation must name the release workflow and the same commit as the GitHub release tag.

## Verify native package metadata

For native package submission, compare metadata to the verified assets:

- Winget installer URL and SHA-256 match the Windows archive.
- Homebrew cask URL and SHA-256 match the macOS archive.
- Debian and RPM metadata refer to the published version and archive digest.
- RPM metadata includes the expected detached OpenPGP metadata note from the release manifest.

Install only after artifact hash, SBOM signature, public-key, provenance, attestation, and
native package metadata checks agree.
