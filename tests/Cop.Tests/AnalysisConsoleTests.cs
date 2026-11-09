using System.Net;
using System.Text;
using System.Text.Json;
using Cop.Cli.Commands;
using Cop.Core;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public class AnalysisConsoleTests
{
    [TestCase(false, false, true)]
    [TestCase(false, true, false)]
    [TestCase(true, false, false)]
    [TestCase(true, true, false)]
    public void StatusColorsFollowStderrNotStdout(bool redirected, bool noColor, bool expected) =>
        Assert.That(AnalysisConsole.UseColor(redirected, noColor), Is.EqualTo(expected));

    [TestCase("Packages")]
    [TestCase("Restored")]
    [TestCase("Analyze")]
    [TestCase("Summary")]
    public void PhaseLabelsAlignWithAndWithoutColor(string phase)
    {
        var color = phase == "Restored" ? ConsoleMarkdown.Green : ConsoleMarkdown.Cyan;
        var plain = AnalysisConsole.Format(phase, "message", false);
        var colored = AnalysisConsole.Format(phase, "message", true);
        Assert.That(plain, Is.EqualTo($"  {phase.PadRight(8)}  message"));
        Assert.That(colored, Is.EqualTo($"  {color}{phase.PadRight(8)}\x1b[0m  message"));
        Assert.That(colored.StartsWith("  " + color, StringComparison.Ordinal), Is.True);
        Assert.That(colored.Contains('\x1b'), Is.True);
        Assert.That(TestTextParsing.StripAnsi(colored), Is.EqualTo(plain));
        Assert.That(plain.IndexOf("message", StringComparison.Ordinal), Is.EqualTo(12));
    }

    [TestCase(false, false)]
    [TestCase(true, false)]
    [TestCase(true, true)]
    public async Task BothRestorePathsReportTheSameExactOutcomes(bool scriptRun, bool incompatible)
    {
        await WithRestoreFixture(async (root, source, output) =>
        {
            var cache = Path.Combine(root, "cache");
            if (scriptRun)
            {
                var scripts = Path.Combine(root, "scripts");
                Directory.CreateDirectory(scripts);
                File.WriteAllText(Path.Combine(scripts, "main.cop"), "import fixture\ncommand MAIN = print('ready')");
                var feeds = new List<string>();
                if (incompatible)
                {
                    ReleasePackageCompatibilityTests.WriteProvider(root, "stale", new Version(2000, 1, 1, 1));
                    feeds.Add(Path.Combine(root, "stale"));
                }
                Assert.That(await RunCommand.AutoRestoreImportsAsync(
                    scripts, null, cache, feeds, ["github.com/tests/feed"], source), Is.True);
            }
            else
            {
                Assert.That(await RunCommand.AutoRestorePackagesAsync(
                    ["fixture"], cache, ["github.com/tests/feed"], source), Is.True);
            }

            Assert.That(output.ToString().Replace("\r\n", "\n"), Is.EqualTo(
                $"  Packages  Restoring packages from 1 feed for Cop {ProviderCompatibility.HostVersion}\n" +
                "  Restored  fixture (github.com/tests/feed)\n"));
            Assert.That(File.ReadAllText(Path.Combine(cache, "fixture", "cop.json")),
                Is.EqualTo(RestoreHandler.Manifest));
            if (incompatible)
                Assert.That(File.Exists(Path.Combine(root, "stale", "fixture", "lib", "fixture.dll")), Is.True);
        });
    }

    [TestCase(false, "not found")]
    [TestCase(true, "not found")]
    [TestCase(false, "connection interrupted")]
    [TestCase(true, "connection interrupted")]
    [TestCase(false, "no files")]
    [TestCase(true, "no files")]
    public async Task RestoreFailuresKeepPackageFeedAndReason(bool scriptRun, string failure)
    {
        await WithRestoreFixture(async (root, source, output) =>
        {
            var cache = Path.Combine(root, "cache");
            if (scriptRun)
            {
                var scripts = Path.Combine(root, "scripts");
                Directory.CreateDirectory(scripts);
                File.WriteAllText(Path.Combine(scripts, "main.cop"), "import fixture\ncommand MAIN = print('ready')");
                Assert.That(await RunCommand.AutoRestoreImportsAsync(
                    scripts, null, cache, [], ["github.com/tests/feed"], source), Is.False);
            }
            else
            {
                Assert.That(await RunCommand.AutoRestorePackagesAsync(
                    ["fixture"], cache, ["github.com/tests/feed"], source), Is.False);
            }
            var level = scriptRun ? "Warning" : "Error";
            Assert.That(output.ToString().Replace("\r\n", "\n"), Is.EqualTo(
                $"  Packages  Restoring packages from 1 feed for Cop {ProviderCompatibility.HostVersion}\n" +
                $"Warning: Cannot restore 'fixture' from github.com/tests/feed: {failure}\n" +
                $"{level}: Package 'fixture' not found in any configured feed.\n"));
            Assert.That(Directory.Exists(Path.Combine(cache, "fixture")), Is.False);
        }, failure);
    }

    [Test]
    public async Task RestoreHeaderPluralizesAndDoesNotInventACompletionSummary()
    {
        await WithRestoreFixture(async (root, source, output) =>
        {
            Assert.That(await RunCommand.AutoRestorePackagesAsync(
                ["fixture", "other"], Path.Combine(root, "cache"),
                ["github.com/tests/feed", "github.com/tests/fallback"], source), Is.True);
            Assert.That(output.ToString().Replace("\r\n", "\n"), Is.EqualTo(
                $"  Packages  Restoring packages from 2 feeds for Cop {ProviderCompatibility.HostVersion}\n" +
                "  Restored  fixture (github.com/tests/feed)\n" +
                "  Restored  other (github.com/tests/feed)\n"));
        });
    }

    private static async Task WithRestoreFixture(
        Func<string, GitHubPackageSource, StringWriter, Task> test, string? failure = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-restore-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var previousError = Console.Error;
        var previousNoColor = ConsoleMarkdown.NoColor;
        using var output = new StringWriter();
        using var http = new HttpClient(new RestoreHandler(failure));
        try
        {
            Console.SetError(output);
            ConsoleMarkdown.NoColor = true;
            await test(root, new GitHubPackageSource(http), output);
        }
        finally
        {
            Console.SetError(previousError);
            ConsoleMarkdown.NoColor = previousNoColor;
            Directory.Delete(root, true);
        }
    }

    private sealed class RestoreHandler(string? failure) : HttpMessageHandler
    {
        internal const string Manifest =
            """{"name":"fixture","version":"1.0.0","title":"Fixture","description":"Fixture","authors":"tests"}""";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (failure == "connection interrupted")
                throw new HttpRequestException(failure);
            if (failure == "not found")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(Manifest));
            var json = request.RequestUri!.AbsolutePath.EndsWith("/cop.json", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { content, encoding = "base64" })
                : failure == "no files" ? "[]" :
                    JsonSerializer.Serialize(new[] { new { name = "cop.json", type = "file", content, encoding = "base64" } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }
}
