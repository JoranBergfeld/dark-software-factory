param(
    [Parameter(Mandatory = $true)]
    [string] $ArtifactRoot
)

$ErrorActionPreference = "Stop"

if (-not $env:WINDOWS_SIGNING_CERTIFICATE_BASE64 -or -not $env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD) {
    throw "Windows Authenticode signing requires protected release environment secrets."
}

$certificatePath = Join-Path $ArtifactRoot "windows-signing.pfx"
[IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($env:WINDOWS_SIGNING_CERTIFICATE_BASE64))

Get-ChildItem -Path $ArtifactRoot -Recurse -File |
    Where-Object { $_.Name -in @("dsf.exe", "dsf-runtime.exe") } | ForEach-Object {
    signtool sign `
        /fd SHA256 `
        /f $certificatePath `
        /p $env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD `
        /tr "http://timestamp.digicert.com" `
        /td SHA256 `
        $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed: $($_.FullName)" }
    signtool verify /pa $_.FullName
    if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed: $($_.FullName)" }
}

Remove-Item $certificatePath -Force
