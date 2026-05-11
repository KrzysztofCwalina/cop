# Verify that Unix zip archives have correct host OS and permission bits.
# Run this after publish.ps1 to confirm archives will extract as executable on Linux/macOS.
# Usage: .\verify-archives.ps1 [-Path <directory>]
param(
    [string]$Path = $PSScriptRoot
)

$failed = 0
$passed = 0

foreach ($zip in Get-ChildItem $Path -Filter "cop-*.zip") {
    $rid = $zip.BaseName -replace '^cop-', ''
    $isUnix = $rid -like "linux-*" -or $rid -like "osx-*"

    if (-not $isUnix) {
        Write-Host "  SKIP $($zip.Name) (Windows target)" -ForegroundColor DarkGray
        continue
    }

    Write-Host "  Checking $($zip.Name)..." -NoNewline
    try {
        $bytes = [System.IO.File]::ReadAllBytes($zip.FullName)
        $len = $bytes.Length

        # Find EOCD
        $eocdOffset = -1
        for ($i = $len - 22; $i -ge [Math]::Max(0, $len - 65557); $i--) {
            if ($bytes[$i] -eq 0x50 -and $bytes[$i+1] -eq 0x4B -and $bytes[$i+2] -eq 0x05 -and $bytes[$i+3] -eq 0x06) {
                $eocdOffset = $i
                break
            }
        }
        if ($eocdOffset -lt 0) { throw "EOCD signature not found" }

        $cdOffset = [BitConverter]::ToUInt32($bytes, $eocdOffset + 16)
        $cdSize = [BitConverter]::ToUInt32($bytes, $eocdOffset + 12)
        $entryCount = [BitConverter]::ToUInt16($bytes, $eocdOffset + 10)

        if ($entryCount -eq 0) { throw "Archive has 0 entries" }

        $pos = [int]$cdOffset
        $cdEnd = [int]($cdOffset + $cdSize)
        $errors = @()
        $checked = 0

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
                $errors += "  '$fileName': version-made-by host OS = $hostOS (must be 3/Unix)"
            }
            if (($unixMode -band 0x0049) -eq 0) {
                $errors += "  '$fileName': mode 0o$([Convert]::ToString($unixMode, 8).PadLeft(6, '0')) has no execute bits"
            }

            $extraLen = [BitConverter]::ToUInt16($bytes, $pos + 30)
            $commentLen = [BitConverter]::ToUInt16($bytes, $pos + 32)
            $pos += 46 + $fnLen + $extraLen + $commentLen
            $checked++
        }

        if ($checked -ne $entryCount) {
            $errors += "  Parsed $checked entries but EOCD declares $entryCount"
        }

        if ($errors.Count -gt 0) {
            Write-Host " FAIL" -ForegroundColor Red
            $errors | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
            $failed++
        } else {
            Write-Host " OK ($checked entries, all executable)" -ForegroundColor Green
            $passed++
        }
    } catch {
        Write-Host " ERROR: $_" -ForegroundColor Red
        $failed++
    }
}

Write-Host ""
if ($failed -gt 0) {
    Write-Host "FAILED: $failed archive(s) have incorrect Unix permissions." -ForegroundColor Red
    Write-Host "Run install/publish.ps1 to regenerate archives with correct permissions." -ForegroundColor Yellow
    exit 1
} elseif ($passed -eq 0) {
    Write-Host "WARNING: No Unix archives found to verify in $Path" -ForegroundColor Yellow
    exit 1
} else {
    Write-Host "All $passed Unix archive(s) verified successfully." -ForegroundColor Green
    exit 0
}
