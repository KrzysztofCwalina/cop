# Publish cop as a self-contained single-file executable
# Builds for all supported platforms into install/<rid>/ subfolders
# Creates zip archives with Unix executable permissions for Linux/macOS
param(
    [string[]]$Runtimes = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RepoRoot = "$PSScriptRoot\.."
$OutputBase = $PSScriptRoot

foreach ($rid in $Runtimes) {
    $outDir = Join-Path $OutputBase $rid
    if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null

    Write-Host "Publishing cop for $rid..."
    dotnet publish "$RepoRoot\cop\cli\cop.csproj" -c Release -r $rid --self-contained -p:PublishReadyToRun=false -o $outDir
    
    # Clean up build artifacts
    Remove-Item -Force "$outDir\*.pdb" -ErrorAction SilentlyContinue
    Remove-Item -Force "$outDir\*.json" -ErrorAction SilentlyContinue
    Remove-Item -Force "$outDir\web.config" -ErrorAction SilentlyContinue

    Write-Host "  -> $outDir"

    # Create zip archive
    $zipName = "cop-$rid.zip"
    $zipPath = Join-Path $OutputBase $zipName
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

    $isUnix = $rid -like "linux-*" -or $rid -like "osx-*"

    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $outDir -File) {
            $entry = $zip.CreateEntry($file.Name, [System.IO.Compression.CompressionLevel]::Optimal)
            if ($isUnix) {
                # Set Unix permissions: rwxr-xr-x (0755) + regular file type
                $entry.ExternalAttributes = ([int]0x81ED) -shl 16
            }
            $stream = $entry.Open()
            try {
                $fileBytes = [System.IO.File]::ReadAllBytes($file.FullName)
                $stream.Write($fileBytes, 0, $fileBytes.Length)
            } finally {
                $stream.Dispose()
            }
        }
    } finally {
        $zip.Dispose()
    }
    Write-Host "  -> $zipPath"
}

Write-Host "`nDone! Published for: $($Runtimes -join ', ')"
Write-Host "To publish for a single platform: .\publish.ps1 -Runtimes win-x64"
