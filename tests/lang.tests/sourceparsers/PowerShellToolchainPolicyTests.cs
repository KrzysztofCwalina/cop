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
    public void MissingToolchain_UsesFallbackWithoutStartingWorker()
    {
        var parser = new PowerShellAstParser(_ => null, (_, _) => throw new AssertionException("Unexpected worker request"));
        Assert.That(parser.Parse("$value = Invoke-Expression 'text'", "sample.ps1").Commands.Select(c => c.Name),
            Is.EqualTo(new[] { "Invoke-Expression" }));
    }

    [Test]
    public void UnusablePreferredCandidate_TriesAlternateAndProbesOnlyOnce()
    {
        var requests = new List<(string, bool)>();
        var parser = new PowerShellAstParser(name => name, (executable, request) =>
        {
            var probe = request.Contains("\"Probe\"");
            requests.Add((executable, probe));
            if (executable == "pwsh")
                return new(ToolchainStatus.Failed, executable, 1, "", "", "unusable shim");
            return Response(probe ? """{"Ready":true}""" : """{"Commands":[{"Name":"NativeCommand","Text":"NativeCommand","Line":1}],"Errors":[]}""");
        });
        Assert.That(parser.Parse("echo fallback", "one.ps1").Commands.Single().Name, Is.EqualTo("NativeCommand"));
        Assert.That(parser.Parse("echo fallback", "two.ps1").Commands.Single().Name, Is.EqualTo("NativeCommand"));
        Assert.That(requests, Is.EqualTo(new[]
        {
            ("pwsh", true), ("powershell", true), ("powershell", false), ("powershell", false)
        }));
    }

    [Test]
    public void UnusablePreflights_AllowFallbackButNeverSubmitSource()
    {
        var requests = new List<string>();
        var parser = new PowerShellAstParser(name => name, (_, request) =>
        {
            requests.Add(request);
            return Response("invalid handshake");
        });
        Assert.That(parser.Parse("Write-Output 'fallback'", "one.ps1").Commands.Single().Name, Is.EqualTo("Write-Output"));
        Assert.That(parser.Parse("Write-Output 'fallback'", "two.ps1").Commands.Single().Name, Is.EqualTo("Write-Output"));
        Assert.That(requests, Is.EqualTo(new[] { """{"Probe":true}""", """{"Probe":true}""" }));
    }

    [TestCase(ToolchainStatus.Unavailable)]
    [TestCase(ToolchainStatus.Failed)]
    [TestCase(ToolchainStatus.TimedOut)]
    [TestCase(ToolchainStatus.OutputLimitExceeded)]
    public void EstablishedWorkerFailure_IsFatalRatherThanFallback(ToolchainStatus status)
    {
        int requests = 0;
        var parser = new PowerShellAstParser(name => name, (_, _) => ++requests == 1
            ? Response("""{"Ready":true}""")
            : new(status, "test-host", null, "", "", "fixture failure"));
        var error = Assert.Throws<InvalidDataException>(() => parser.Parse("Write-Output 'not fallback'", "broken.ps1"));
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
        var parser = new PowerShellAstParser(name => name, (_, _) => Response(++requests == 1 ? """{"Ready":true}""" : output));
        var error = Assert.Throws<InvalidDataException>(() => parser.Parse("echo fallback", "broken.ps1"));
        Assert.That(error!.Message, Does.StartWith("broken.ps1: PowerShell returned invalid AST JSON:"));
        Assert.That(requests, Is.EqualTo(2));
    }

    [Test]
    public void EstablishedWorkerFailure_BecomesVisibleProviderErrorThroughSnapshot()
    {
        ProviderErrors.Clear();
        try
        {
            int requests = 0;
            var parser = new PowerShellAstParser(name => name, (_, _) => ++requests == 1
                ? Response("""{"Ready":true}""")
                : new(ToolchainStatus.TimedOut, "test-host", null, "", "", "fixture timeout"));
            var snapshot = new DeferredFormatSnapshot(() => ["broken.ps1"], path =>
            {
                var parsed = parser.Parse("echo fallback", path);
                return new() { ["Commands"] = parsed.Commands.Cast<object>().ToList() };
            });
            var result = snapshot.Query(new ProviderQuery(), new ProviderSchema
            {
                Collections = [new() { Name = "Commands", ItemType = "PowerShellCommand" }]
            }, new RuntimeBindings());
            Assert.That(result.Collections["Commands"].ToArray(), Is.Empty);
            Assert.That(ProviderErrors.Drain(), Is.EqualTo(new[]
            {
                "Failed to analyze 'broken.ps1': broken.ps1: PowerShell AST request TimedOut: fixture timeout"
            }));
        }
        finally { ProviderErrors.Clear(); }
    }
}
