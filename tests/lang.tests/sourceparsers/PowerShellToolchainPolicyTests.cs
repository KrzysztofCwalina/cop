using Cop.Core;
using Cop.Providers;
using Cop.Providers.PowerShell;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class PowerShellToolchainPolicyTests
{
    private static ToolchainResult Response(string output) =>
        new(ToolchainStatus.Success, "test-host", null, output, "", null);

    [Test]
    public async Task MissingToolchain_UsesFallbackWithoutStartingWorker()
    {
        var parser = new PowerShellAstParser(_ => null, (_, _, cancellationToken) => throw new AssertionException("Unexpected worker request"));
        Assert.That((await parser.ParseAsync("$value = Invoke-Expression 'text'", "sample.ps1", cancellationToken: CancellationToken.None)).Commands.Select(c => c.Name),
            Is.EqualTo(new[] { "Invoke-Expression" }));
    }

    [Test]
    public async Task UnusablePreferredCandidate_TriesAlternateAndProbesOnlyOnce()
    {
        var requests = new List<(string, bool)>();
        var parser = new PowerShellAstParser(name => name, (executable, request, cancellationToken) =>
        {
            var probe = request.Contains("\"Probe\"");
            requests.Add((executable, probe));
            if (executable == "pwsh")
                return ValueTask.FromResult<ToolchainResult>(new(ToolchainStatus.Failed, executable, 1, "", "", "unusable shim"));
            return ValueTask.FromResult<ToolchainResult>(Response(probe ? """{"Ready":true}""" : """{"Commands":[{"Name":"NativeCommand","Text":"NativeCommand","Line":1}],"Errors":[]}"""));
        });
        Assert.That((await parser.ParseAsync("echo fallback", "one.ps1", cancellationToken: CancellationToken.None)).Commands.Single().Name, Is.EqualTo("NativeCommand"));
        Assert.That((await parser.ParseAsync("echo fallback", "two.ps1", cancellationToken: CancellationToken.None)).Commands.Single().Name, Is.EqualTo("NativeCommand"));
        Assert.That(requests, Is.EqualTo(new[]
        {
            ("pwsh", true), ("powershell", true), ("powershell", false), ("powershell", false)
        }));
    }

    [Test]
    public async Task UnusablePreflights_AllowFallbackButNeverSubmitSource()
    {
        var requests = new List<string>();
        var parser = new PowerShellAstParser(name => name, (_, request, cancellationToken) =>
        {
            requests.Add(request);
            return ValueTask.FromResult<ToolchainResult>(Response("invalid handshake"));
        });
        Assert.That((await parser.ParseAsync("Write-Output 'fallback'", "one.ps1", cancellationToken: CancellationToken.None)).Commands.Single().Name, Is.EqualTo("Write-Output"));
        Assert.That((await parser.ParseAsync("Write-Output 'fallback'", "two.ps1", cancellationToken: CancellationToken.None)).Commands.Single().Name, Is.EqualTo("Write-Output"));
        Assert.That(requests, Is.EqualTo(new[] { """{"Probe":true}""", """{"Probe":true}""" }));
    }

    [TestCase(ToolchainStatus.Unavailable)]
    [TestCase(ToolchainStatus.Failed)]
    [TestCase(ToolchainStatus.TimedOut)]
    [TestCase(ToolchainStatus.OutputLimitExceeded)]
    public void EstablishedWorkerFailure_IsFatalRatherThanFallback(ToolchainStatus status)
    {
        int requests = 0;
        var parser = new PowerShellAstParser(name => name, (_, _, cancellationToken) => ValueTask.FromResult<ToolchainResult>(++requests == 1
            ? Response("""{"Ready":true}""")
            : new(status, "test-host", null, "", "", "fixture failure")));
        var error = Assert.ThrowsAsync<InvalidDataException>(async () => await parser.ParseAsync("Write-Output 'not fallback'", "broken.ps1", cancellationToken: CancellationToken.None));
        Assert.That(error!.Message, Is.EqualTo($"broken.ps1: PowerShell AST request {status}: fixture failure"));
        Assert.That(requests, Is.EqualTo(2), "Established helper failure must not try another candidate.");
    }

    [TestCase("invalid JSON")]
    [TestCase("{}")]
    [TestCase("""{"Commands":null,"Errors":[]}""")]
    [TestCase("""{"Commands":[{"Name":"echo","Text":"echo","Line":"bad"}],"Errors":[]}""")]
    public void EstablishedWorkerInvalidProtocol_IsFatalRatherThanFallback(string output)
    {
        int requests = 0;
        var parser = new PowerShellAstParser(name => name, (_, _, cancellationToken) => ValueTask.FromResult<ToolchainResult>(Response(++requests == 1 ? """{"Ready":true}""" : output)));
        var error = Assert.ThrowsAsync<InvalidDataException>(async () => await parser.ParseAsync("echo fallback", "broken.ps1", cancellationToken: CancellationToken.None));
        Assert.That(error!.Message, Does.StartWith("broken.ps1: PowerShell returned invalid AST JSON:"));
        Assert.That(requests, Is.EqualTo(2));
    }

    [Test]
    public async Task EstablishedWorkerFailure_BecomesVisibleProviderErrorThroughSnapshot()
    {
        ProviderErrors.Clear();
        try
        {
            int requests = 0;
            var parser = new PowerShellAstParser(name => name, (_, _, cancellationToken) => ValueTask.FromResult<ToolchainResult>(++requests == 1
                ? Response("""{"Ready":true}""")
                : new(ToolchainStatus.TimedOut, "test-host", null, "", "", "fixture timeout")));
            var snapshot = new DeferredFormatSnapshot(() => ["broken.ps1"], async (path, cancellationToken) =>
            {
                var parsed = (await parser.ParseAsync("echo fallback", path, cancellationToken: CancellationToken.None));
                return new() { ["Commands"] = parsed.Commands.Cast<object>().ToList() };
            });
            var result = snapshot.Query(new ProviderQuery(), new ProviderSchema
            {
                Collections = [new() { Name = "Commands", ItemType = "PowerShellCommand" }]
            }, new RuntimeBindings());
            Assert.That((await result.Collections["Commands"].ToArrayAsync()), Is.Empty);
            Assert.That(ProviderErrors.Drain(), Is.EqualTo(new[]
            {
                "Failed to analyze 'broken.ps1': broken.ps1: PowerShell AST request TimedOut: fixture timeout"
            }));
        }
        finally { ProviderErrors.Clear(); }
    }
}
