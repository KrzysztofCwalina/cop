using Cop.Cli.Commands;
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
    public void Issue054_AllSelfChecksPassStrictStructureWithoutExceptions()
    {
        var checks = Path.Combine(_repo, "cop-checks");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories);
        Assert.That(files, Has.Length.EqualTo(41));
        var diagnostics = VerifyCommand.CollectDiagnostics(files, checks, File.ReadAllText, strictStructure: true);
        Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics.Select(d => d.Message)));
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
    public void GroupedSelfChecks_RunCopAndCSharpRulesThroughRootMain()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Wrong.cop"),
            "export function misplaced(value : string) : string => intrinsic");
        File.WriteAllText(Path.Combine(target, "Sample.cs"),
            "namespace Fixture; public interface IUnexpected { }");
        var checks = Path.Combine(_repo, "cop-checks");
        var main = Path.Combine(checks, "main.cop");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Where(f => f != main).Order(StringComparer.Ordinal).Append(main).ToArray();
        var result = Engine.Run(checks, target, scriptFiles: files,
            additionalFeedPaths: [Path.Combine(_repo, "packages")]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
        {
            "Wrong.cop(1): error: Intrinsic declaration must be in intrinsics.cop, not Wrong.cop",
            "Sample.cs(1): error: Do not add new interfaces — use abstract classes instead: IUnexpected in Sample.cs"
        }));
    }

    [Test]
    public void RepositoryPassesDefaultSelfChecks()
    {
        var checks = Path.Combine(_repo, "cop-checks");
        var main = Path.Combine(checks, "main.cop");
        var files = Directory.GetFiles(checks, "*.cop", SearchOption.AllDirectories)
            .Where(f => f != main).Order(StringComparer.Ordinal).Append(main).ToArray();
        var result = Engine.Run(checks, _repo, scriptFiles: files,
            additionalFeedPaths: [Path.Combine(_repo, "packages")]);

        Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.Empty);
        Assert.That(result.ExitCode, Is.EqualTo(0));
    }

    [TestCase(false)]
    [TestCase(true)]
    public void SelfChecksHandleLegacyNegativeFixtureNamesWithoutChangingTheTarget(bool hasViolation)
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
            var result = Engine.Run(checks, target, scriptFiles: files,
                additionalFeedPaths: [Path.Combine(_repo, "packages")]);

            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Warnings!.Where(w => w.Contains("source analysis error")), Is.Empty);
            Assert.That(result.Warnings!.Count(w => w.Contains("Excluded negative parser fixture")), Is.EqualTo(2));
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
    public void RepositoryHasNoUnapprovedRegexImports()
    {
        File.Copy(Path.Combine(_repo, "cop-checks", "csharp", "no-regex.cop"), Path.Combine(_root, "rule.cop"));
        File.WriteAllText(Path.Combine(_root, "main.cop"), "command MAIN = CHECK(no-regex)");
        var result = Engine.Run(_root, _repo, additionalFeedPaths: [Path.Combine(_repo, "packages")]);

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
    public void Issue054_EnvironmentCheckUsesCallsAndOnlyTheActualVariableName()
    {
        AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", "Input.cs", """
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
            "Environment variable used to control behavior — use a CLI flag or config instead (Input.cs:{0})");
    }

    [Test]
    public void EnvironmentCheckAllowsOnlyLiteralSearchPathReads()
    {
        AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", "Input.cs", """
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
            "Environment variable used to control behavior — use a CLI flag or config instead (Input.cs:{0})");
    }

    [TestCase("cop/cli/commands/CopInstallation.cs", true)]
    [TestCase("cop/cli/commands/Other.cs", false)]
    public void EnvironmentCheckLimitsPathUpdatesToTheInstaller(string file, bool isInstaller)
    {
        AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", file, """
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
            $"Environment variable used to control behavior — use a CLI flag or config instead ({file}:{{0}})");
    }

    [TestCase("providers/java-provider/src/JavaSemanticContext.cs", "Discover", "CLASSPATH", false, true)]
    [TestCase("providers/rust-provider/src/RustFrontend.cs", "Bind", "CARGO_HOME", true, true)]
    [TestCase("providers/rust-provider/src/Other.cs", "Bind", "CARGO_HOME", false, false)]
    public void EnvironmentCheckLimitsReviewedFrontendContextToItsOwner(
        string file, string method, string variable, bool allowsComputed, bool allowsLiteral)
    {
        int[] violations = allowsLiteral
            ? allowsComputed ? [6, 8, 11] : [6, 7, 8, 11]
            : [5, 6, 7, 8, 11];
        AssertCheck(Path.Combine("csharp", "no-environment.cop"), "no-environment", file, $$"""
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
            $"Environment variable used to control behavior — use a CLI flag or config instead ({file}:{{0}})");
    }

    [Test]
    public void Issue054_FfiApprovalUsesTheDecodedFirstArgumentOnly()
    {
        AssertCheck(Path.Combine("csharp", "no-new-ffi.cop"), "no-new-ffi", "Input.cs", """
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
            "New runtime FFI registered without approval. The FFI surface must not grow — implement the capability as a .cop package, or get sign-off and add its name to approved-ffi-names.");
    }

    [TestCase("tests/lang.tests/lang/EvaluatorTests.cs", true)]
    [TestCase("tests/lang.tests/lang/OtherTests.cs", false)]
    [TestCase("cop/shared/interpreter/StandardLibrary.cs", false)]
    public void FfiExceptionOnlyAllowsTheInvocationTest(string file, bool allowsInvocationTest)
    {
        AssertCheck(Path.Combine("csharp", "no-new-ffi.cop"), "no-new-ffi", file, """
            class Example {
                void EvalForeignFunction(Registry ffi) {
                    ffi.Register("double", () => 0);
                    ffi.Register("unexpected", () => 0);
                }
                void OtherTest(Registry ffi) {
                    ffi.Register("double", () => 0);
                }
            }
            """, "csharp", allowsInvocationTest ? [4, 7] : [3, 4, 7],
            "New runtime FFI registered without approval. The FFI surface must not grow — implement the capability as a .cop package, or get sign-off and add its name to approved-ffi-names.");
    }

    [Test]
    public void Issue054_NullableCheckUsesActiveParsedDirectives()
    {
        AssertCheck(Path.Combine("csharp", "derived", "no-nullable-disable.cop"), "derived-no-nullable-disable", "Input.cs", """
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
    public void Issue054_PackageCheckUsesCopCallAndMemberNodesIncludingCachedRuns(string package)
    {
        AssertCheck(Path.Combine("cop", "packages", "lang-provider-hardcoded.cop"),
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
            "Language-specific package must hardcode its provider via codebase(<lang>.parse()), not codebase(Program.Providers), so it works without -p");
    }

    private void AssertCheck(string check, string binding, string file, string source,
        string language, int[] lines, string message)
    {
        var scripts = Path.Combine(_root, "cop-checks");
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(target, file))!);
        File.WriteAllText(Path.Combine(target, file), source);
        File.Copy(Path.Combine(_repo, "cop-checks", check), Path.Combine(scripts, "rule.cop"));
        File.WriteAllText(Path.Combine(scripts, "main.cop"), $"""
            import code
            import {language}
            let codebase = codebase({language}.parse())
            command MAIN = CHECK({binding})
            """);
        var path = file.Replace('\\', '/');
        var expected = lines.Select(line =>
            $"{path}({line}): error: {string.Format(message, line)}").ToArray();
        for (int run = 0; run < 2; run++)
        {
            var result = Engine.Run(scripts, target, additionalFeedPaths: [Path.Combine(_repo, "packages")]);
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(expected), $"Run {run + 1}");
        }
    }
}
