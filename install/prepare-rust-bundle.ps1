param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime,
    [string]$RustSourceArchive,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$nativeRoot = Join-Path $PSScriptRoot '..\providers\rust-provider\native'
$bundleRoot = Join-Path $PSScriptRoot 'native-bundles'
$sourcePin = Get-Content -LiteralPath (Join-Path $nativeRoot 'stdlib.json') -Raw | ConvertFrom-Json
$sourceName = [System.IO.Path]::GetFileName(([uri]$sourcePin.archiveUrl).AbsolutePath)
$sourceHash = $sourcePin.archiveSha256
New-Item -ItemType Directory -Force -Path $bundleRoot | Out-Null

if (-not $RustSourceArchive) {
    $inputRoot = Join-Path $bundleRoot 'inputs'
    New-Item -ItemType Directory -Force -Path $inputRoot | Out-Null
    $RustSourceArchive = Join-Path $inputRoot $sourceName
    if (-not (Test-Path -LiteralPath $RustSourceArchive -PathType Leaf)) {
        if ($Offline) { throw "Offline Rust publishing requires -RustSourceArchive or cached $RustSourceArchive." }
        $download = Join-Path $inputRoot ([guid]::NewGuid().ToString('N') + '.download')
        try {
            Write-Host "Downloading pinned Rust standard-library sources (build time only)..."
            Invoke-WebRequest $sourcePin.archiveUrl -OutFile $download -TimeoutSec 600
            if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $sourceHash) {
                throw "Rust standard-library source checksum mismatch."
            }
            [System.IO.File]::Move($download, $RustSourceArchive, $true)
        } finally {
            if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
        }
    }
}
$RustSourceArchive = (Resolve-Path -LiteralPath $RustSourceArchive).Path
if ((Get-FileHash -LiteralPath $RustSourceArchive -Algorithm SHA256).Hash -ne $sourceHash) {
    throw "Expected checksum-pinned $sourceName; refusing incompatible Rust sources."
}

$ridRoot = Join-Path $bundleRoot $Runtime
New-Item -ItemType Directory -Force -Path $ridRoot | Out-Null
$staging = Join-Path $ridRoot ('staging-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'payload'
$temporaryZip = Join-Path $staging 'bundle.zip'
$bundlePath = Join-Path $ridRoot 'bundle.zip'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
try {
    & (Join-Path $nativeRoot 'prepare-stdlib.ps1') -ArchivePath $RustSourceArchive -OutputDirectory $payload | Out-Host
    & (Join-Path $nativeRoot 'build.ps1') -Runtime $Runtime -OutputDirectory $payload -Offline:$Offline | Out-Host

    $helperName = if ($Runtime -like 'win-*') { 'cop-rust-frontend.exe' } else { 'cop-rust-frontend' }
    foreach ($required in @($helperName, 'rust-stdlib\manifest.json', 'rust-frontend-licenses\manifest.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $required) -PathType Leaf)) {
            throw "Rust bundle is missing required payload: $required"
        }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'rust-stdlib\library') -PathType Container)) {
        throw "Rust bundle is missing required payload: rust-stdlib\library"
    }
    $files = [System.IO.Directory]::GetFiles($payload, '*', [System.IO.SearchOption]::AllDirectories)
    [System.Array]::Sort($files, [System.StringComparer]::Ordinal)
    $zip = [System.IO.Compression.ZipFile]::Open($temporaryZip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $files) {
            $name = [System.IO.Path]::GetRelativePath($payload, $file).Replace('\', '/')
            $entry = $zip.CreateEntry($name, [System.IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [System.DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [System.TimeSpan]::Zero)
            $mode = if ($name -eq $helperName) { 0x81ED } else { 0x81A4 }
            $entry.ExternalAttributes = $mode -shl 16
            $sourceStream = [System.IO.File]::OpenRead($file)
            try {
                $output = $entry.Open()
                try { $sourceStream.CopyTo($output) } finally { $output.Dispose() }
            } finally {
                $sourceStream.Dispose()
            }
        }
    } finally {
        $zip.Dispose()
    }
    [System.IO.File]::Move($temporaryZip, $bundlePath, $true)
} finally {
    Remove-Item -LiteralPath $staging -Recurse -Force
}

Write-Output ([System.IO.Path]::GetFullPath($bundlePath))
