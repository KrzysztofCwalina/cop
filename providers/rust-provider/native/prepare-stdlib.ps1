param(
    [Parameter(Mandatory)][string]$ArchivePath,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = "Stop"
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot "stdlib.json") -Raw | ConvertFrom-Json
$archive = (Resolve-Path -LiteralPath $ArchivePath).Path
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $pin.archiveSha256) {
    throw "Rust source archive checksum does not match the pinned official build input."
}
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$staging = Join-Path $destination (".rust-source-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    & tar -xf $archive -C $staging
    if ($LASTEXITCODE -ne 0) { throw "Cannot extract pinned Rust sources." }
    $component = Join-Path $staging "rust-src-$($pin.release)"
    if ((Get-Content -LiteralPath (Join-Path $component "git-commit-hash") -Raw).Trim() -ne $pin.compilerCommit) {
        throw "Rust source component compiler identity does not match the pinned manifest."
    }
    $bundle = Join-Path $destination "rust-stdlib"
    New-Item -ItemType Directory -Force -Path $bundle | Out-Null
    Copy-Item -LiteralPath (Join-Path $component "rust-src\lib\rustlib\src\rust\library") -Destination $bundle -Recurse -Force
    foreach ($name in @("LICENSE-MIT", "LICENSE-APACHE", "COPYRIGHT", "version", "git-commit-hash")) {
        Copy-Item -LiteralPath (Join-Path $component $name) -Destination $bundle -Force
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot "stdlib.json") -Destination (Join-Path $bundle "manifest.json") -Force
    Write-Host "Prepared pinned Rust $($pin.release) sources and attribution in $bundle"
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
