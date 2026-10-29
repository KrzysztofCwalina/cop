param(
    [Parameter(Mandatory)][ValidateSet("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")]
    [string]$Runtime,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [switch]$Offline
)
$ErrorActionPreference = "Stop"
$targets = @{
    "win-x64" = "x86_64-pc-windows-msvc"; "win-arm64" = "aarch64-pc-windows-msvc"
    "linux-x64" = "x86_64-unknown-linux-gnu"; "linux-arm64" = "aarch64-unknown-linux-gnu"
    "osx-x64" = "x86_64-apple-darwin"; "osx-arm64" = "aarch64-apple-darwin"
}
$manifest = Join-Path $PSScriptRoot "Cargo.toml"
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$target = $targets[$Runtime]
$version = & rustc -vV
if ($LASTEXITCODE -ne 0) { throw "A working Rust development toolchain is required to build the frontend." }
$hostTarget = ($version | Where-Object { $_.StartsWith("host: ") }).Substring(6)
$arguments = @("build", "--release", "--locked", "--manifest-path", $manifest)
if ($Offline) { $arguments += "--offline" }
if ($hostTarget -ne $target) { $arguments += @("--target", $target) }
$oldFlags = $env:CARGO_ENCODED_RUSTFLAGS
try {
    if ($Runtime.StartsWith("win-")) {
        $env:CARGO_ENCODED_RUSTFLAGS = "-C$([char]31)target-feature=+crt-static"
    }
    & cargo @arguments
    if ($LASTEXITCODE -ne 0) { throw "Rust frontend build failed. Cross-compilers/targets must be provisioned by release CI, never installed at runtime." }
} finally { $env:CARGO_ENCODED_RUSTFLAGS = $oldFlags }
$buildRoot = Join-Path $PSScriptRoot "target"
if ($hostTarget -ne $target) { $buildRoot = Join-Path $buildRoot $target }
$executable = if ($Runtime.StartsWith("win-")) { "cop-rust-frontend.exe" } else { "cop-rust-frontend" }
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot "release\$executable") -Destination $destination -Force

$metadataArguments = @("metadata", "--locked", "--format-version", "1", "--manifest-path", $manifest)
if ($Offline) { $metadataArguments += "--offline" }
$metadata = & cargo @metadataArguments | ConvertFrom-Json
if ($LASTEXITCODE -ne 0) { throw "Cannot collect frontend dependency attribution." }
$licenses = Join-Path $destination "rust-frontend-licenses"
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
$entries = foreach ($package in $metadata.packages | Where-Object { $_.name -ne "cop-rust-frontend" }) {
    $directory = Split-Path $package.manifest_path
    $texts = @(Get-ChildItem -LiteralPath $directory -File |
        Where-Object { $_.Name -match "^(LICENSE|COPYING|NOTICE|COPYRIGHT)" })
    if ($package.license_file) {
        $texts += Get-Item -LiteralPath (Join-Path $directory $package.license_file)
    }
    if ($texts.Count -eq 0) {
        $group = switch -Regex ($package.name) {
            '^ra_ap_|^la-arena$' { 'rust-analyzer'; break }
            '^ra-ap-rustc' { 'rust'; break }
            '^line-index$' { 'line-index'; break }
            '^perf-event-open-sys$' { 'perf-event-open-sys'; break }
            '^perf-event$' { 'perf-event'; break }
            '^valuable$' { 'valuable'; break }
            default { throw "No license text is available for $($package.name); update pinned notices before distributing." }
        }
        $texts = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot "notices\$group") -File)
    }
    $packageDestination = Join-Path $licenses "$($package.name)-$($package.version)"
    New-Item -ItemType Directory -Force -Path $packageDestination | Out-Null
    foreach ($text in $texts) { Copy-Item -LiteralPath $text.FullName -Destination $packageDestination -Force }
    [ordered]@{ name = $package.name; version = $package.version; license = $package.license
        repository = $package.repository; notices = @($texts.Name | Sort-Object -Unique) }
}
$entries | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $licenses "manifest.json") -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "notices\provenance.json") -Destination $licenses -Force
Write-Host "Built pinned Rust frontend for $Runtime in $destination (include rust-frontend-licenses when distributing)."
