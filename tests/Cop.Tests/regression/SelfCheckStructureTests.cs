using Cop.Cli.Commands;
using Cop.Lang.Ast;
using Cop.Lang.Parser;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public class SelfCheckStructureTests
{
    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var repo = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "cop.sln")))
            repo = repo.Parent;
        _repo = repo?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "self-checks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public void Issue054_AllSelfChecksPassStrictStructureWithApprovedFormattingChecks()
    {
        var checks = Path.Combine(_repo, "cop-checks");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories);
        Assert.That(files, Has.Length.EqualTo(41));
        var diagnostics = VerifyCommand.CollectDiagnostics(files, checks, File.ReadAllText,
            strictStructure: true, allowTextChecks: ["hasTab", "hasTrailingWhitespace"]);
        Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics.Select(d => d.Message)));
    }

    [Test]
    public void SelfCheckSourceLinesStayWithin90Characters()
    {
        var checks = Path.Combine(_repo, "cop-checks");
        var overlongLines = new List<string>();
        foreach (var file in Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories))
        {
            int number = 0;
            foreach (var line in File.ReadLines(file))
            {
                number++;
                if (line.Length > 90)
                    overlongLines.Add($"{Path.GetRelativePath(checks, file)}:{number}: {line.Length} characters");
            }
        }

        Assert.That(overlongLines, Is.Empty, string.Join("\n", overlongLines));
    }

    [Test]
    public void SelfChecksUsePrivateNamedDiagnosticTemplates()
    {
        var templates = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(_repo, "cop-checks"), "*.cop", SearchOption.AllDirectories))
        {
            var module = CopParser.Parse(File.ReadAllText(file), file);
            var calls = new DiagnosticCallCollector();
            calls.Visit(module);
            var bindings = module.Declarations.OfType<LetDecl>().ToDictionary(d => d.Name);
            foreach (var call in calls.Calls)
            {
                Assert.That(call.Args, Has.Count.EqualTo(1), file);
                Assert.That(call.Args[0], Is.InstanceOf<IdentifierExpr>(), file);
                var name = ((IdentifierExpr)call.Args[0]).Name;
                Assert.That(bindings.ContainsKey(name), Is.True, $"{file}: {name}");
                var binding = bindings[name];
                Assert.That(binding.IsExported, Is.False, $"{file}: {name}");
                Assert.That(binding.Value is InterpolatedStringExpr or LiteralExpr { Value: string },
                    Is.True, $"{file}: {name} must hold a plain template, not a lambda");
                templates.Add(name);
            }
        }

        Assert.That(templates, Has.Count.EqualTo(38));
        Assert.That(templates.Distinct().Count(), Is.EqualTo(38), "Sibling rules share a namespace.");
    }

    private sealed class DiagnosticCallCollector : AstWalker
    {
        public List<CallExpr> Calls { get; } = [];

        public override object? VisitCallExpr(CallExpr node)
        {
            if (node.Callee is IdentifierExpr { Name: "toError" or "toWarning" or "toInfo" })
                Calls.Add(node);
            return base.VisitCallExpr(node);
        }
    }

    [Test]
    public void SelfChecksAreGroupedBySourceLanguage()
    {
        var checks = Path.Combine(_repo, "cop-checks");
        Assert.That(Directory.GetFiles(checks, "*.cop").Select(Path.GetFileName),
            Is.EqualTo(new[] { "main.cop" }));
        Assert.That(Directory.GetFiles(Path.Combine(checks, "cop"), "*.cop", SearchOption.AllDirectories),
            Has.Length.EqualTo(6));
        Assert.That(Directory.GetFiles(Path.Combine(checks, "csharp"), "*.cop", SearchOption.AllDirectories),
            Has.Length.EqualTo(32));
        Assert.That(Directory.GetFiles(Path.Combine(checks, "general"), "*.cop", SearchOption.AllDirectories),
            Has.Length.EqualTo(2));
    }

    [Test]
    public async Task GroupedSelfChecks_RunCopAndCSharpRulesThroughRootMain()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Wrong.cop"),
            "export function misplaced(value : string) : string => intrinsic");
        File.WriteAllText(Path.Combine(target, "Sample.cs"),
            """
            // Fixture for the repository's interface policy.
            namespace Fixture.Example;
            /// <summary>A contract rejected by the repository's interface policy.</summary>
            public interface IUnexpected
            {
                /// <summary>Executes the fixture operation.</summary>
                void Run();
            }
            """);
        var checks = Path.Combine(_repo, "cop-checks");
        var main = Path.Combine(checks, "main.cop");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Where(f => f != main).Order(StringComparer.Ordinal).Append(main).ToArray();
        var result = (await Engine.RunAsync(checks, target, new EngineRunOptions
        {
            ScriptFiles = files,
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
        }));

        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
        {
            "Wrong.cop(1): error: Intrinsic declaration must be in intrinsics.cop, not Wrong.cop",
            "Sample.cs(4): error: Use an abstract class instead of interface IUnexpected (Sample.cs)"
        }));
    }

    [TestCase("tests")]
    [TestCase("samples")]
    [TestCase("examples")]
    public async Task DefaultCSharpChecksExcludeNonProductionFixtures(string folder)
    {
        var target = Path.Combine(_root, "target");
        var excluded = Path.Combine(target, folder);
        Directory.CreateDirectory(excluded);
        File.WriteAllText(Path.Combine(excluded, "Sample.cs"),
            "namespace Fixture; public class sample { public void Run() { var value = Missing(); } }");
        var checks = Path.Combine(_repo, "cop-checks");
        var main = Path.Combine(checks, "main.cop");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Where(f => f != main).Order(StringComparer.Ordinal).Append(main).ToArray();
        var result = (await Engine.RunAsync(checks, target, new EngineRunOptions
        {
            ScriptFiles = files,
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
        }));

        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.Empty);
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [Test]
    public async Task RepositoryPassesDefaultSelfChecks()
    {
        var checks = Path.Combine(_repo, "cop-checks");
        var main = Path.Combine(checks, "main.cop");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Where(f => f != main).Order(StringComparer.Ordinal).Append(main).ToArray();
        var result = (await Engine.RunAsync(checks, _repo, new EngineRunOptions
        {
            ScriptFiles = files,
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
        }));

        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.Empty);
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task SelfChecksHandleLegacyNegativeFixtureNamesWithoutChangingTheTarget(bool hasViolation)
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var fixtures = Path.Combine(_repo, "tests", "lang.tests", "referencetests", "programs");
        var names = new[] { "negative-predicate-no-body.cop", "negative-unclosed-string.cop" };
        foreach (var name in names)
            File.Copy(Path.Combine(fixtures, name + ".invalid"), Path.Combine(target, name));
        File.WriteAllText(Path.Combine(target, "Wrong.cop"),
            "export function misplaced(value : string) : string => " + (hasViolation ? "intrinsic" : "value"));
        var checks = Path.Combine(_repo, "cop-checks");
        var main = Path.Combine(checks, "main.cop");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Where(f => f != main).Order(StringComparer.Ordinal).Append(main).ToArray();

        for (int run = 0; run < 2; run++)
        {
            var result = (await Engine.RunAsync(checks, target, new EngineRunOptions
            {
                ScriptFiles = files,
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
            }));

            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Warnings!.Where(w => w.Contains("source analysis error")), Is.Empty);
            Assert.That(result.Warnings, Is.Empty);
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(hasViolation ? new[]
            {
                "Wrong.cop(1): error: Intrinsic declaration must be in intrinsics.cop, not Wrong.cop"
            } : Array.Empty<string>()));
            Assert.That(result.ExitCode, Is.EqualTo(hasViolation ? 1 : 0));
            foreach (var name in names)
                Assert.That(File.ReadAllText(Path.Combine(target, name)),
                    Is.EqualTo(File.ReadAllText(Path.Combine(fixtures, name + ".invalid"))));
        }
    }

    [Test]
    public async Task RepositoryHasNoUnapprovedRegexImports()
    {
        File.Copy(Path.Combine(_repo, "cop-checks", "csharp", "no-regex.cop"), Path.Combine(_root, "rule.cop"));
        File.WriteAllText(Path.Combine(_root, "main.cop"), """
            import code
            import csharp
            let codebase = codebase(csharp.parse())
            command MAIN = CHECK(no-regex)
            """);
        var result = (await Engine.RunAsync(_root, _repo, new EngineRunOptions
        {
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
        }));

        Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.Empty);
    }

    [Test]
    [Explicit("Issue #55: verifying code/src imports its own CHECK declaration twice - remove when fixed")]
    [Category("PendingFix")]
    public void Issue055_CodePackageSourceVerifiesWithoutSelfImportDuplicates()
    {
        var source = Path.Combine(_repo, "packages", "core", "code", "src");
        var files = Directory.GetFiles(source, "*.cop");
        var diagnostics = VerifyCommand.CollectDiagnostics(files, source, File.ReadAllText);
        Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics.Select(d => d.Message)));
    }

    [Test]
    public void CorePackageSourceVerifiesWithDefaultLibrary()
    {
        var source = Path.Combine(_repo, "packages", "core", "core", "src");
        var diagnostics = VerifyCommand.CollectDiagnostics(
            Directory.GetFiles(source, "*.cop"), source, File.ReadAllText);

        Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics.Select(d => d.Message)));
    }

    [Test]
    public async Task Issue054_EnvironmentCheckUsesCallsAndOnlyTheActualVariableName()
    {
        await AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", "Input.cs", """
            using System;
            namespace Fixture;
            class Example {
                string? A() => Environment.GetEnvironmentVariable("MODE");
                string? B() => Wrap(Environment.GetEnvironmentVariable("OTHER"));
                string? C() => Environment.GetEnvironmentVariable("GITHUB_TOKEN");
                string? D() => Environment.GetEnvironmentVariable("MODE"); // TOKEN
                string? E() => Environment.GetEnvironmentVariable(
                    "\u004dODE");
                string Decoy = "GetEnvironmentVariable(\"MODE\")";
                // Environment.GetEnvironmentVariable("MODE");
                string? F() => Other.GetEnvironmentVariable("MODE");
                string? Wrap(string? value) => value;
            }
            """, "csharp", [4, 5, 7, 8],
            "Use CLI flags or config instead of environment variables for Cop settings");
    }

    [Test]
    public async Task EnvironmentCheckKeepsUnapprovedCallsOnTheSameLine()
    {
        await AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", "Input.cs", """
            using System;
            namespace Fixture;
            class Example {
                string? Read() => Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("MODE");
                void Write() { Environment.SetEnvironmentVariable("GITHUB_TOKEN", "value"); Environment.SetEnvironmentVariable("MODE", "GITHUB_TOKEN"); }
                string? Lookup(string name) => Environment.GetEnvironmentVariable("PATH") ?? Environment.GetEnvironmentVariable(name);
            }
            """, "csharp", [4, 5, 6],
            "Use CLI flags or config instead of environment variables for Cop settings");
    }

    [Test]
    public async Task EnvironmentCheckAllowsOnlyLiteralSearchPathReads()
    {
        await AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", "Input.cs", """
            using System;
            namespace Fixture;
            class Example {
                string? Discover() => Environment.GetEnvironmentVariable("PATH");
                string? Encoded() => Environment.GetEnvironmentVariable("\u0050ATH");
                string? Toggle() => Environment.GetEnvironmentVariable("COP_FEATURE");
                void Mutate() => Environment.SetEnvironmentVariable("PATH", "bin");
                string? Unknown(string name) => Environment.GetEnvironmentVariable(name);
            }
            """, "csharp", [6, 7, 8],
            "Use CLI flags or config instead of environment variables for Cop settings");
    }

    [TestCase("cop/cli/commands/CopInstallation.cs", true)]
    [TestCase("cop/cli/commands/Other.cs", false)]
    public async Task EnvironmentCheckLimitsPathUpdatesToTheInstaller(string file, bool isInstaller)
    {
        await AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", file, """
            using System;
            namespace Fixture;
            class Example {
                void ConfigurePath(string name) {
                    Environment.SetEnvironmentVariable("PATH", "bin");
                    Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
                    Environment.SetEnvironmentVariable("COP_FEATURE", "true");
                    Environment.SetEnvironmentVariable(name, "bin");
                }
                void Other() => Environment.SetEnvironmentVariable("PATH", "bin");
            }
            """, "csharp", isInstaller ? [7, 8, 10] : [5, 6, 7, 8, 10],
            "Use CLI flags or config instead of environment variables for Cop settings");
    }

    [TestCase("providers/java-provider/src/JavaSemanticContext.cs", "Discover", "CLASSPATH", false, true)]
    [TestCase("providers/rust-provider/src/RustFrontend.cs", "Bind", "CARGO_HOME", true, true)]
    [TestCase("providers/rust-provider/src/Other.cs", "Bind", "CARGO_HOME", false, false)]
    public async Task EnvironmentCheckLimitsReviewedFrontendContextToItsOwner(
        string file, string method, string variable, bool allowsComputed, bool allowsLiteral)
    {
        int[] violations = allowsLiteral
            ? allowsComputed ? [6, 8, 11] : [6, 7, 8, 11]
            : [5, 6, 7, 8, 11];
        await AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", file, $$"""
            using System;
            namespace Fixture;
            class Example {
                string? {{method}}(string name) {
                    var context = Environment.GetEnvironmentVariable("{{variable}}");
                    var toggle = Environment.GetEnvironmentVariable("COP_FEATURE");
                    var computed = Environment.GetEnvironmentVariable(name);
                    Environment.SetEnvironmentVariable("{{variable}}", "value");
                    return context;
                }
                string? Other() => Environment.GetEnvironmentVariable("{{variable}}");
            }
            """, "csharp", violations,
            "Use CLI flags or config instead of environment variables for Cop settings");
    }

    [Test]
    public async Task Issue054_FfiApprovalUsesTheDecodedFirstArgumentOnly()
    {
        await AssertCheck(Path.Combine("csharp", "no-new-ffi.cop"), "no-new-ffi", "Input.cs", """
            class Example {
                void RegisterAll(Registry ffi) {
                    ffi.Register("print", () => "");
                    ffi.Register(@"print", () => "");
                    ffi.Register("\u0070rint", () => "");
                    ffi.Register("unapproved", () => "", "print");
                    ffi.Register(name, () => "");
                    // ffi.Register("unapproved", () => "");
                    var decoy = "ffi.Register(\"unapproved\")";
                    other.Register("unapproved", () => "");
                }
            }
            """, "csharp", [6, 7],
            "Unapproved runtime function; use a .cop package or get explicit approval");
    }

    [TestCase("tests/lang.tests/lang/EvaluatorTests.cs", false)]
    [TestCase("tests/lang.tests/lang/OtherTests.cs", false)]
    [TestCase("cop/shared/interpreter/StandardLibrary.cs", true)]
    [TestCase("src/Input.cs", true)]
    [TestCase("samples/Input.cs", true)]
    [TestCase("examples/Input.cs", true)]
    public async Task FfiCheckExemptsTestsButStillChecksOtherSources(string file, bool hasViolations)
    {
        await AssertCheck(Path.Combine("csharp", "no-new-ffi.cop"), "no-new-ffi", file, """
            class Example {
                void EvalForeignFunction(Registry ffi) {
                    ffi.Register("double", () => 0);
                    ffi.Register("unexpected", () => 0);
                }
                void OtherTest(Registry ffi) {
                    ffi.Register("double", () => 0);
                }
            }
            """, "csharp", hasViolations ? [3, 4, 7] : [],
            "Unapproved runtime function; use a .cop package or get explicit approval");
    }

    [TestCase("src/Input.cop", true)]
    [TestCase("tests/Input.cop", false)]
    public async Task FfiCheckPreservesCopCallCoverage(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "no-new-ffi.cop"), "no-new-ffi", file,
            "let registration = ffi.Register('unexpected')", "cop", hasViolation ? [1] : [],
            "Unapproved runtime function; use a .cop package or get explicit approval");
    }

    [Test]
    public async Task Issue054_NullableCheckUsesActiveParsedDirectives()
    {
        await AssertCheck(Path.Combine("csharp", "derived", "no-nullable-disable.cop"), "derived-no-nullable-disable", "Input.cs", """
            #nullable enable
            // #nullable disable
            class Example { string Decoy = "#nullable disable"; }
            #nullable   disable
            #nullable restore
            #nullable disable warnings
            #nullable enable warnings
            #nullable disable annotations
            #if false
            #nullable disable
            #endif
            """, "csharp", [4, 6, 8],
            "Do not disable nullable reference types (Input.cs:{0})");
    }

    [TestCase("dotnet")]
    [TestCase("checks/csharp-checks")]
    [TestCase("checks/javascript-checks")]
    [TestCase("checks/python-checks")]
    [TestCase("checks/java-checks")]
    [TestCase("checks/go-checks")]
    [TestCase("checks/rust-checks")]
    public async Task Issue054_PackageCheckUsesCopCallAndMemberNodesIncludingCachedRuns(string package)
    {
        await AssertCheck(Path.Combine("cop", "packages", "language-checks-require-parser.cop"),
            "lang-provider-hardcoded-violations", Path.Combine(package, "Input.cop"), """
            # codebase(Program.Providers)
            let decoy = 'codebase(Program.Providers)'
            let a = codebase ( Program.Providers )
            let b = codebase(
                Program.Providers)
            let good = codebase(csharp.parse())
            let different = codebase(Other.Providers)
            let other = helper(Program.Providers)
            """, "cop", [3, 4],
            "Use codebase(<lang>.parse()), not codebase(Program.Providers)");
    }

    [TestCase("src/Input.cs", true)]
    [TestCase("tests/Input.cs", false)]
    [TestCase("samples/Input.cs", false)]
    [TestCase("examples/Input.cs", true)]
    public async Task NamespaceCheckPreservesItsFixtureExemptions(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "types-require-namespaces.cop"), "types-without-namespace", file,
            "class Example { }", "csharp", hasViolation ? [1] : [],
            $"Example in {file} must be in a namespace");
    }

    [TestCase("src/Input.cs", true)]
    [TestCase("tests/Input.cs", false)]
    [TestCase("samples/Input.cs", false)]
    [TestCase("examples/Input.cs", false)]
    public async Task InterfaceCheckExemptsTestsSamplesAndExamples(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "no-interfaces.cop"), "no-new-interfaces", file,
            "namespace Example; interface IUnexpected { }", "csharp", hasViolation ? [1] : [],
            $"Use an abstract class instead of interface IUnexpected ({file})");
    }

    [TestCase("[]", new[] { 2, 3, 6 })]
    [TestCase("['IAllowed']", new[] { 3, 6 })]
    [TestCase("['iallowed', 'IFirst', 'ISecond']", new int[] { })]
    public async Task InterfaceQueryUsesCallerScopeAllowlistAndDiagnostic(string allowed, int[] lines)
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Input.cs"), """
            namespace Fixture;
            interface IAllowed { }
            interface IFirst { }
            class Ordinary { }
            class Container { }
            interface ISecond { }
            interface IExcluded { }
            """);
        File.WriteAllText(Path.Combine(_root, "main.cop"), $$"""
            import code
            import csharp
            let cb = codebase(csharp.parse())
            let selected = cb.Types.Where(item.Name != 'IExcluded')
            let rejected = code.interfacesExcept(selected, {{allowed}})
                + code.interfacesExcept([], [])
            command MAIN = CHECK(rejected:toError('Caller policy rejects {item.Name}', ''))
            """);
        var names = new Dictionary<int, string> { [2] = "IAllowed", [3] = "IFirst", [6] = "ISecond" };
        var expected = lines.Select(line =>
            $"Input.cs({line}): error: Caller policy rejects {names[line]}");
        for (int run = 0; run < 2; run++)
        {
            var result = (await Engine.RunAsync(_root, target, new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
            }));

            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(expected));
            Assert.That(result.ExitCode, Is.EqualTo(lines.Length));
        }
    }

    [TestCase("src/Input.cs", true)]
    [TestCase("tests/Input.cs", false)]
    [TestCase("samples/Input.cs", false)]
    [TestCase("examples/Input.cs", true)]
    public async Task ExceptionChecksOnlyExemptTestsAndSamples(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "derived", "exception-naming.cop"),
            "derived-exception-naming", file, "namespace Fixture; class Failure : Exception { }",
            "csharp", hasViolation ? [1] : [],
            $"Failure derives from Exception but is not named *Exception ({file})");
        await AssertCheck(Path.Combine("csharp", "derived", "exception-suffix-implies-base.cop"),
            "derived-exception-suffix-implies-base", file, "namespace Fixture; class FailureException { }",
            "csharp", hasViolation ? [1] : [],
            "FailureException is named *Exception but does not derive from Exception");
    }

    [Test]
    public async Task SharedFileScopesPreserveDistinctExemptions()
    {
        var scripts = Path.Combine(_root, "cop-checks");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(scripts);
        foreach (var folder in new[] { "src", "tests", "samples", "examples" })
        {
            Directory.CreateDirectory(Path.Combine(target, folder));
            File.WriteAllText(Path.Combine(target, folder, "Input.cs"),
                """namespace Fixture; class Example { void Run() { System.Console.WriteLine("example"); } }""");
        }
        File.WriteAllText(Path.Combine(target, "src", "Input.cop"), "let example = print('example')");
        var program = Path.Combine(scripts, "main.cop");
        File.WriteAllText(program, """
            command FILE_SCOPES =
                print('{source-files.Path.OrderBy(item)}') &
                print('{source-files-without-examples.Path.OrderBy(item)}') &
                print('{non-test-files.Path.OrderBy(item)}') &
                print('{files-without-samples-or-examples.Path.OrderBy(item)}') &
                print('{source-calls.File.Path.OrderBy(item)}') &
                print('{non-test-calls.File.Path.OrderBy(item)}')
            """);
        var checks = Path.Combine(_repo, "cop-checks");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Append(program).ToArray();
        var result = (await Engine.RunAsync(checks, target, new EngineRunOptions
        {
            CommandName = "FILE_SCOPES",
            ScriptFiles = files,
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
        }));

        Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.ExitCode, Is.Null);
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
        {
            "[examples/Input.cs, src/Input.cop, src/Input.cs]",
            "[src/Input.cop, src/Input.cs]",
            "[examples/Input.cs, samples/Input.cs, src/Input.cop, src/Input.cs]",
            "[src/Input.cop, src/Input.cs, tests/Input.cs]",
            "[examples/Input.cs, src/Input.cs]",
            "[examples/Input.cs, samples/Input.cs, src/Input.cop, src/Input.cs]"
        }));
    }

    [TestCase("src/Input.cs", true)]
    [TestCase("tests/Input.cs", false)]
    [TestCase("samples/Input.cs", true)]
    [TestCase("examples/Input.cs", true)]
    public async Task FixtureLocationCheckOnlyExemptsTests(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "derived", "test-fixtures-under-tests.cop"),
            "derived-tests-location", file, "using NUnit.Framework; namespace Fixture; [TestFixture] class Example { }",
            "csharp", hasViolation ? [1] : [],
            $"Test fixture Example must live under tests/ ({file})");
    }

    [TestCase("src/Input.cs", true)]
    [TestCase("tests/Input.cs", false)]
    [TestCase("samples/Input.cs", false)]
    [TestCase("examples/Input.cs", true)]
    public async Task EnvironmentCheckOnlyExemptsTestsAndSamples(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", file,
            "class Example { string? Read() => Environment.GetEnvironmentVariable(\"MODE\"); }",
            "csharp", hasViolation ? [1] : [],
            "Use CLI flags or config instead of environment variables for Cop settings");
    }

    [TestCase("src/Input.cs", true)]
    [TestCase("tests/Input.cs", true)]
    [TestCase("samples/Input.cs", false)]
    [TestCase("examples/Input.cs", false)]
    public async Task ProviderNamespaceCheckOnlyExemptsSamplesAndExamples(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "derived", "provider-namespace.cop"),
            "derived-provider-namespace", file, "namespace Other; class ExampleProvider : DataProvider { }",
            "csharp", hasViolation ? [1] : [],
            "ExampleProvider: namespace must start with Cop.Providers, not Other");
    }

    [TestCase("cop/cli/cop.csproj", true)]
    [TestCase("providers/example/Example.csproj", false)]
    public async Task AspNetFrameworkCheckOnlyAppliesToCliProjects(string file, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "cli-no-aspnet-framework.cop"), "no-aspnet-in-cli", file, """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
              <ItemGroup><FrameworkReference Include="Microsoft.AspNetCore.App" /></ItemGroup>
            </Project>
            """, "csharp", hasViolation ? [0] : [],
            $"Project {Path.GetFileNameWithoutExtension(file)}: " +
            "remove Microsoft.AspNetCore.App for single-file publishing");
    }

    [Test]
    public async Task RegexCheckPreservesInfrastructureNameExemptions()
    {
        await AssertCheck(Path.Combine("csharp", "no-regex.cop"), "no-regex", "Input.cs", """
            using System.Text.RegularExpressions;
            namespace Fixture;
            class Unexpected { }
            class RegexCache { }
            class FilterCompiler { }
            class FilterEvaluator { }
            class StandardLibrary { }
            class CustomRegexCacheWrapper { }
            """, "csharp", [3],
            "Unexpected: use parsers instead of System.Text.RegularExpressions");
    }

    [Test]
    public async Task Issue103_NamedDiagnosticTemplateRendersEveryType()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Input.cs"), """
            namespace Fixture;
            class Alpha { }
            class Beta { }
            """);
        File.WriteAllText(Path.Combine(_root, "main.cop"), """
            import code
            import csharp
            let codebase = codebase(csharp.parse())
            let message = 'Type {item.Name} in {item.File.Path}'
            command MAIN = CHECK(codebase.Types:toError(message))
            """);

        var result = (await Engine.RunAsync(_root, target, new EngineRunOptions
        {
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
        }));

        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
        {
            "Input.cs(2): error: Type Alpha in Input.cs",
            "Input.cs(3): error: Type Beta in Input.cs"
        }));
        Assert.That(result.ExitCode, Is.EqualTo(2));
    }

    [Test]
    public async Task RegexCheckUsesSharedCodebaseSelection()
    {
        var target = Path.Combine(_root, "target");
        var selected = Path.Combine(target, "selected");
        Directory.CreateDirectory(selected);
        File.WriteAllText(Path.Combine(selected, "Included.cs"), """
            using System.Text.RegularExpressions;
            namespace Fixture;
            class Included { }
            """);
        File.WriteAllText(Path.Combine(target, "Excluded.cs"), """
            using System.Text.RegularExpressions;
            namespace Fixture;
            class Excluded { }
            """);
        File.Copy(Path.Combine(_repo, "cop-checks", "csharp", "no-regex.cop"), Path.Combine(_root, "rule.cop"));
        File.WriteAllText(Path.Combine(_root, "main.cop"), """
            import code
            import csharp
            let codebase = codebase(csharp.parse('selected'))
            command MAIN = CHECK(no-regex)
            """);

        var result = (await Engine.RunAsync(_root, target, new EngineRunOptions
        {
            AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
        }));

        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
        {
            "Included.cs(3): error: Included: use parsers instead of System.Text.RegularExpressions"
        }));
        Assert.That(result.ExitCode, Is.EqualTo(1));
    }

    [TestCase("providers/java-provider/src/JavaSemanticContext.cs", "Discover", false)]
    [TestCase("providers/rust-provider/src/RustFrontend.cs", "Bind", true)]
    public async Task FrontendEnvironmentExemptionsRequireExactlyOneArgument(
        string file, string method, bool allowsComputedName)
    {
        await AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", file, $$"""
            using System;
            namespace Fixture;
            class Example {
                string? {{method}}(string name) {
                    var missing = Environment.GetEnvironmentVariable();
                    var javaOverload = Environment.GetEnvironmentVariable("CLASSPATH", EnvironmentVariableTarget.Process);
                    var rustOverload = Environment.GetEnvironmentVariable("CARGO_HOME", EnvironmentVariableTarget.Process);
                    return Environment.GetEnvironmentVariable(name);
                }
            }
            """, "csharp", allowsComputedName ? [5, 6, 7] : [5, 6, 7, 8],
            "Use CLI flags or config instead of environment variables for Cop settings");
    }

    [TestCase("Cop.Providers", false)]
    [TestCase("Cop.Providers.Example", false)]
    [TestCase("Cop.ProvidersExtra", false)]
    [TestCase("Other", true)]
    public async Task ProviderNamespaceCheckPreservesPrefixMatching(string ns, bool hasViolation)
    {
        await AssertCheck(Path.Combine("csharp", "derived", "provider-namespace.cop"),
            "derived-provider-namespace", "Input.cs",
            $"namespace {ns}; class SampleProvider : DataProvider {{ }}",
            "csharp", hasViolation ? [1] : [],
            $"SampleProvider: namespace must start with Cop.Providers, not {ns}");
    }

    [TestCase(null, false)]
    [TestCase("net10.0", true)]
    [TestCase("net10.0;net9.0", true)]
    public async Task MultiTargetingCheckRejectsNonblankTargetFrameworksProperties(string? value, bool hasViolation)
    {
        var setting = value is null ? "" : $"<TargetFrameworks>{value}</TargetFrameworks>";
        await AssertCheck(Path.Combine("csharp", "derived", "single-target-framework.cop"),
            "derived-single-target-framework", "Input.csproj",
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{setting}</PropertyGroup></Project>",
            "csharp", hasViolation ? [0] : [],
            "Project Input: use TargetFramework, not TargetFrameworks");
    }

    [Test]
    [Explicit("Issue #101: empty project properties are omitted from the parsed model — remove when fixed")]
    [Category("PendingFix")]
    public async Task Issue101_EmptyTargetFrameworksPropertyRemainsVisibleToStructuralChecks()
    {
        await AssertCheck(Path.Combine("csharp", "derived", "single-target-framework.cop"),
            "derived-single-target-framework", "Input.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFrameworks /></PropertyGroup></Project>",
            "csharp", [0],
            "Project Input: use TargetFramework, not TargetFrameworks");
    }

    [TestCase("implicit-usings.cop", "derived-implicit-usings", "ImplicitUsings", "enable", "disable",
        "Project Input must set <ImplicitUsings>enable</ImplicitUsings>")]
    [TestCase("nullable-enabled.cop", "derived-nullable-enabled", "Nullable", "enable", "disable",
        "Project Input must set <Nullable>enable</Nullable>")]
    [TestCase("target-framework.cop", "derived-target-framework", "TargetFramework", "net10.0", "net9.0",
        "Project Input must target net10.0")]
    public async Task ProjectSettingChecksPreserveExplicitValueAndMissingSettingBehavior(
        string rule, string binding, string property, string allowed, string forbidden, string message)
    {
        var scripts = Path.Combine(_root, "cop-checks");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        File.Copy(Path.Combine(_repo, "cop-checks", "csharp", "derived", rule), Path.Combine(scripts, "rule.cop"));
        File.WriteAllText(Path.Combine(scripts, "main.cop"), $"""
            import code
            import csharp
            let codebase = codebase(csharp.parse())
            command MAIN = CHECK({binding})
            """);

        foreach (var value in new[] { forbidden, allowed, null })
        {
            var setting = value is null ? "" : $"<{property}>{value}</{property}>";
            File.WriteAllText(Path.Combine(target, "Input.csproj"),
                $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{setting}</PropertyGroup></Project>");
            var result = (await Engine.RunAsync(scripts, target, new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
            }));

            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(value == forbidden
                ? new[] { $"Input.csproj(0): error: {message}" }
                : Array.Empty<string>()), $"Setting: {value ?? "(missing)"}");
            Assert.That(result.ExitCode, Is.EqualTo(value == forbidden ? 1 : 0));
        }
    }

    private async Task AssertCheck(string check, string binding, string file, string source,
        string language, int[] lines, string message)
    {
        var scripts = Path.Combine(_root, "cop-checks");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, file))!);
        File.WriteAllText(Path.Combine(target, file), source);
        File.WriteAllText(Path.Combine(scripts, "main.cop"), $"""
            import code
            import {language}
            command SELF_CHECK = CHECK({binding})
            """);
        var checks = Path.Combine(_repo, "cop-checks");
        var main = Path.Combine(checks, "main.cop");
        var rule = Path.Combine(checks, check);
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Where(f => f != main && f != rule).Order(StringComparer.Ordinal)
            .Append(rule).Append(main).Append(Path.Combine(scripts, "main.cop")).ToArray();
        var path = file.Replace('\\', '/');
        var expected = lines.Select(line =>
            $"{path}({line}): error: {string.Format(message, line)}").ToArray();
        for (int run = 0; run < 2; run++)
        {
            var result = (await Engine.RunAsync(checks, target, new EngineRunOptions
            {
                CommandName = "SELF_CHECK",
                ScriptFiles = files,
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
            }));
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(expected), $"Run {run + 1}");
        }
    }
}
