# Publish cop as a self-contained single-file executable
# Builds for all supported platforms into install/<rid>/ subfolders
# Creates zip archives with Unix executable permissions for Linux/macOS
param(
    [string[]]$Runtimes = @("win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"),
    [switch]$SkipBuild
)

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$RepoRoot = "$PSScriptRoot\.."
$OutputBase = $PSScriptRoot

# Patches the ZIP central directory "version made by" host OS byte to Unix (3)
# for all entries in the archive. .NET's ZipArchive always writes host OS = 0 (MS-DOS),
# which causes unzip/mise to ignore ExternalAttributes Unix permission bits.
function Set-ZipUnixHostOS([string]$ZipPath) {
    $bytes = [System.IO.File]::ReadAllBytes($ZipPath)
    $len = $bytes.Length

    # Find End of Central Directory Record (EOCD) — search backward for signature 0x06054b50
    $eocdOffset = -1
    for ($i = $len - 22; $i -ge [Math]::Max(0, $len - 65557); $i--) {
        if ($bytes[$i] -eq 0x50 -and $bytes[$i+1] -eq 0x4B -and $bytes[$i+2] -eq 0x05 -and $bytes[$i+3] -eq 0x06) {
            $eocdOffset = $i
            break
        }
    }
    if ($eocdOffset -lt 0) { throw "EOCD signature not found in $ZipPath" }

    # Reject multi-disk and Zip64 archives (not expected for our small CLI binaries)
    $diskNumber = [BitConverter]::ToUInt16($bytes, $eocdOffset + 4)
    $cdDisk = [BitConverter]::ToUInt16($bytes, $eocdOffset + 6)
    if ($diskNumber -ne 0 -or $cdDisk -ne 0) { throw "Multi-disk ZIP not supported: $ZipPath" }

    $entryCount = [BitConverter]::ToUInt16($bytes, $eocdOffset + 10)
    $cdSize = [BitConverter]::ToUInt32($bytes, $eocdOffset + 12)
    $cdOffset = [BitConverter]::ToUInt32($bytes, $eocdOffset + 16)

    if ($cdOffset -eq 0xFFFFFFFF -or $cdSize -eq 0xFFFFFFFF) {
        throw "Zip64 not supported: $ZipPath"
    }
    if ($cdOffset + $cdSize -gt $eocdOffset) {
        throw "Central directory bounds exceed EOCD offset in $ZipPath"
    }

    # Walk central directory entries and patch host OS byte
    $pos = [int]$cdOffset
    $cdEnd = [int]($cdOffset + $cdSize)
    $patched = 0
    while ($pos -lt $cdEnd) {
        # Verify central directory entry signature: 0x02014b50
        if ($bytes[$pos] -ne 0x50 -or $bytes[$pos+1] -ne 0x4B -or $bytes[$pos+2] -ne 0x01 -or $bytes[$pos+3] -ne 0x02) {
            throw "Invalid central directory entry signature at offset $pos in $ZipPath"
        }
        # Fixed header is 46 bytes; check bounds
        if ($pos + 46 -gt $cdEnd) { throw "Truncated central directory entry at offset $pos in $ZipPath" }

        # "Version made by" is at offset 4-5 from entry start. High byte = host OS.
        # Set high byte to 3 (Unix) so ExternalAttributes Unix permissions are honored.
        $bytes[$pos + 5] = 3

        # Calculate entry length to advance: 46 + filename + extra + comment
        $fnLen = [BitConverter]::ToUInt16($bytes, $pos + 28)
        $extraLen = [BitConverter]::ToUInt16($bytes, $pos + 30)
        $commentLen = [BitConverter]::ToUInt16($bytes, $pos + 32)
        $entryLen = 46 + $fnLen + $extraLen + $commentLen

        if ($pos + $entryLen -gt $cdEnd) {
            throw "Central directory entry at offset $pos overflows directory bounds in $ZipPath"
        }

        $pos += $entryLen
        $patched++
    }

    if ($patched -ne $entryCount) {
        throw "Patched $patched entries but EOCD declares $entryCount in $ZipPath"
    }

    [System.IO.File]::WriteAllBytes($ZipPath, $bytes)
    return $patched
}

# Verifies a Unix zip archive has correct host OS and permission bits.
# Returns the number of verified entries. Throws on failure.
function Test-ZipUnixPermissions([string]$ZipPath) {
    $bytes = [System.IO.File]::ReadAllBytes($ZipPath)
    $len = $bytes.Length

    # Find EOCD
    $eocdOffset = -1
    for ($i = $len - 22; $i -ge [Math]::Max(0, $len - 65557); $i--) {
        if ($bytes[$i] -eq 0x50 -and $bytes[$i+1] -eq 0x4B -and $bytes[$i+2] -eq 0x05 -and $bytes[$i+3] -eq 0x06) {
            $eocdOffset = $i
            break
        }
    }
    if ($eocdOffset -lt 0) { throw "EOCD not found in $ZipPath" }

    $cdOffset = [BitConverter]::ToUInt32($bytes, $eocdOffset + 16)
    $cdSize = [BitConverter]::ToUInt32($bytes, $eocdOffset + 12)

    $pos = [int]$cdOffset
    $cdEnd = [int]($cdOffset + $cdSize)
    $errors = @()
    $entryIndex = 0

    while ($pos -lt $cdEnd) {
        if ($bytes[$pos] -ne 0x50 -or $bytes[$pos+1] -ne 0x4B -or $bytes[$pos+2] -ne 0x01 -or $bytes[$pos+3] -ne 0x02) {
            throw "Invalid central directory entry at offset $pos"
        }

        $hostOS = $bytes[$pos + 5]
        $extAttrs = [BitConverter]::ToUInt32($bytes, $pos + 38)
        $unixMode = ($extAttrs -shr 16) -band 0xFFFF
        $fnLen = [BitConverter]::ToUInt16($bytes, $pos + 28)
        $fileName = [System.Text.Encoding]::UTF8.GetString($bytes, $pos + 46, $fnLen)

        if ($hostOS -ne 3) {
            $errors += "Entry '$fileName': host OS is $hostOS (expected 3/Unix)"
        }
        if (($unixMode -band 0x0049) -eq 0) {
            # No execute bits at all (owner/group/other)
            $errors += "Entry '$fileName': Unix mode 0x$($unixMode.ToString('X4')) has no execute bits"
        }

        $extraLen = [BitConverter]::ToUInt16($bytes, $pos + 30)
        $commentLen = [BitConverter]::ToUInt16($bytes, $pos + 32)
        $pos += 46 + $fnLen + $extraLen + $commentLen
        $entryIndex++
    }

    if ($errors.Count -gt 0) {
        throw "Archive verification failed for ${ZipPath}:`n  $($errors -join "`n  ")"
    }
    return $entryIndex
}

# Build external provider DLLs (and copy to their packages/*/lib/ folders)
# This must happen before publishing cop.exe so the release includes compatible providers.
if (-not $SkipBuild) {
    Write-Host "Building csharp-provider..."
    dotnet build "$RepoRoot\providers\csharp-provider\csharp-provider.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "csharp-provider build failed" }

    Write-Host "Building python-provider..."
    dotnet build "$RepoRoot\providers\python-provider\python-provider.csproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw "python-provider build failed" }
}

foreach ($rid in $Runtimes) {
    $outDir = Join-Path $OutputBase $rid
    if (-not $SkipBuild) {
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

    # For Unix targets, patch the ZIP central directory to mark entries as created on Unix.
    # Without this, unzip/mise ignore ExternalAttributes permission bits and extract as 0644.
    if ($isUnix) {
        $count = Set-ZipUnixHostOS $zipPath
        Write-Host "  Patched $count entries with Unix host OS in $zipName"

        # Self-check: verify the archive is correct immediately after patching
        $verified = Test-ZipUnixPermissions $zipPath
        Write-Host "  Verified $verified entries have Unix permissions in $zipName"
    }

    Write-Host "  -> $zipPath"
}

# Package VS Code extension as a zip for release
$vscodeDir = Join-Path $RepoRoot "install\vscode-cop"
$vscodeZip = Join-Path $OutputBase "cop-vscode.zip"
if (Test-Path $vscodeZip) { Remove-Item -Force $vscodeZip }
[System.IO.Compression.ZipFile]::CreateFromDirectory($vscodeDir, $vscodeZip, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Host "  -> $vscodeZip (VS Code extension)"

Write-Host "`nDone! Published for: $($Runtimes -join ', ')"
Write-Host "To publish for a single platform: .\publish.ps1 -Runtimes win-x64"

# Update PATH-installed cop.exe if it exists in ~/.dotnet/tools
if ($Runtimes -contains "win-x64") {
    $dotnetToolPath = Join-Path $env:USERPROFILE ".dotnet\tools\cop.exe"
    if (Test-Path $dotnetToolPath) {
        $localBinary = Join-Path $OutputBase "win-x64\cop.exe"
        Copy-Item $localBinary $dotnetToolPath -Force
        Write-Host "  -> Updated $dotnetToolPath"
    }
}
