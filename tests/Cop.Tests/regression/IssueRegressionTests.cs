using System.Diagnostics;
using Cop.Lang.Interpreter;
using Cop.Lang;
using Cop.Providers;
using Cop.Repl;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class IssueRegressionTests
{
    private string _workDir = null!;

    [SetUp]
    public void SetUp()
    {
        _workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "regression-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workDir))
        {
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }
    }

    [Test]
    public async Task Issue107_ReplEvaluatesExpressionsBindingsAndCommands()
    {
        File.WriteAllText(Path.Combine(_workDir, "main.cop"),
            "let numbers = [2, 3]\n" +
            "command SHOW = foreach numbers => '{item}'\n" +
            "let once = print('once')\n" +
            "command SAVEFILE = save('result.txt', 'saved')\n" +
            "command ESCAPE = save('../escape.txt', 'bad')\n" +
            "function __eval__() => 99\n");
        File.WriteAllText(Path.Combine(_workDir, "sample.txt"), "sample");
        List<string> errors = [];
        await using ReplContext context = Engine.PrepareRepl(_workDir, _workDir, errors)!;
        Assert.That(errors, Is.Empty);
        Assert.That(context, Is.Not.Null);
        ReplEvaluator repl = new ReplEvaluator(context);

        Assert.That(await repl.EvaluateAsync("1 + 2"), Is.EqualTo(new[] { "3" }));
        Assert.That(await repl.EvaluateAsync("__eval__()"), Is.EqualTo(new[] { "99" }));
        Assert.That(await repl.EvaluateAsync("numbers"), Is.EqualTo(new[] { "2", "3" }));
        Assert.That(await repl.EvaluateAsync("numbers.Count"), Is.EqualTo(new[] { "2" }));
        Assert.That(await repl.EvaluateAsync("filesystem.Files.Count"), Is.EqualTo(new[] { "2" }));
        Assert.That(await repl.EvaluateAsync("SHOW"), Is.EqualTo(new[] { "2", "3" }));
        Assert.That(await repl.EvaluateAsync("once"), Is.EqualTo(new[] { "once" }));
        Assert.That(await repl.EvaluateAsync("once"), Is.Empty);
        Assert.That(await repl.EvaluateAsync("SAVEFILE"), Is.EqualTo(new[] { "SAVE: result.txt" }));
        Assert.That(File.ReadAllText(Path.Combine(_workDir, "result.txt")), Is.EqualTo("saved"));
        Assert.That(Assert.ThrowsAsync<InvalidOperationException>(
            async () => await repl.EvaluateAsync("ESCAPE"))!.Message, Does.Contain("outside the project root"));
        Assert.That(Assert.ThrowsAsync<CopEvaluationException>(
            async () => await repl.EvaluateAsync("missing"))!.Message, Does.Contain("Undefined"));
        Assert.ThrowsAsync<ParseException>(async () => await repl.EvaluateAsync("1 2"));
        Assert.That(await repl.EvaluateAsync("1 + 4"), Is.EqualTo(new[] { "5" }));
    }

    [Test]
    public async Task Issue107_ReplLoadsDeclaredFeedsAndExternalProviders()
    {
        string feed = Path.Combine(RepoRoot, "packages").Replace("\\", "\\\\").Replace("'", "\\'");
        File.WriteAllText(Path.Combine(_workDir, "main.cop"), $$"""
            feed '{{feed}}'
            import code
            import csharp
            let model = csharp.parse()
            command TYPES = foreach model.Types => '{item.Name}'
            """);
        File.WriteAllText(Path.Combine(_workDir, "Input.cs"), "namespace Fixture; class Found { }");
        List<string> errors = [];
        await using ReplContext context = Engine.PrepareRepl(_workDir, _workDir, errors)!;
        Assert.That(errors, Is.Empty, string.Join("\n", errors));
        ReplEvaluator repl = new ReplEvaluator(context);

        Assert.That(await repl.EvaluateAsync("model.Types.Count"), Is.EqualTo(new[] { "1" }));
        Assert.That(await repl.EvaluateAsync("TYPES"), Is.EqualTo(new[] { "Found" }));
    }

    [Test]
    public void Issue117_PublishedSelfChecksAnalyzeFixtureWithoutMissingMethod()
    {
        const string sample = """
            // Fixture for the repository's interface policy.
            namespace Fixture.Example;
            /// <summary>A contract rejected by the repository's interface policy.</summary>
            public interface IUnexpected
            {
                /// <summary>Executes the fixture operation.</summary>
                void Run();
            }
            """;
        File.WriteAllText(Path.Combine(_workDir, "Wrong.cop"),
            "export function misplaced(value : string) : string => intrinsic");
        File.WriteAllText(Path.Combine(_workDir, "Sample.cs"), sample);
        foreach (string folder in new[] { "tests", "samples", "examples" })
        {
            Directory.CreateDirectory(Path.Combine(_workDir, folder));
            File.WriteAllText(Path.Combine(_workDir, folder, "Sample.cs"), sample);
        }

        var result = RunCop($"\"{Path.Combine(RepoRoot, "cop-checks", "main.cop")}\" -t \"{_workDir}\" --no-user-checks --no-color", _workDir);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo(
            "Wrong.cop(1): error: Intrinsic declaration must be in intrinsics.cop, not Wrong.cop\n" +
            "Sample.cs(4): error: Use an abstract class instead of interface IUnexpected (Sample.cs)"), Describe(result));
        Assert.That(result.Stderr, Does.Not.Contain("MissingMethodException"), Describe(result));
    }

    [TestCase("python.parse()")]
    [TestCase("python.parse('.')")]
    public void Issue060_PublishedPythonParseReturnsComposableCodebase(string parse)
    {
        File.WriteAllText(Path.Combine(_workDir, "example.py"), "class Example:\n    pass\n");
        File.WriteAllText(Path.Combine(_workDir, "main.cop"), $$"""
            import code
            import python
            let cb = codebase({{parse}})
            command MAIN = print('{cb.Files.Count}:{cb.Types.Count}:{cb.Types[0].Name}')
            """);

        var result = RunCop($"\"{Path.Combine(_workDir, "main.cop")}\" -t \"{_workDir}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("1:1:Example"), Describe(result));
        Assert.That(result.Stderr, Does.Not.Contain("error:"), Describe(result));
    }

    [Test]
    public void Issue080_PublishedPythonAcceptsQuotedReturnAnnotation()
    {
        File.WriteAllText(Path.Combine(_workDir, "client.py"), """
            class GoodClient:
                @classmethod
                def from_connection_string(cls, conn_str: str, **kwargs) -> "GoodClient":
                    return cls(conn_str, None, **kwargs)
            """);
        File.WriteAllText(Path.Combine(_workDir, "main.cop"), """
            import python
            let cb = python.parse()
            command MAIN = print('{cb.Types.Count}:{cb.Types[0].Name}:{cb.Methods.Count}:{cb.Methods[0].Name}')
            """);

        var result = RunCop($"\"{Path.Combine(_workDir, "main.cop")}\" -t \"{_workDir}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout),
            Is.EqualTo("1:GoodClient:1:from_connection_string"), Describe(result));
        Assert.That(result.Stderr, Does.Not.Contain("error:"), Describe(result));
    }

    [Test]
    public void Issue083_PublishedPythonClassCompoundStatementTerminates()
    {
        File.WriteAllText(Path.Combine(_workDir, "example.py"),
            "class C:\n    if True:\n        value = 1\n    def method(self):\n        useful()\n");
        File.WriteAllText(Path.Combine(_workDir, "main.cop"), """
            import python
            let cb = python.parse()
            command MAIN = print('{cb.Types.Count}:{cb.Types[0].Name}:{cb.Types[0].Methods[0].Name}:{cb.Calls.Count}')
            """);

        var result = RunCop($"\"{Path.Combine(_workDir, "main.cop")}\" -t \"{_workDir}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("1:C:method:1"), Describe(result));
        Assert.That(result.Stderr, Does.Not.Contain("error:"), Describe(result));
    }

    [Test]
    public void Issue124_PublishedVarCheckResolvesValueTypesAndProjects()
    {
        File.WriteAllText(Path.Combine(_workDir, "Sample.cs"), """
            namespace Fixture;
            class Sample
            {
                string Read()
                {
                    var value = "example";
                    var anonymous = new { Name = value };
                    var nested = new[] { anonymous };
                    return value;
                }
            }
            """);
        File.WriteAllText(Path.Combine(_workDir, "Fixture.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_workDir, "main.cop"), """
            import code
            import csharp
            import csharp-checks
            let cb = csharp.parse()
            command MAIN = CHECK(varDeclarations(cb)) | print('projects:{cb.Projects.Count}')
            """);

        for (int run = 0; run < 2; run++)
        {
            var result = RunCop($"\"{Path.Combine(_workDir, "main.cop")}\" -t \"{_workDir}\"");

            Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
            Assert.That(Normalize(result.Stdout), Is.EqualTo(
                "Sample.cs(6): error: Do not use 'var' for value [var-declarations]\nprojects:1"), Describe(result));
            Assert.That(result.Stderr, Does.Not.Contain("MissingMethodException"), Describe(result));
        }
    }

    // Issue #5
    [Test]
    [Explicit("Issue #5: verify does not flag duplicate command MAIN across a folder — remove when fixed")]
    [Category("PendingFix")]
    public void Issue005_SingleFileRunIsIsolatedButFolderVerifyRejectsDuplicateMain()
    {
        File.WriteAllText(Path.Combine(_workDir, "target.cop"),
            "import code\n" +
            "command MAIN = CHECK([])\n");
        File.WriteAllText(Path.Combine(_workDir, "colliding.cop"),
            "import code\n" +
            "command MAIN = CHECK(filesystem.Folders:toError('WRONG FILE RAN'))\n");

        var run = RunCop($"\"{Path.Combine(_workDir, "target.cop")}\" -t \"{_workDir}\"");
        Assert.That(run.ExitCode, Is.EqualTo(0), Describe(run));
        Assert.That(Normalize(run.Stdout), Is.EqualTo(string.Empty), Describe(run));

        var verify = RunCop($"verify \"{_workDir}\"");
        Assert.That(verify.ExitCode, Is.EqualTo(1), Describe(verify));
        Assert.That(Normalize(verify.Stdout + verify.Stderr), Does.Contain("Duplicate declaration 'MAIN'"), Describe(verify));
    }

    // Issue #8
    [Test]
    public void Issue008_ProviderProgramInChecksFolderScansTargetRoot()
    {
        var checksDir = Path.Combine(_workDir, "checks");
        Directory.CreateDirectory(checksDir);
        File.WriteAllText(Path.Combine(_workDir, "Issue8Target.cs"), "class Issue8Target { }\n");
        File.WriteAllText(Path.Combine(checksDir, "main.cop"),
            "import code\n" +
            "import code\n" +
            "import csharp\n" +
            "let cb = codebase(csharp.parse())\n" +
            "predicate isIssue8Target(Type) => Type.Name == 'Issue8Target'\n" +
            "let violations = cb.Types:isIssue8Target:toError('ISSUE8 TYPE {item.Name}')\n" +
            "command MAIN = CHECK(violations)\n");

        var result = RunCop($"\"{Path.Combine(checksDir, "main.cop")}\" -t \"{_workDir}\"", _workDir);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("Issue8Target.cs(1): error: ISSUE8 TYPE Issue8Target"), Describe(result));
        AssertOnlyExecutionSummary(result, 1, "check");
    }

    // Issue #10
    [Test]
    public void Issue010_ExplicitSingleFileRunKeepsFileSelectionAndViolationExitCode()
    {
        var checksDir = Path.Combine(_workDir, "checks");
        Directory.CreateDirectory(checksDir);
        Directory.CreateDirectory(Path.Combine(_workDir, "empty"));
        File.WriteAllText(Path.Combine(checksDir, "target.cop"),
            "import code\n" +
            "predicate isIssue10Folder(Folder) => Folder.Path == 'empty'\n" +
            "let violations = filesystem.Folders:isIssue10Folder:toError('ISSUE10 VIOLATION {item.Path}')\n" +
            "command MAIN = CHECK(violations)\n");
        File.WriteAllText(Path.Combine(checksDir, "other.cop"),
            "import code\n" +
            "command MAIN = CHECK([])\n");

        var result = RunCop($"\"{Path.Combine(checksDir, "target.cop")}\" -t \"{_workDir}\"", _workDir);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("empty(0): error: ISSUE10 VIOLATION empty"), Describe(result));
        AssertOnlyExecutionSummary(result, 1, "check");
    }

    // Issue #37
    [Test]
    public void Issue037_SuccessfulForeachOutputExitsZero()
    {
        var program = Path.Combine(_workDir, "foreach.cop");
        File.WriteAllText(program, "command MAIN = foreach [1] => 'ISSUE37 OUTPUT {item}'\n");

        var result = RunCop($"\"{program}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("ISSUE37 OUTPUT 1"), Describe(result));
        AssertOnlyExecutionSummary(result);
    }

    // Issue #38
    [Test]
    public void Issue038_BareTopLevelExpressionProducesOutput()
    {
        var program = Path.Combine(_workDir, "expression.cop");
        File.WriteAllText(program, "'ISSUE38 OUTPUT'\n");

        var result = RunCop($"\"{program}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("ISSUE38 OUTPUT"), Describe(result));
        AssertOnlyExecutionSummary(result);
    }

    // Issue #50 — `cb.Types.Methods:<methodPredicate>` (the canonical "flag long methods" rule
    // from the getting-started walkthroughs) must flatten and bind per-Method, not hand the
    // predicate a collection-of-collections and fatal at runtime.
    [Test]
    public void Issue050_MethodsOnTypeCollectionFlattensPerMethod()
    {
        File.WriteAllText(Path.Combine(_workDir, "Foo.cs"),
            "class Foo { public void M() { int a = 1; int b = 2; int c = a + b; } }\n");
        File.WriteAllText(Path.Combine(_workDir, "check.cop"),
            "import csharp\n" +
            "import code\n" +
            "let cb = codebase(csharp.parse())\n" +
            "predicate isTooLong(Method) => Method.Statements.count() > 1\n" +
            "let longMethods = cb.Types.Methods:isTooLong :toWarning('Method {item.Name}')\n" +
            "command MAIN = CHECK(longMethods)\n");

        var result = RunCop($"run \"{Path.Combine(_workDir, "check.cop")}\" -t \"{_workDir}\"");

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("Foo.cs(1): warning: Method M"), Describe(result));
    }

    // Issue #51 — `cop verify` must accept valid overloaded function declarations (same name,
    // different arity) instead of rejecting them as a "Duplicate declaration".
    [Test]
    public void Issue051_VerifyAcceptsOverloadedFunctions()
    {
        var program = Path.Combine(_workDir, "over.cop");
        File.WriteAllText(program,
            "function greet() : string => 'hi'\n" +
            "function greet(name : string) : string => 'hi {name}'\n" +
            "command MAIN = print(greet())\n");

        var result = RunCop($"verify \"{program}\"");

        Assert.That(result.ExitCode, Is.EqualTo(0), Describe(result));
        Assert.That(Normalize(result.Stdout + result.Stderr), Does.Not.Contain("Duplicate declaration"), Describe(result));
        Assert.That(Normalize(result.Stdout + result.Stderr), Does.Contain("verified successfully"), Describe(result));
    }

    // Issue #52 — selecting a violation `let` binding by name with `-c <name>` must route it
    // through CHECK (formatted `file(line): level: message` + non-zero exit), not dump the raw
    // Violation object with a clean exit 0.
    [Test]
    public void Issue052_NamedViolationRuleIsRoutedThroughCheck()
    {
        Directory.CreateDirectory(Path.Combine(_workDir, "issue52"));
        File.WriteAllText(Path.Combine(_workDir, "check.cop"),
            "import code\n" +
            "predicate isIssue52Folder(Folder) => Folder.Path == 'issue52'\n" +
            "let issue52-violations = filesystem.Folders:isIssue52Folder:toError('ISSUE52 {item.Path}')\n" +
            "command MAIN = CHECK(issue52-violations)\n");

        var result = RunCop($"run \"{Path.Combine(_workDir, "check.cop")}\" -t \"{_workDir}\" -c issue52-violations", _workDir);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout), Is.EqualTo("issue52(0): error: ISSUE52 issue52"), Describe(result));
        // Anti-regression: the raw Violation object dump (e.g. "Severity = ...") must NOT appear.
        Assert.That(Normalize(result.Stdout), Does.Not.Contain("Severity ="), Describe(result));
        AssertOnlyExecutionSummary(result, 1, "check");
    }

    [Test]
    public void Issue066_PublishedHostExtractsCSharpStatementsWithMatchingProvider()
    {
        File.WriteAllText(Path.Combine(_workDir, "Sample.cs"),
            "public class Sample { public int M() { return 1; } }\n");
        var program = Path.Combine(_workDir, "main.cop");
        File.WriteAllText(program, """
            import code
            import csharp
            let cb = csharp.parse()
            command MAIN = CHECK(cb.Statements:toError('ISSUE66 {item.Kind} {item.Method.Name}'))
            """);

        var verify = RunCop($"verify \"{program}\"");
        Assert.That(verify.ExitCode, Is.EqualTo(0), Describe(verify));

        var result = RunCop($"\"{program}\" -t \"{_workDir}\"");

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(Normalize(result.Stdout),
            Is.EqualTo("Sample.cs(1): error: ISSUE66 return M"), Describe(result));
        AssertOnlyExecutionSummary(result, 2);
    }

    private static string RepoRoot => FindRepoRoot();

    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCop(string args, string? workingDir = null)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 -Runtimes win-x64 first.");

        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = args,
            WorkingDirectory = workingDir ?? RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail($"cop.exe timed out. Args: {args}");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static void AssertOnlyExecutionSummary((int ExitCode, string Stdout, string Stderr) result, int computations = 1, string work = "computation")
    {
        var lines = Normalize(result.Stderr).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines, Has.Length.EqualTo(1), Describe(result));
        Assert.That(lines[0], Does.StartWith("Summary   ")
            .And.Contain($"{computations} {work}{(computations == 1 ? "" : "s")} evaluated in ").And.EndWith("s."),
            Describe(result));
    }

    private static string Normalize(string text) =>
        TestTextParsing.StripAnsi(text.Replace("\r\n", "\n"), colorsOnly: true).Trim();

    private static string Describe((int ExitCode, string Stdout, string Stderr) result) =>
        $"ExitCode: {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}";
}
