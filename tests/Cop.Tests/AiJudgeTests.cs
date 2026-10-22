using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Lang;
using Cop.Providers;
using Cop.Providers.Ai;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public class AiJudgeTests
{
    private string _root = "";
    private string _config = "";
    private string? _previousConfig;

    private static string RepoRoot
    {
        get
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
                dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find cop.sln.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(RepoRoot, "tests", "behavior", "ai-judge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _config = Path.Combine(_root, "ai.json");
        _previousConfig = Environment.GetEnvironmentVariable("COP_AI_CONFIG");
        Environment.SetEnvironmentVariable("COP_AI_CONFIG", _config);
    }

    [TearDown]
    public async Task TearDown()
    {
        Environment.SetEnvironmentVariable("COP_AI_CONFIG", _previousConfig);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(_root, recursive: true);
                break;
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(100 * (attempt + 1));
            }
        }
    }

    [Test]
    public async Task Judge_SendsSourceAndArchitecturalFactsWithoutFollowingCycles()
    {
        using var model = ConfigureModel(
            """[{"file":"Example.cs","line":2,"message":"Business logic belongs outside the core."}]""");
        var file = Object("File", ("Path", "Example.cs"), ("Namespace", "Core"),
            ("Language", "csharp"), ("Usings", new[] { "System" }));
        var type = Object("Type", ("Name", "Example"), ("Kind", "Class"), ("File", file),
            ("Line", 1), ("BaseTypes", new[] { "BaseService" }), ("Interfaces", new[] { "IService" }));
        file.Set("Types", new[] { type });
        var codebase = Codebase(file,
            Object("Line", ("File", file), ("Number", 2), ("Text", "    public int Total() => 0;")),
            Object("Line", ("File", file), ("Number", 1), ("Text", "class Example : BaseService, IService")));
        codebase.Set("Types", new[] { type });
        codebase.Set("Methods", new[]
        {
            Object("Method", ("Name", "Total"), ("File", file), ("Line", 2),
                ("ReturnType", Object("TypeReference", ("Name", "int"))))
        });
        codebase.Set("Calls", new[]
        {
            Object("Statement", ("File", file), ("Line", 2), ("TypeName", "Orders"), ("MemberName", "Submit"))
        });
        codebase.Set("Projects", new[]
        {
            Object("Project", ("Name", "Core"), ("Path", "Core.csproj"),
                ("References", new[] { "Domain" }), ("Packages", new[] { "Example.Package" }),
                ("Frameworks", new[] { "net10.0" }))
        });

        var violations = await Judge("The core must remain general-purpose.", codebase);

        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0].GetField("Message"), Is.EqualTo("Business logic belongs outside the core."));
        Assert.That(violations[0].GetField("File"), Is.EqualTo("Example.cs"));
        Assert.That(violations[0].GetField("Line"), Is.EqualTo(2));
        Assert.That(violations[0].GetField("Source"), Is.EqualTo("ai.judge"));
        Assert.That(violations[0].GetField("Check"), Is.EqualTo(""));
        using var request = JsonDocument.Parse(await model.Request);
        Assert.That(request.RootElement.GetProperty("model").GetString(), Is.EqualTo("local-test-model"));
        using var context = Context(request);
        var root = context.RootElement;
        var source = root.GetProperty("files")[0];
        Assert.That(source.GetProperty("path").GetString(), Is.EqualTo("Example.cs"));
        Assert.That(source.GetProperty("namespace").GetString(), Is.EqualTo("Core"));
        Assert.That(source.GetProperty("imports")[0].GetString(), Is.EqualTo("System"));
        Assert.That(source.GetProperty("lines").GetArrayLength(), Is.EqualTo(2));
        Assert.That(source.GetProperty("lines")[0].GetProperty("number").GetInt32(), Is.EqualTo(1));
        Assert.That(source.GetProperty("lines")[1].GetProperty("text").GetString(),
            Is.EqualTo("    public int Total() => 0;"));
        Assert.That(root.GetProperty("types")[0].GetProperty("baseTypes")[0].GetString(),
            Is.EqualTo("BaseService"));
        Assert.That(root.GetProperty("types")[0].GetProperty("interfaces")[0].GetString(), Is.EqualTo("IService"));
        Assert.That(root.GetProperty("methods")[0].GetProperty("returnType").GetString(), Is.EqualTo("int"));
        Assert.That(root.GetProperty("calls")[0].GetProperty("memberName").GetString(), Is.EqualTo("Submit"));
        Assert.That(root.GetProperty("projects")[0].GetProperty("references")[0].GetString(), Is.EqualTo("Domain"));
        Assert.That(root.GetProperty("projects")[0].GetProperty("packages")[0].GetString(),
            Is.EqualTo("Example.Package"));
    }

    [TestCase("string")]
    [TestCase("collection")]
    [TestCase("null")]
    [TestCase("incomplete")]
    public async Task Judge_RejectsLegacyOrInvalidArgumentsBeforeLoadingConfiguration(string kind)
    {
        object? input = kind switch
        {
            "string" => "class Example { }",
            "collection" => new List<object?> { Object("Type", ("Name", "Example")) },
            "incomplete" => Object("Codebase", ("Files", Array.Empty<object>())),
            _ => null
        };

        var violations = await Judge("The core must remain general-purpose.", input);

        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0].GetField("Message"), Is.EqualTo(
            "ai.judge: expected a Codebase from a language parse() function or codebase(...); " +
            "strings and individual collections are not supported"));
    }

    [Test]
    public async Task Judge_RejectsEmptyCodebaseInsteadOfReturningNoFindings()
    {
        var codebase = EmptyCodebase();
        var violations = await Judge("The core must remain general-purpose.", codebase);
        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0].GetField("Message"), Is.EqualTo(
            "ai.judge: the Codebase contains no files; check the parser and target path"));
    }

    [Test]
    public async Task Judge_RejectsFilesWithoutSource()
    {
        var codebase = Codebase(Object("File", ("Path", "Example.cs")));
        var violations = await Judge("The core must remain general-purpose.", codebase);
        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0].GetField("Message"), Is.EqualTo(
            "ai.judge: the Codebase has no source lines for 'Example.cs'; review cannot proceed"));
    }

    [Test]
    public async Task Judge_DoesNotTruncateLongSourceLines()
    {
        using var model = ConfigureModel("[]");
        var file = Object("File", ("Path", "Example.cs"));
        var source = "// " + new string('x', 150_000) + " end of source";
        var violations = await Judge("The core must remain general-purpose.",
            Codebase(file, Object("Line", ("File", file), ("Number", 1), ("Text", source))));
        Assert.That(violations, Is.Empty);
        using var request = JsonDocument.Parse(await model.Request);
        using var context = Context(request);
        Assert.That(context.RootElement.GetProperty("files")[0].GetProperty("lines")[0]
            .GetProperty("text").GetString(), Is.EqualTo(source));
    }

    [Test]
    public async Task Judge_RejectsAmbiguousSourceInCombinedScopes()
    {
        var file = Object("File", ("Path", "Example.cs"));
        var codebase = Codebase(file,
            Object("Line", ("File", file), ("Number", 1), ("Text", "class First { }")),
            Object("Line", ("File", file), ("Number", 1), ("Text", "class Second { }")));
        var violations = await Judge("The core must remain general-purpose.", codebase);
        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0].GetField("Message"), Is.EqualTo(
            "ai.judge: conflicting source text for 'Example.cs' at line 1"));
    }

    [Test]
    public async Task Judge_RejectsSourceOutsideTheCodebase()
    {
        var codebase = Codebase(Object("File", ("Path", "Example.cs")),
            Object("Line", ("File", Object("File", ("Path", "Other.cs"))), ("Number", 1), ("Text", "secret")));
        var violations = await Judge("The core must remain general-purpose.", codebase);
        Assert.That(violations, Has.Count.EqualTo(1));
        Assert.That(violations[0].GetField("Message"), Is.EqualTo(
            "ai.judge: source line refers to a file outside Codebase.Files: Other.cs"));
    }

    [TestCase("[]", 0)]
    [TestCase("not JSON", 1)]
    [TestCase(null, 1)]
    public async Task Judge_PreservesCleanReviewsAndReportsMalformedModelOutput(string? response, int count)
    {
        using var model = ConfigureModel(response);
        var file = Object("File", ("Path", "Example.cs"));
        var violations = await Judge("The core must remain general-purpose.",
            Codebase(file, Object("Line", ("File", file), ("Number", 1), ("Text", "class Example { }"))));
        Assert.That(violations, Has.Count.EqualTo(count));
        if (count == 1)
            Assert.That(violations[0].GetField("Message"),
                Is.EqualTo(response is null
                    ? "ai.judge error: model response contained no review content"
                    : "ai.judge: could not parse model output as JSON violations: not JSON"));
        await model.Request;
    }

    [TestCase("""[{"file":"Other.cs","line":1,"message":"Not supplied."}]""")]
    [TestCase("""[{"file":"Example.cs","line":2,"message":"Beyond source."}]""")]
    [TestCase("""[{"file":"Example.cs","line":0,"message":"No location."}]""")]
    [TestCase("""[{"file":"Example.cs","line":1,"message":false}]""")]
    [TestCase("""[{"file":"Example.cs","line":1,"message":"Extra field.","extra":true}]""")]
    public async Task Judge_FullModeRejectsInvalidFindingLocationsAndShapes(string response)
    {
        using var model = ConfigureModel(response);
        var file = Object("File", ("Path", "Example.cs"));
        var result = await Judge("Policy",
            Codebase(file, Object("Line", ("File", file), ("Number", 1), ("Text", "class Example { }"))));
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].GetField("IsInfrastructureError"), Is.EqualTo(true));
        Assert.That(result[0].GetField("File"), Is.EqualTo(""));
        Assert.That(result[0].GetField("Line"), Is.EqualTo(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Engine_ReviewsParsedCodebasesWithFullSourceAndScope(bool combineScopes)
    {
        using var model = ConfigureModel("""[{"file":"Example.cs","line":5,"message":"Constant total."}]""");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(Path.Combine(target, "core"));
        File.WriteAllText(Path.Combine(target, "core", "Example.cs"), """
            using System;
            namespace Fixture;
            public class Example
            {
                public int Total(int subtotal) => 0;
            }
            """);
        File.WriteAllText(Path.Combine(target, "Outside.cs"), "public class OutsideScope { }");
        Directory.CreateDirectory(Path.Combine(target, "domain"));
        File.WriteAllText(Path.Combine(target, "domain", "Helper.cs"), "public class Helper { }");
        File.WriteAllText(Path.Combine(target, "core", "Core.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><ProjectReference Include="..\Domain\Domain.csproj" /></ItemGroup>
            </Project>
            """);
        var codebase = combineScopes
            ? "codebase(csharp.parse('core'), csharp.parse('domain'))"
            : "csharp.parse('core')";
        var program = $$"""
            import ai
            import code
            import csharp
            let cb = {{codebase}}
            let findings = ai.judge('Invoice totals must reflect the subtotal.', cb)
            command MAIN = CHECK(findings)
            """;
        var scripts = Path.Combine(_root, "checks");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "main.cop"), program);

        var result = Engine.Run(scripts, target, additionalFeedPaths: [Path.Combine(RepoRoot, "packages")],
            reviewOptions: new ReviewOptions { Scope = "full" });

        Assert.That(result.ParseErrors, Is.Empty);
        Assert.That(result.Errors, Is.Empty);
        Assert.That(result.Outputs.Select(output => output.Message),
            Is.EqualTo(new[] { "Example.cs(5): error: Constant total." }));
        using var request = JsonDocument.Parse(await model.Request);
        using var context = Context(request);
        var files = context.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.That(files.Select(file => file.GetProperty("path").GetString()),
            Is.EqualTo(combineScopes ? new[] { "Example.cs", "Helper.cs" } : new[] { "Example.cs" }));
        Assert.That(files[0].GetProperty("lines")[4].GetProperty("text").GetString(),
            Is.EqualTo("    public int Total(int subtotal) => 0;"));
        var projects = context.RootElement.GetProperty("projects");
        Assert.That(projects.GetArrayLength(), Is.EqualTo(1));
        Assert.That(projects[0].GetProperty("name").GetString(), Is.EqualTo("Core"));
        Assert.That(projects[0].GetProperty("references")[0].GetString(), Is.EqualTo("Domain"));
    }

    [Test]
    public async Task PublishedCli_ReviewsRealSourceAndReturnsFindingExitCode()
    {
        using var model = ConfigureModel("""[{"file":"Invoice.cs","line":3,"message":"Constant total."}]""");
        var target = Path.Combine(_root, "source");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Invoice.cs"), """
            public class Invoice
            {
                public int Total(int subtotal) => 0;
            }
            """);
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, """
            import ai
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = CHECK(ai.judge('Totals must reflect the subtotal.', cb))
            """);
        var result = await RunCli(script, "-t", target, "--no-color");
        Assert.That(result.ExitCode, Is.EqualTo(1), result.Stderr);
        Assert.That(TestTextParsing.StripAnsi(result.Stdout).Trim(),
            Is.EqualTo("Invoice.cs(3): error: Constant total."));
        using var request = JsonDocument.Parse(await model.Request);
        using var context = Context(request);
        Assert.That(context.RootElement.GetProperty("files")[0].GetProperty("lines")[2]
            .GetProperty("text").GetString(), Is.EqualTo("    public int Total(int subtotal) => 0;"));
    }

    [TestCase("'literal source'")]
    [TestCase("cb.Types")]
    [TestCase("cb.Files")]
    [TestCase("cb.Lines")]
    public async Task PublishedCli_RejectsLegacyArgumentsBeforeCallingModel(string argument)
    {
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, $$"""
            import ai
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = CHECK(ai.judge('The core must remain general-purpose.', {{argument}}))
            """);
        File.WriteAllText(Path.Combine(_root, "Example.cs"), "public class Example { }");
        var result = await RunCli(script, "-t", _root);
        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(TestTextParsing.StripAnsi(result.Stdout).Trim(), Is.EqualTo(
            "(0): error: ai.judge: expected a Codebase from a language parse() function or codebase(...); " +
            "strings and individual collections are not supported"));
    }

    [Test]
    public async Task PublishedCli_CorePurityCheckReviewsCoreImplementationOnly()
    {
        using var model = ConfigureModel("[]");
        var result = await RunCli(Path.Combine(RepoRoot, "cop-checks", "main.cop"),
            "-t", RepoRoot, "-c", "ai", "--no-color");
        Assert.That(result.ExitCode, Is.EqualTo(0), result.Stderr + result.Stdout);
        Assert.That(result.Stdout.Trim(), Is.EqualTo(""));
        using var request = JsonDocument.Parse(await model.Request);
        using var context = Context(request);
        var files = context.RootElement.GetProperty("files").EnumerateArray().ToArray();
        var engine = files.Single(file => file.GetProperty("path").GetString() == "Engine.cs");
        Assert.That(engine.GetProperty("lines")[0].GetProperty("text").GetString(),
            Is.EqualTo("using System.Diagnostics;"));
        Assert.That(files.Any(file => file.GetProperty("path").GetString() == "cli/Program.cs"), Is.False);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCli(params string[] args)
    {
        var exe = Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");
        if (!File.Exists(exe))
            Assert.Ignore("Run install/publish.ps1 -Runtimes win-x64 before the CLI integration test.");
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--scope");
        start.ArgumentList.Add("full");
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }
        return (process.ExitCode, await stdout, await stderr);
    }

    private MockModel ConfigureModel(string? response)
    {
        var model = new MockModel(response);
        File.WriteAllText(_config, JsonSerializer.Serialize(new
        {
            endpoint = model.Endpoint,
            model = "local-test-model",
            apiKey = ""
        }));
        return model;
    }

    private static JsonDocument Context(JsonDocument request)
    {
        var user = request.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        const string marker = "\n\nCODEBASE UNDER REVIEW:\n";
        Assert.That(user, Does.Contain(marker));
        return JsonDocument.Parse(user[(user.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
    }

    private static async Task<List<DataObject>> Judge(string requirement, object? codebase)
    {
        var result = await new AiProvider().GetFunctions()!["judge"]([requirement, codebase]);
        Assert.That(result, Is.InstanceOf<IEnumerable<object?>>());
        return ((IEnumerable<object?>)result!).Cast<DataObject>().ToList();
    }

    private static DataObject Codebase(DataObject file, params DataObject[] lines)
    {
        var codebase = EmptyCodebase();
        codebase.Set("Files", new[] { file });
        codebase.Set("Lines", lines);
        return codebase;
    }

    private static DataObject EmptyCodebase()
    {
        var codebase = new DataObject("Codebase");
        foreach (var name in new[] { "Files", "Types", "Methods", "Statements", "Calls", "Lines", "Regions", "Projects" })
            codebase.Set(name, Array.Empty<object>());
        return codebase;
    }

    private static DataObject Object(string type, params (string Name, object? Value)[] fields) =>
        new(type, fields.ToDictionary(field => field.Name, field => field.Value));

    private sealed class MockModel : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
        public string Endpoint { get; }
        public Task<string> Request { get; }

        public MockModel(string? response)
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/chat/completions";
            Request = Serve(response);
        }

        private async Task<string> Serve(string? response)
        {
            using var client = await _listener.AcceptTcpClientAsync(_timeout.Token);
            using var stream = client.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                await stream.ReadExactlyAsync(buffer, _timeout.Token);
                header.Append((char)buffer[0]);
                if (header.Length > 65536) throw new InvalidOperationException("Oversized HTTP headers.");
            }
            var length = int.Parse(header.ToString().Split("\r\n")
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Split(':')[1].Trim());
            var body = new byte[length];
            await stream.ReadExactlyAsync(body, _timeout.Token);
            var content = JsonSerializer.SerializeToUtf8Bytes(new
            {
                choices = new[] { new { message = new { content = response } } }
            });
            var headers = Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(headers, _timeout.Token);
            await stream.WriteAsync(content, _timeout.Token);
            return Encoding.UTF8.GetString(body);
        }

        public void Dispose()
        {
            _timeout.Cancel();
            _listener.Stop();
            _timeout.Dispose();
        }
    }
}
