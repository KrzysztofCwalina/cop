# Publish cop as a self-contained single-file executable
# Builds for all supported platforms into install/<rid>/ subfolders
param(
    [string[]]$Runtimes = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
)

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
}

Write-Host "`nDone! Published for: $($Runtimes -join ', ')"
Write-Host "To publish for a single platform: .\publish.ps1 -Runtimes win-x64"
