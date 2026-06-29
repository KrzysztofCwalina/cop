using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class CodebaseModelPopulationTests
{
    private static string RepoRoot => FindRepoRoot();
    private static string PackagesDir => Path.Combine(RepoRoot, "packages");
    private static string BehaviorRoot => Path.Combine(RepoRoot, "tests", "behavior", "model");

    [Test]
    [Explicit("Issue #24: Method.Statements undercounts a known four-statement method — remove when fixed")]
    [Category("PendingFix")]
    public void MethodStatements_HasExactKnownCount()
    {
        // Issue #24
        var fixture = CreateFixture(nameof(MethodStatements_HasExactKnownCount));
        try
        {
            WriteFile(fixture.TargetDir, "Sample.cs", """
                namespace ModelPopulation;
                using System;

                public class Sample
                {
                    public void Known()
                    {
                        int value = 0;
                        Console.WriteLine(value);
                        value++;
                        Console.WriteLine(value + 1);
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isKnown(Method) => Method.Name == 'Known'

                command MAIN = foreach cb.Methods:isKnown => '{item.Name}:{item.Statements.Count}'
                """);

            Assert.That(messages, Is.EqualTo(new[] { "Known:4" }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    public void StatementTree_ControlFlowFieldsArePopulated()
    {
        // Issue #15
        var fixture = CreateFixture(nameof(StatementTree_ControlFlowFieldsArePopulated));
        try
        {
            WriteFile(fixture.TargetDir, "ControlFlow.cs", """
                namespace ModelPopulation;
                using System;

                public class ControlFlow
                {
                    public void Known()
                    {
                        if (DateTime.Now.Ticks > 0)
                        {
                            Console.WriteLine("if");
                        }

                        while (DateTime.Now.Ticks < 0)
                        {
                            Console.WriteLine("while");
                        }
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isKnownControl(Statement) =>
                    Statement.InMethod == true && Statement.Method.Name == 'Known' && Statement:isControlFlow

                command MAIN = foreach cb.Statements:isKnownControl => '{item.Kind}:braced={item.Braced}:children={item.Children.Count}:control=true'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "if:braced=true:children=1:control=true",
                "while:braced=true:children=1:control=true"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    [Explicit("Issue #31: nested control-flow fixture returns no method statements/calls — remove when fixed")]
    [Category("PendingFix")]
    public void NestedBlocks_DoNotDuplicateStatementsOrCalls()
    {
        // Issue #31
        var fixture = CreateFixture(nameof(NestedBlocks_DoNotDuplicateStatementsOrCalls));
        try
        {
            WriteFile(fixture.TargetDir, "Nested.cs", """
                namespace ModelPopulation;
                using System;

                public class Nested
                {
                    public void Known()
                    {
                        if (DateTime.Now.Ticks > 0)
                        {
                            if (DateTime.Now.Ticks < 0)
                            {
                                Console.WriteLine("inner");
                            }
                        }

                        Console.WriteLine("outer");
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isKnown(Method) => Method.Name == 'Known'
                predicate isKnownStatement(Statement) => Statement.InMethod == true && Statement.Method.Name == 'Known'

                command MAIN =
                    foreach cb.Methods:isKnown => 'statements={item.Statements.Count}' |
                    foreach cb.Calls:isKnownStatement => 'call:{item.MemberName}'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "statements=4",
                "call:WriteLine",
                "call:WriteLine"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    public void ProjectReferences_ArePopulatedWithKnownReferences()
    {
        // Issue #30
        var fixture = CreateFixture(nameof(ProjectReferences_ArePopulatedWithKnownReferences));
        try
        {
            WriteProjectFixture(fixture.TargetDir, targetFrameworkElement: "<TargetFramework>net10.0</TargetFramework>");

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isApp(Project) => Project.Name == 'App'

                command MAIN = foreach cb.Projects:isApp => 'project={item.Name}:refs={item.References.Count}:packages={item.Packages.Count}'
                """);

            Assert.That(messages, Is.EqualTo(new[] { "project=App:refs=1:packages=1" }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    public void ProjectFrameworks_IncludeSingleTargetFramework()
    {
        // Issue #48
        var fixture = CreateFixture(nameof(ProjectFrameworks_IncludeSingleTargetFramework));
        try
        {
            WriteProjectFixture(fixture.TargetDir, targetFrameworkElement: "<TargetFramework>net10.0</TargetFramework>");

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isApp(Project) => Project.Name == 'App'

                command MAIN = foreach cb.Projects:isApp => '{item.Name}:frameworks={item.Frameworks.Count}:{item.Frameworks}'
                """);

            Assert.That(messages, Is.EqualTo(new[] { "App:frameworks=1:[net10.0]" }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    [Explicit("Issue #3: Line/File fields and line predicates return no matches for a known C# fixture — remove when fixed")]
    [Category("PendingFix")]
    public void LineAndFileFields_AndPredicatesMatchKnownFixture()
    {
        // Issue #3
        var fixture = CreateFixture(nameof(LineAndFileFields_AndPredicatesMatchKnownFixture));
        try
        {
            WriteFile(fixture.TargetDir, "LineFile.cs", """
                using System;

                namespace ModelPopulation;
                // marker comment
                public class LineType
                {
                    public void M()
                    {
                        Console.WriteLine("x");
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isLineFile(File) => File.Path:endsWith('LineFile.cs')
                predicate inLineFile(Line) => Line.File.Path:endsWith('LineFile.cs')

                command MAIN =
                    foreach cb.Files:isLineFile => 'file={item.Path}:language={item.Language}:namespace={item.Namespace}:usings={item.Usings.Count}:types={item.Types.Count}' |
                    foreach cb.Lines:inLineFile:isBlank => 'blank={item.Number}:{item.Kind}' |
                    foreach cb.Lines:inLineFile:isComment => 'comment={item.Number}:{item.Kind}:{item.Text}' |
                    foreach cb.Lines:inLineFile:isCSharp => 'csharp-line={item.Number}'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "file=LineFile.cs:language=csharp:namespace=ModelPopulation:usings=1:types=1",
                "blank=2:blank",
                "comment=4:comment:// marker comment",
                "csharp-line=1",
                "csharp-line=2",
                "csharp-line=3",
                "csharp-line=4",
                "csharp-line=5",
                "csharp-line=6",
                "csharp-line=7",
                "csharp-line=8",
                "csharp-line=9"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    public void PredicateFilteringStatements_ReturnsExactNonZeroMatches()
    {
        // Issue #16
        var fixture = CreateFixture(nameof(PredicateFilteringStatements_ReturnsExactNonZeroMatches));
        try
        {
            WriteFile(fixture.TargetDir, "PredicateStatements.cs", """
                namespace ModelPopulation;
                using System;

                public class PredicateStatements
                {
                    public void Known()
                    {
                        Console.WriteLine("one");
                        Console.WriteLine("two");
                        string value = "not a call match";
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isWriteLine(Statement) => Statement.MemberName == 'WriteLine'

                command MAIN = foreach cb.Statements:isWriteLine => 'write-line:{item.Line}:{item.MemberName}'
                """);

            Assert.That(messages, Is.EqualTo(new[]
            {
                "write-line:8:WriteLine",
                "write-line:9:WriteLine"
            }));
        }
        finally
        {
            fixture.Delete();
        }
    }

    [Test]
    public void ParameterLine_IsPopulated()
    {
        // Regression: Parameter.Line was declared in code.cop and backed by a real CLR field,
        // but had no provider-schema entry or CLR accessor, so `parameter.Line` silently returned
        // null at runtime. It must return the actual source line.
        var fixture = CreateFixture(nameof(ParameterLine_IsPopulated));
        try
        {
            WriteFile(fixture.TargetDir, "Sample.cs", """
                namespace ModelPopulation;
                public class Sample
                {
                    public void Known(int value)
                    {
                    }
                }
                """);

            var messages = RunCop(fixture, """
                import code
                import csharp

                let cb = csharp.parse()
                predicate isKnown(Method) => Method.Name == 'Known'

                command MAIN = foreach cb.Methods:isKnown => 'param-line:{item.Parameters.First.Line}'
                """);

            // The method (and its `int value` parameter) is on line 4 of Sample.cs.
            Assert.That(messages, Is.EqualTo(new[] { "param-line:4" }));
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

    private static void WriteProjectFixture(string targetDir, string targetFrameworkElement)
    {
        Directory.CreateDirectory(Path.Combine(targetDir, "Lib"));
        Directory.CreateDirectory(Path.Combine(targetDir, "App"));

        WriteFile(Path.Combine(targetDir, "Lib"), "Library.cs", """
            namespace ModelPopulation.Lib;
            public class Library { }
            """);
        WriteFile(Path.Combine(targetDir, "Lib"), "Lib.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>{targetFrameworkElement}</PropertyGroup>
            </Project>
            """);

        WriteFile(Path.Combine(targetDir, "App"), "Program.cs", """
            namespace ModelPopulation.App;
            public class Program { }
            """);
        WriteFile(Path.Combine(targetDir, "App"), "App.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>{targetFrameworkElement}</PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Lib\Lib.csproj" />
                <PackageReference Include="NUnit" Version="4.0.0" />
              </ItemGroup>
            </Project>
            """);
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
