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
public class AiChangeReviewTests
{
    private string _root = "";
    private string _repo = "";
    private string? _previousConfig;
    private readonly List<string> _logs = [];

    [SetUp]
    public void SetUp()
    {
        var directory = TestContext.CurrentContext.TestDirectory;
        while (!File.Exists(Path.Combine(directory, "cop.sln")))
            directory = Path.GetDirectoryName(directory)!;
        _root = Path.Combine(directory, "tests", "behavior", "ai-changes-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(_repo);
        File.WriteAllText(Path.Combine(_repo, "Example.cs"), "class Example {\nint Total => 1;\n}\n");
        File.WriteAllText(Path.Combine(_repo, "Helper.cs"), "class Helper { /* unchanged supporting evidence */ }\n");
        Git("init", "-q");
        Git("add", ".");
        Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "base");
        _previousConfig = Environment.GetEnvironmentVariable("COP_AI_CONFIG");
        Environment.SetEnvironmentVariable("COP_AI_CONFIG", Path.Combine(_root, "missing-config.json"));
        _logs.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Environment.SetEnvironmentVariable("COP_AI_CONFIG", _previousConfig);
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, true);
        }
    }

    [Test]
    public async Task Diff_FirstRequestContainsOnlySelectedDiff_ThenReadsUnchangedSource()
    {
        Change();
        using var model = Configure((turn, _) => turn == 0
            ? Tool("read_source", new { path = "Helper.cs", start_line = 1, line_count = 1, side = "head" })
            : Final("""[{"file":"Example.cs","line":2,"message":"Total must use the input."}]"""));
        var result = await Judge();
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].GetField("Message"), Is.EqualTo("Total must use the input."));
        Assert.That(result[0].GetField("File"), Is.EqualTo("Example.cs"));
        Assert.That(result[0].GetField("Line"), Is.EqualTo(2));
        Assert.That(model.Requests, Has.Count.EqualTo(2));
        using var first = JsonDocument.Parse(model.Requests[0]);
        Assert.That(first.RootElement.GetProperty("tool_choice").GetString(), Is.EqualTo("required"));
        Assert.That(first.RootElement.GetProperty("tools").GetArrayLength(), Is.EqualTo(2));
        var subject = first.RootElement.GetProperty("messages")[1].GetProperty("content").GetString();
        Assert.That(subject, Does.Contain("Example.cs").And.Contain("int Total"));
        Assert.That(subject, Does.Not.Contain("unchanged supporting evidence").And.Not.Contain("CODEBASE UNDER REVIEW"));
        Assert.That(model.Requests[1], Does.Contain("unchanged supporting evidence"));
        Assert.That(_logs.Last(), Does.Contain("review completed").And.Contain("2 requests"));
    }

    [Test]
    public async Task Diff_EmptySelectionSkipsConfigurationAndRequests()
    {
        var result = await Judge();
        Assert.That(result, Is.Empty);
        Assert.That(_logs.Last(), Does.Contain("no applicable changes").And.Contain("0 model requests"));
    }

    [Test]
    public async Task Diff_RequiresToolSupportEvenForImmediateEmptyArray()
    {
        Change();
        using var model = Configure((_, _) => Final("[]"));
        var result = await Judge();
        AssertFailure(result, "requires tool-calling support");
        Assert.That(model.Requests, Has.Count.EqualTo(1));
    }

    [TestCase("{}")]
    [TestCase("[{}]")]
    [TestCase("""[{"file":"Example.cs","line":"2","message":"bad"}]""")]
    [TestCase("""[{"file":"Example.cs","line":2.5,"message":"bad"}]""")]
    [TestCase("""[{"file":"Example.cs","line":2,"message":""}]""")]
    [TestCase("""[{"file":"Helper.cs","line":1,"message":"pre-existing"}]""")]
    [TestCase("""[{"file":"Example.cs","line":1,"message":"outside hunk"}]""")]
    [TestCase("""[{"file":"../Example.cs","line":2,"message":"escaped"}]""")]
    [TestCase("""[{"file":"Example.cs","line":0,"message":"not deletion"}]""")]
    [TestCase("""[{"file":"Example.cs","line":2,"message":"bad","extra":true}]""")]
    public async Task Diff_InvalidFinalOutputIsInfrastructureFailureNotClean(string final)
    {
        Change();
        using var model = Configure((turn, _) => turn == 0 ? ReadExample() : Final(final));
        AssertFailure(await Judge(), "change review failed");
    }

    [Test]
    public async Task Diff_SourceLookupRejectsEscapes()
    {
        Change();
        using var model = Configure((_, _) =>
            Tool("read_source", new { path = "../secret.cs", start_line = 1, line_count = 1, side = "head" }));
        AssertFailure(await Judge(), "change review failed");
        Assert.That(model.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Diff_ChangesDuringToolLoopFailStale()
    {
        Change();
        using var model = Configure((turn, _) =>
        {
            if (turn == 0) return ReadExample();
            File.AppendAllText(Path.Combine(_repo, "Helper.cs"), "// moved during review\n");
            return Final("[]");
        });
        AssertFailure(await Judge(), "chang");
    }

    [Test]
    public async Task Diff_RequestBudgetExhaustionCannotBecomeSuccess()
    {
        Change();
        using var model = Configure((_, _) => ReadExample());
        AssertFailure(await Judge(), "turn budget exhausted");
        Assert.That(model.Requests, Has.Count.EqualTo(8));
    }

    [Test]
    public async Task Diff_RejectsOversizedSourceReadWithoutTruncation()
    {
        Change();
        using var model = Configure((_, _) =>
            Tool("read_source", new { path = "Helper.cs", start_line = 1, line_count = 201, side = "head" }));
        AssertFailure(await Judge(), "maximum 200 lines");
        Assert.That(model.Requests, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Diff_SearchReadsFullTargetAndReturnsNumberedResults()
    {
        Change();
        using var model = Configure((turn, _) => turn == 0
            ? Tool("search_source", new { query = "supporting evidence", max_results = 4 })
            : Final("[]"));
        Assert.That(await Judge(), Is.Empty);
        Assert.That(model.Requests[1], Does.Contain("Helper.cs").And.Contain("supporting evidence"));
        Assert.That(model.Requests, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task Diff_AbsoluteProvenanceMapsParsedSubroots()
    {
        Directory.CreateDirectory(Path.Combine(_repo, "core"));
        File.Move(Path.Combine(_repo, "Example.cs"), Path.Combine(_repo, "core", "Example.cs"));
        Git("add", ".");
        Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "move");
        File.WriteAllText(Path.Combine(_repo, "core", "Example.cs"), "class Example {\nint Total => 0;\n}\n");
        using var model = Configure((turn, _) => turn == 0
            ? Tool("read_source", new { path = "core/Example.cs", start_line = 1, line_count = 3, side = "head" })
            : Final("""[{"file":"core/Example.cs","line":2,"message":"Subroot finding."}]"""));
        var result = await Judge(Codebase("core\\Example.cs"));
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].GetField("File"), Is.EqualTo("core/Example.cs"));
        Assert.That(result[0].GetField("Message"), Is.EqualTo("Subroot finding."));
    }

    [TestCase("auto")]
    [TestCase("full")]
    public async Task AllScopesContinueRejectingStringsAndCollections(string scope)
    {
        var context = new AnalysisContext(_repo, new ReviewOptions { Scope = scope }, _logs.Add);
        foreach (var input in new object[] { "source", new[] { new DataObject("File") } })
        {
            var result = await new AiProvider().GetFunctions(context)!["judge"](["Policy", input]);
            AssertFailure(((IEnumerable<object?>)result!).Cast<DataObject>().ToList(),
                "strings and individual collections are not supported");
        }
        Assert.That(_logs, Is.Empty);
    }

    [Test]
    public async Task Diff_OversizedInitialDiffFailsBeforeLoadingConfiguration()
    {
        File.WriteAllText(Path.Combine(_repo, "Example.cs"), "// " + new string('x', 300_000));
        AssertFailure(await Judge(), "initial diff exceeds");
    }

    [Test]
    public async Task Diff_DeletedSourceUsesBaseToolAndAllowsFileLevelFinding()
    {
        File.Delete(Path.Combine(_repo, "Example.cs"));
        using var model = Configure((turn, _) => turn == 0
            ? Tool("read_source", new { path = "Example.cs", start_line = 1, line_count = 3, side = "base" })
            : Final("""[{"file":"Example.cs","line":0,"message":"Required API removed."}]"""));
        var result = await Judge(Codebase("Helper.cs"));
        Assert.That(result, Has.Count.EqualTo(1));
        Assert.That(result[0].GetField("Message"), Is.EqualTo("Required API removed."));
        Assert.That(result[0].GetField("File"), Is.EqualTo("Example.cs"));
        Assert.That(result[0].GetField("Line"), Is.EqualTo(0));
        Assert.That(model.Requests[1], Does.Contain("int Total"));
    }

    [TestCase("target")]
    [TestCase("subdir")]
    [TestCase("combined")]
    public void Engine_DeletionOnlyCodebasesRetainExactParserRootsAndFullModeStillRejectsEmpty(string selection)
    {
        var first = selection == "target" ? "Example.cs" : "first/Example.cs";
        var second = selection switch
        {
            "target" => "Helper.cs",
            "subdir" => "first/Helper.cs",
            _ => "second/Helper.cs"
        };
        if (selection != "target")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, first))!);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, second))!);
            File.Move(Path.Combine(_repo, "Example.cs"), Path.Combine(_repo, first));
            File.Move(Path.Combine(_repo, "Helper.cs"), Path.Combine(_repo, second));
            File.WriteAllText(Path.Combine(_repo, "Outside.cs"), "public class OutsideSelection { }\n");
            Git("add", ".");
            Git("-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-qm", "selected scopes");
            File.Delete(Path.Combine(_repo, "Outside.cs"));
        }
        File.Delete(Path.Combine(_repo, first));
        File.Delete(Path.Combine(_repo, second));
        using var model = Configure((turn, _) => turn == 0
            ? Tool("read_source", new { path = first, start_line = 1, line_count = 3, side = "base" })
            : Final(JsonSerializer.Serialize(new[] { new { file = first, line = 0, message = "Required API removed." } })));
        var parse = selection switch
        {
            "target" => "csharp.parse()",
            "subdir" => "csharp.parse('first')",
            _ => "codebase(csharp.parse('first'), csharp.parse('second'))"
        };
        var scripts = Path.Combine(_root, "checks");
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(scripts, "main.cop"), $$"""
            import ai
            import code
            import csharp
            let cb = {{parse}}
            command MAIN = CHECK(ai.judge('The public API must remain available.', cb))
            """);
        var root = _root;
        while (!File.Exists(Path.Combine(root, "cop.sln"))) root = Path.GetDirectoryName(root)!;
        var result = Engine.Run(scripts, _repo, additionalFeedPaths: [Path.Combine(root, "packages")],
            reviewOptions: new ReviewOptions { Scope = "branch", Base = "HEAD" });

        Assert.That(result.ParseErrors, Is.Empty);
        Assert.That(result.Errors, Is.Empty, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message),
            Is.EqualTo(new[] { $"{first}(0): error: Required API removed." }));
        Assert.That(result.ExitCode, Is.EqualTo(1));
        Assert.That(model.Requests, Has.Count.EqualTo(2));
        using var initial = JsonDocument.Parse(model.Requests[0]);
        var user = initial.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!;
        const string marker = "\n\nCHANGES UNDER REVIEW:\n";
        using var changes = JsonDocument.Parse(user[(user.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
        Assert.That(changes.RootElement.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("path").GetString()).OrderBy(path => path),
            Is.EqualTo(new[] { first, second }.OrderBy(path => path)));
        Assert.That(model.Requests[1], Does.Contain("int Total"));

        var full = Engine.Run(scripts, _repo, additionalFeedPaths: [Path.Combine(root, "packages")],
            reviewOptions: new ReviewOptions { Scope = "full" });
        Assert.That(full.Errors, Is.Empty);
        Assert.That(full.Outputs.Select(output => output.Message), Is.EqualTo(new[]
        {
            "(0): error: ai.judge: the Codebase contains no files; check the parser and target path"
        }));
        Assert.That(full.ExitCode, Is.EqualTo(1));
        Assert.That(model.Requests, Has.Count.EqualTo(2), "Full mode must reject an empty Codebase before contacting the model.");
    }

    [Test]
    public async Task Diff_BinarySelectedSourceFailsBeforeConfiguration()
    {
        File.WriteAllBytes(Path.Combine(_repo, "Example.cs"), [0, 1, 2, 0, 3]);
        AssertFailure(await Judge(Codebase("Helper.cs")), "binary or omitted patch");
    }

    [Test]
    public async Task Diff_OmittedSelectedSourceFailsBeforeConfiguration()
    {
        Change();
        AssertFailure(await Judge(Codebase("Helper.cs")), "omitted by the selected parser");
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task PublishedCli_DiffToolsReviewRealSourceAndKeepProtocolErrorsVisible(bool invalidFinding)
    {
        Change();
        using var model = Configure((turn, _) => turn == 0 ? ReadExample()
            : Final(invalidFinding
                ? """[{"file":"Helper.cs","line":1,"message":"Out-of-scope finding."}]"""
                : """[{"file":"Example.cs","line":2,"message":"Changed total."}]"""));
        var script = Path.Combine(_root, "main.cop");
        File.WriteAllText(script, """
            import ai
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = CHECK(ai.judge('Totals must reflect input.', cb))
            """);
        var root = _root;
        while (!File.Exists(Path.Combine(root, "cop.sln"))) root = Path.GetDirectoryName(root)!;
        var exe = Path.Combine(root, "install", "win-x64", "cop.exe");
        if (!File.Exists(exe)) Assert.Ignore("Publish win-x64 before running CLI integration tests.");
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[] { script, "-t", _repo, "--scope", "branch", "--base", "HEAD",
            "--report-scope", "changes", "--no-color" }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw; }
        Assert.That(process.ExitCode, Is.EqualTo(1), await errors + await output);
        if (invalidFinding)
            Assert.That(TestTextParsing.StripAnsi(await output), Does.Contain(
                "ai.judge: change review failed: finding outside selected changed files: 'Helper.cs'"));
        else
            Assert.That(TestTextParsing.StripAnsi(await output).Trim(),
                Is.EqualTo("Example.cs(2): error: Changed total."));
        Assert.That(model.Requests, Has.Count.EqualTo(2));
    }

    private void Change() =>
        File.WriteAllText(Path.Combine(_repo, "Example.cs"), "class Example {\nint Total => 0;\n}\n");

    private Task<List<DataObject>> Judge() => Judge(Codebase("Example.cs"));

    private async Task<List<DataObject>> Judge(DataObject codebase)
    {
        var context = new AnalysisContext(_repo, new ReviewOptions { Scope = "branch", Base = "HEAD" }, _logs.Add);
        var result = await new AiProvider().GetFunctions(context)!["judge"](["Totals must reflect input.", codebase]);
        return ((IEnumerable<object?>)result!).Cast<DataObject>().ToList();
    }

    private DataObject Codebase(string relative)
    {
        var full = Path.Combine(_repo, relative);
        var file = new DataObject("File");
        file.Set("Path", Path.GetFileName(relative));
        file.Set("FullPath", full);
        file.Set("SourceRoot", Path.GetDirectoryName(full));
        var cb = new DataObject("Codebase");
        foreach (var collection in new[] { "Files", "Lines", "Types", "Methods", "Statements", "Calls", "Regions", "Projects" })
            cb.Set(collection, Array.Empty<object>());
        cb.Set("Files", new[] { file });
        cb.Set("Lines", File.ReadAllLines(full).Select((text, index) =>
        {
            var line = new DataObject("Line");
            line.Set("File", file);
            line.Set("Number", index + 1);
            line.Set("Text", text);
            return line;
        }).ToArray());
        return cb;
    }

    private static void AssertFailure(List<DataObject> findings, string text)
    {
        Assert.That(findings, Has.Count.EqualTo(1));
        Assert.That(findings[0].GetField("Message"), Does.Contain(text));
        Assert.That(findings[0].GetField("IsInfrastructureError"), Is.EqualTo(true));
        Assert.That(findings[0].GetField("File"), Is.EqualTo(""));
        Assert.That(findings[0].GetField("Line"), Is.EqualTo(0));
    }

    private void Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _repo, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in args) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Assert.That(process.ExitCode, Is.EqualTo(0), stderr.GetAwaiter().GetResult() + stdout.GetAwaiter().GetResult());
    }

    private Model Configure(Func<int, string, object> respond)
    {
        var model = new Model(respond);
        var config = Path.Combine(_root, "ai.json");
        File.WriteAllText(config, JsonSerializer.Serialize(new { endpoint = model.Endpoint, model = "local-test", apiKey = "" }));
        Environment.SetEnvironmentVariable("COP_AI_CONFIG", config);
        return model;
    }

    private static object ReadExample() =>
        Tool("read_source", new { path = "Example.cs", start_line = 1, line_count = 3, side = "head" });

    private static object Tool(string name, object args) => new
    {
        choices = new[] { new
        {
            finish_reason = "tool_calls",
            message = new
            {
                role = "assistant", content = (string?)null,
                tool_calls = new[] { new
                {
                    id = "call_fixture", type = "function",
                    function = new { name, arguments = JsonSerializer.Serialize(args) }
                }}
            }
        }}
    };

    private static object Final(string content) => new
    {
        choices = new[] { new { finish_reason = "stop", message = new { role = "assistant", content } } }
    };

    private sealed class Model : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new(TimeSpan.FromSeconds(40));
        private readonly Task _server;
        public List<string> Requests { get; } = [];
        public string Endpoint { get; }

        public Model(Func<int, string, object> respond)
        {
            _listener.Start();
            Endpoint = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/chat/completions";
            _server = Serve(respond);
        }

        private async Task Serve(Func<int, string, object> respond)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    using var stream = client.GetStream();
                    var header = new StringBuilder();
                    var one = new byte[1];
                    while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        await stream.ReadExactlyAsync(one, _stop.Token);
                        header.Append((char)one[0]);
                        if (header.Length > 65536) throw new InvalidOperationException("Oversized headers");
                    }
                    var length = int.Parse(header.ToString().Split("\r\n")
                        .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                    var body = new byte[length];
                    await stream.ReadExactlyAsync(body, _stop.Token);
                    var request = Encoding.UTF8.GetString(body);
                    Requests.Add(request);
                    var content = JsonSerializer.SerializeToUtf8Bytes(respond(Requests.Count - 1, request));
                    var headers = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {content.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _stop.Token);
                    await stream.WriteAsync(content, _stop.Token);
                }
            }
            catch (OperationCanceledException) { }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _server.GetAwaiter().GetResult();
            _stop.Dispose();
        }
    }
}
