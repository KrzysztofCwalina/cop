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
    public void RepositoryHasNoUnapprovedRegexImports()
    {
        File.Copy(Path.Combine(_repo, "cop-checks", "no-regex.cop"), Path.Combine(_root, "rule.cop"));
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
        AssertCheck("no-environment.cop", "no-environment", "Input.cs", """
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
    public void Issue054_FfiApprovalUsesTheDecodedFirstArgumentOnly()
    {
        AssertCheck("no-new-ffi.cop", "no-new-ffi", "Input.cs", """
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
        AssertCheck("no-new-ffi.cop", "no-new-ffi", file, """
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
        AssertCheck(Path.Combine("derived", "no-nullable-disable.cop"), "derived-no-nullable-disable", "Input.cs", """
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
        AssertCheck(Path.Combine("packages", "lang-provider-hardcoded.cop"),
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
