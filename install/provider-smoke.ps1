# Exercise the host executable and each shipped source provider before packaging.
param(
    [Parameter(Mandatory = $true)]
    [string]$Executable,
    [switch]$RequireRustBundle
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

function Test-SemanticSmoke([string]$Language, [string]$Source, [string]$Extension, [string]$Member, [string]$ResultType) {
    $target = Join-Path $smokeDir ("semantic-" + $Language)
    New-Item -ItemType Directory -Path $target | Out-Null
    Set-Content -LiteralPath (Join-Path $target ("sample" + $Extension)) -Encoding utf8 -Value $Source
    if ($Language -eq 'rust') {
        Set-Content -LiteralPath (Join-Path $target 'Cargo.toml') -Encoding utf8 -Value @'
[package]
name = "cop-publish-smoke"
version = "0.1.0"
edition = "2024"
[lib]
path = "sample.rs"
'@
    }
    $programDir = Join-Path $smokeDir ("semantic-checks-" + $Language)
    New-Item -ItemType Directory -Path $programDir | Out-Null
    $program = Join-Path $programDir 'main.cop'
    $feed = (Join-Path $RepoRoot 'packages').Replace('\', '/').Replace("'", "\'")
    Set-Content -LiteralPath $program -Encoding utf8 -Value @"
feed '$feed'
import code
import $Language
let cb = $Language.parse()
let calls = cb.Calls.Where(item.MemberName == '$Member')
let resolved = calls.Where(
    item.ReceiverType != nic && item.ValueType != nic && item.ReferencedCallable != nic
    && item.ReceiverType.Name == 'Alpha' && item.ValueType.Name == '$ResultType'
    && item.ReferencedCallable.Name == '$Member'
    && item.ReferencedCallable.ReturnType.Name == '$ResultType'
    && item.ReferencedCallable.SourceFile != nic)
command MAIN = print('PUBLISH-SEMANTIC $Language {calls.Count} {resolved.Count}')
"@
    $verification = Invoke-SmokeProcess @('verify', $program)
    if ($verification.ExitCode -ne 0) {
        throw "$Language semantic smoke failed verification:`n$($verification.Stdout)`n$($verification.Stderr)"
    }
    foreach ($run in 1..2) {
        $result = Invoke-SmokeProcess @($program, '-t', $target, '--no-color')
        if ($result.ExitCode -ne 0 -or $result.Stdout.Trim() -ne "PUBLISH-SEMANTIC $Language 1 1") {
            throw "$Language semantic smoke run $run failed (exit $($result.ExitCode)):`n$($result.Stdout)`n$($result.Stderr)"
        }
        if ($result.Stderr) { Write-Host $result.Stderr.TrimEnd() -ForegroundColor Yellow }
    }
    Write-Host "  Verified $Language receiver, result, selected declaration, and cold/warm semantic facts."
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
    Test-SemanticSmoke 'csharp' 'class Alpha { public int Get(int value) => value; } class Caller { void Run() { var a = new Alpha(); a.Get(1); } }' '.cs' 'Get' 'Int32'
    if ($RequireRustBundle) {
        $portableDir = Join-Path $smokeDir 'portable'
        New-Item -ItemType Directory -Path $portableDir | Out-Null
        $portableExe = Join-Path $portableDir ([System.IO.Path]::GetFileName($Executable))
        Copy-Item -LiteralPath $Executable -Destination $portableExe
        $Executable = $portableExe
        Test-SemanticSmoke 'rust' 'pub struct Alpha; impl Alpha { pub fn get(&self, value: u32) -> u32 { value } } pub fn run() { let a = Alpha; a.get(1); }' '.rs' 'get' 'u32'
    }
}
finally {
    Remove-Item -LiteralPath $smokeDir -Recurse -Force
}
