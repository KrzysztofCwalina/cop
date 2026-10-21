using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class KnownBugLedgerWorkflowTests
{
    [TestCase("failed", 1, true)]
    [TestCase("passed", 0, true)]
    [TestCase("missing", 1, false)]
    [TestCase("malformed", 1, false)]
    [TestCase("empty", 0, false)]
    [TestCase("incomplete", 1, false)]
    [TestCase("runner-error", 2, false)]
    [TestCase("run-error", 1, false)]
    [TestCase("inconsistent-exit", 0, false)]
    public void LedgerOnlyAcceptsCompletedTestOutcomes(string scenario, int runnerExit, bool success)
    {
        var repo = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "cop.sln")))
            repo = repo.Parent;
        Assert.That(repo, Is.Not.Null);
        var lines = File.ReadAllLines(Path.Combine(repo!.FullName, ".github", "workflows", "ci.yml"));
        var step = Array.FindIndex(lines, line => line == "      - name: Report PendingFix specs");
        Assert.That(step, Is.Not.EqualTo(-1));
        var start = Array.FindIndex(lines, step, line => line == "        run: |") + 1;
        var script = string.Join("\n", lines.Skip(start)
            .TakeWhile(line => line.StartsWith("          ", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(line))
            .Select(line => line.Length >= 10 ? line[10..] : ""));
        var root = Path.Combine(Path.GetTempPath(), $"cop-ledger-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var passed = scenario == "passed";
            var total = scenario == "empty" ? 0 : 1;
            var executed = scenario == "incomplete" ? 0 : total;
            var fixture = Path.Combine(root, "fixture.trx");
            if (scenario != "missing")
                File.WriteAllText(fixture, scenario == "malformed" ? "<broken" : $"""
                    <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
                      <Results>
                        {(total == 0 ? "" : $"<UnitTestResult testName=\"KnownBug\" outcome=\"{(passed ? "Passed" : "Failed")}\" />")}
                      </Results>
                      <ResultSummary>
                        <Counters total="{total}" executed="{executed}" passed="{(passed ? total : 0)}" failed="{(passed ? 0 : total)}" error="0" timeout="0" aborted="0" />
                        {(scenario == "run-error" ? "<RunInfos><RunInfo outcome=\"Error\"><Text>Adapter crashed</Text></RunInfo></RunInfos>" : "")}
                      </ResultSummary>
                    </TestRun>
                    """);
            var mock = """
                $ErrorActionPreference = 'Stop'
                function dotnet {
                  $index = [array]::IndexOf($args, '--results-directory')
                  $dir = $args[$index + 1]
                  New-Item -ItemType Directory -Path $dir | Out-Null
                  if (Test-Path -LiteralPath $env:LEDGER_FIXTURE) {
                    Copy-Item -LiteralPath $env:LEDGER_FIXTURE -Destination (Join-Path $dir 'pending-fix.trx')
                  }
                  $global:LASTEXITCODE = [int]$env:LEDGER_EXIT
                }

                """;
            var psi = new ProcessStartInfo("pwsh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add(mock + script);
            psi.Environment["RUNNER_TEMP"] = root;
            psi.Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(root, "summary.md");
            psi.Environment["LEDGER_FIXTURE"] = fixture;
            psi.Environment["LEDGER_EXIT"] = runnerExit.ToString();
            using var process = Process.Start(psi)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                Assert.Fail("Known-bug workflow script timed out.");
            }
            Assert.That(process.ExitCode, Is.EqualTo(success ? 0 : 1),
                stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
            if (success)
            {
                var summary = File.ReadAllText(psi.Environment["GITHUB_STEP_SUMMARY"]!);
                Assert.That(summary, Does.Contain(passed
                    ? "1 tests completed: 0 still failing, 1 passing."
                    : "1 tests completed: 1 still failing, 0 passing."));
                Assert.That(summary, Does.Contain(passed ? "Now passing: KnownBug" : "Still failing: KnownBug"));
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
