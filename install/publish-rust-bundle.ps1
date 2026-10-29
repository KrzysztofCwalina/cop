param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string]$Runtime,
    [string]$RustSourceArchive,
    [switch]$Offline
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$bundle = & (Join-Path $PSScriptRoot 'prepare-rust-bundle.ps1') -Runtime $Runtime -RustSourceArchive $RustSourceArchive -Offline:$Offline
$output = Join-Path $PSScriptRoot "cop-rust-$Runtime.zip"
$temporary = Join-Path $PSScriptRoot ("cop-rust-$Runtime-" + [guid]::NewGuid().ToString('N') + '.tmp')
try {
    $archive = [System.IO.Compression.ZipFile]::Open($temporary, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $bundle, 'Cop.RustFrontend.Bundle.zip', [System.IO.Compression.CompressionLevel]::NoCompression) | Out-Null
    } finally { $archive.Dispose() }
    [System.IO.File]::Move($temporary, $output, $true)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
}
Write-Output $output
