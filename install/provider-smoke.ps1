# Exercise the host executable and each shipped source provider before packaging.
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable
)

$ErrorActionPreference = 'Stop'
$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$Executable = [System.IO.Path]::GetFullPath($Executable)
$smokeDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cop-provider-smoke-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $smokeDir | Out-Null
$fixtures = @(
    @{ Language = 'csharp'; Extension = '.cs'; Source = 'class Alpha { void Run(string value) { ping(value); } }'; Malformed = 'class Alpha {' },
    @{ Language = 'python'; Extension = '.py'; Source = "class Alpha:`n    def Run(self, value):`n        ping(value)`n"; Malformed = "class Alpha:`n    def Run(" },
    @{ Language = 'javascript'; Extension = '.js'; Source = 'class Alpha { Run(value) { ping(value); } }'; Malformed = 'class Alpha { Run(' },
    @{ Language = 'go'; Extension = '.go'; Source = "package fixture`ntype Alpha struct {}`nfunc (a Alpha) Run(value string) { ping(value) }"; Malformed = "package fixture`nfunc Run(" },
    @{ Language = 'java'; Extension = '.java'; Source = 'class Alpha { void Run(String value) { ping(value); } }'; Malformed = 'class Alpha { void Run(' },
    @{ Language = 'rust'; Extension = '.rs'; Source = 'pub struct Alpha; impl Alpha { fn Run(&self, value: &str) { ping(value); } }'; Malformed = 'pub struct Alpha {' },
    @{ Language = 'cop'; Extension = '.cop'; Source = "type Alpha = { Value : string }`nfunction Run(value : string) => ping(value)"; Malformed = 'type Alpha = { Value :' }
)

function Invoke-SmokeProcess([string[]]$Arguments) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($Executable)
    $start.WorkingDirectory = $RepoRoot
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Cannot start smoke executable: $Executable" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            if (-not $process.WaitForExit(5000)) { throw "Smoke process did not exit after termination." }
            throw "Smoke command exceeded 60 seconds: $($Arguments -join ' ')"
        }
        if (-not [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) {
            throw "Smoke process exited but its output pipes did not close."
        }
        return @{ ExitCode = $process.ExitCode; Stdout = $stdout.Result; Stderr = $stderr.Result }
    }
    finally { $process.Dispose() }
}

try {
    foreach ($fixture in $fixtures) {
        $language = $fixture.Language
        $target = Join-Path $smokeDir $language
        New-Item -ItemType Directory -Path $target | Out-Null
        $source = Join-Path $target ("sample" + $fixture.Extension)
        Set-Content -LiteralPath $source -Encoding utf8 -Value $fixture.Source
        $programDir = Join-Path $smokeDir ("checks-" + $language)
        New-Item -ItemType Directory -Path $programDir | Out-Null
        $program = Join-Path $programDir 'main.cop'
        $feed = (Join-Path $RepoRoot 'packages').Replace('\', '/').Replace("'", "\'")
        Set-Content -LiteralPath $program -Encoding utf8 -Value @"
feed '$feed'
import code
import $language
let cb = $language.parse()
let alpha = cb.Types.Where(item.Name == 'Alpha')
let runs = cb.Methods.Where(item.Name == 'Run')
let calls = cb.Calls.Where(item.MemberName == 'ping')
command MAIN = print('PUBLISH-SMOKE $language {alpha.Count} {runs.Count} {calls.Count}')
"@
        $verification = Invoke-SmokeProcess @('verify', $program)
        if ($verification.ExitCode -ne 0) {
            throw "$language smoke failed verification:`n$($verification.Stdout)`n$($verification.Stderr)"
        }
        foreach ($run in 1..2) {
            $result = Invoke-SmokeProcess @($program, '-t', $target, '--no-color')
            if ($result.ExitCode -ne 0 -or $result.Stdout.Trim() -ne "PUBLISH-SMOKE $language 1 1 1") {
                throw "$language cold/warm smoke run $run failed (exit $($result.ExitCode)):`n$($result.Stdout)`n$($result.Stderr)"
            }
            if ($result.Stderr) { Write-Host $result.Stderr.TrimEnd() -ForegroundColor Yellow }
        }
        Set-Content -LiteralPath $source -Encoding utf8 -Value $fixture.Malformed
        $result = Invoke-SmokeProcess @($program, '-t', $target, '--no-color')
        if ($result.ExitCode -eq 0 -or -not $result.Stderr.Contains("sample" + $fixture.Extension)) {
            throw "$language malformed-source smoke was silent/successful (exit $($result.ExitCode)):`n$($result.Stdout)`n$($result.Stderr)"
        }
        Write-Host "  Verified $language declarations, methods, calls, cold/warm results, and visible malformed input."
    }
}
finally {
    Remove-Item -LiteralPath $smokeDir -Recurse -Force
}
