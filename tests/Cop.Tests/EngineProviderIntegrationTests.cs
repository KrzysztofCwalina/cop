using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class EngineProviderIntegrationTests
{
    private static string RepoRoot => FindRepoRoot();
    private static string PackagesDir => Path.Combine(RepoRoot, "packages");
    private static string BehaviorRoot => Path.Combine(RepoRoot, "tests", "behavior", "model");

    [Test]
    public void CheckHonorsPredicatesAndViolationTemplates()
    {
        // Issue #25
        var fixture = CreateFixture(nameof(CheckHonorsPredicatesAndViolationTemplates));
        try
        {
            WriteFile(fixture.TargetDir, "CheckTemplate.cs", """
                namespace Integration;
                using System;

                public class CheckTemplate
                {
                    public void Known()
                    {
                        Console.WriteLine("flagged");
                        string notFlagged = "safe";
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import code
                import csharp

                let cb = csharp.parse()
                predicate isWriteLine(Statement) => Statement.MemberName == 'WriteLine'

                let violations = cb.Statements:isWriteLine
                    :toWarning('templated {item.MemberName} at line {item.Line}')

                command MAIN = CHECK(violations)
                """);

            Assert.That(messages, Is.EqualTo(new[] { "CheckTemplate.cs(8): warning: templated WriteLine at line 8" }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    [Explicit("Issue #18: explicit csharp.parse() returns no model items when another language file is present — remove when fixed")]
    [Category("PendingFix")]
    public void ExplicitCSharpProvider_DoesNotLeakOtherLanguageModelItems()
    {
        // Issue #18
        var fixture = CreateFixture(nameof(ExplicitCSharpProvider_DoesNotLeakOtherLanguageModelItems));
        try
        {
            WriteFile(fixture.TargetDir, "OnlyCSharp.cs", """
                namespace Integration;
                public class OnlyCSharp
                {
                    public void M() { }
                }
                """);
            WriteFile(fixture.TargetDir, "python_file.py", """
                class PythonType:
                    pass
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()

                command MAIN =
                    foreach cb.Files => 'file:{item.Path}:{item.Language}' |
                    foreach cb.Types => 'type:{item.Name}:{item.File.Language}' |
                    foreach cb.Lines => 'line:{item.File.Path}:{item.File.Language}'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "file:OnlyCSharp.cs:csharp",
                "type:OnlyCSharp:csharp",
                "line:OnlyCSharp.cs:csharp",
                "line:OnlyCSharp.cs:csharp",
                "line:OnlyCSharp.cs:csharp",
                "line:OnlyCSharp.cs:csharp",
                "line:OnlyCSharp.cs:csharp"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    [Explicit("Issues #20/#47: csharp.parse() and parse('subdir') return no model items for a minimal path fixture — remove when fixed")]
    [Category("PendingFix")]
    public void CSharpParse_DefaultAndExplicitPathReturnUsableCodebases()
    {
        // Issues #20, #47
        var fixture = CreateFixture(nameof(CSharpParse_DefaultAndExplicitPathReturnUsableCodebases));
        try
        {
            WriteFile(Path.Combine(fixture.TargetDir, "subdir"), "SubType.cs", """
                namespace Integration.Subdir;
                public class SubType
                {
                    public void M() { }
                }
                """);
            var messages = RunCop(fixture, """
                import code
                import csharp

                let all = csharp.parse()
                let sub = csharp.parse('subdir')

                command MAIN =
                    foreach all.Types => 'all:{item.Name}:{item.File.Path}' |
                    foreach sub.Types => 'sub:{item.Name}:{item.File.Path}'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "all:SubType:subdir/SubType.cs",
                "sub:SubType:subdir/SubType.cs"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    [Explicit("Issue #33: object.Get returns a fatal error for Cop object literals — remove when fixed")]
    [Category("PendingFix")]
    public void CustomPredicateOverDynamicObjects_ReturnsCorrectMatches()
    {
        // Issue #33
        var fixture = CreateFixture(nameof(CustomPredicateOverDynamicObjects_ReturnsCorrectMatches));
        try
        {
            var messages = RunCop(fixture, """
                let items = [
                    { Name = 'alpha' Enabled = true }
                    { Name = 'beta' Enabled = false }
                    { Name = 'gamma' Enabled = true }
                ]

                predicate isEnabled(object) => object.Get('Enabled') == true

                command MAIN = foreach items:isEnabled => 'enabled:{item.Get('Name')}'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "enabled:alpha",
                "enabled:gamma"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    public void ThirdLanguageConstrainedPredicateOverload_DispatchesEarlierOverloads()
    {
        // Issue #35
        var fixture = CreateFixture(nameof(ThirdLanguageConstrainedPredicateOverload_DispatchesEarlierOverloads));
        try
        {
            WriteFile(fixture.TargetDir, "CSharpType.cs", """
                namespace Integration;
                public class CSharpType { }
                """);
            WriteFile(fixture.TargetDir, "python_type.py", """
                class PythonType:
                    pass
                """);
            WriteFile(fixture.TargetDir, "javascript_type.js", """
                class JavaScriptType {
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp
                import python
                import javascript

                let cb = codebase(csharp.parse(), python.parse(), javascript.parse())

                predicate selected(Type:isCSharp) => Type.Name == 'CSharpType'
                predicate selected(Type:isPython) => Type.Name == 'PythonType'
                predicate selected(Type:isJavaScript) => Type.Name == 'JavaScriptType'

                command MAIN = foreach cb.Types:selected => 'selected:{item.Name}:{item.File.Language}'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "selected:CSharpType:csharp",
                "selected:PythonType:python",
                "selected:JavaScriptType:javascript"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    public void ReasonablyNamedPredicate_ProducesExpectedNonZeroResult()
    {
        // Issue #22
        var fixture = CreateFixture(nameof(ReasonablyNamedPredicate_ProducesExpectedNonZeroResult));
        try
        {
            WriteFile(fixture.TargetDir, "RegexFixture.cs", """
                namespace Integration;
                using System.Text.RegularExpressions;

                public class RegexFixture
                {
                    public bool Known(string value)
                    {
                        return Regex.IsMatch(value, "^a+$");
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate usesRegex(Statement) => Statement.MemberName == 'IsMatch'

                command MAIN = foreach cb.Statements:usesRegex => 'regex:{item.MemberName}:line={item.Line}'
                """);

            Assert.That(messages, Is.EqualTo(new[] { "regex:IsMatch:line=8" }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    private static Fixture CreateFixture(string testName)
    {
        var root = Path.Combine(BehaviorRoot, Sanitize(testName) + "-" + Guid.NewGuid().ToString("N")[..8]);
        var scriptsDir = Path.Combine(root, "checks");
        var targetDir = Path.Combine(root, "target");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(targetDir);
        return new Fixture(root, scriptsDir, targetDir);
    }

    private static IReadOnlyList<string> RunCop(Fixture fixture, string copSource)
    {
        File.WriteAllText(Path.Combine(fixture.ScriptsDir, "main.cop"), copSource);
        var result = Engine.Run(fixture.ScriptsDir, fixture.TargetDir, additionalFeedPaths: [PackagesDir]);

        Assert.That(result.HasParseErrors, Is.False, "Parse errors: " + string.Join(Environment.NewLine, result.ParseErrors));
        Assert.That(result.HasFatalErrors, Is.False, "Fatal errors: " + string.Join(Environment.NewLine, result.Errors));
        Assert.That(result.Errors, Is.Empty, "Errors: " + string.Join(Environment.NewLine, result.Errors));

        return result.Outputs.Select(output => output.Message).ToArray();
    }

    private static void WriteFile(string directory, string relativePath, string content)
    {
        var path = Path.Combine(directory, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Normalize(content));
    }

    private static string Normalize(string value) => value.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);

        return dir ?? throw new InvalidOperationException("Could not find repo root containing cop.sln.");
    }

    private static string Sanitize(string value) =>
        new(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray());

    private sealed record Fixture(string Root, string ScriptsDir, string TargetDir)
    {
        public void Delete()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
