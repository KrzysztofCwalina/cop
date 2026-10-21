using System.CommandLine;
using System.Text.Json;
using Cop.Cli.Commands;
using Cop.Lang;
using Cop.Lang.Parser;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public class StructuralCheckPolicyTests
{
    private const string Model = """
        type Sample = {
            Name : string,
            Source : string,
            Text : string,
            Condition : string,
            Expression : string,
            Arguments : [string],
            Parent : Sample?
        }

        """;

    private static IReadOnlyList<CopDiagnostic> Check(string source, string[]? exceptions = null, string? imported = null)
    {
        var files = new[] { (CopParser.Parse(source, "checks.cop"), "checks.cop", source) };
        var imports = imported is null ? [] :
            new[] { (CopParser.Parse(imported, "helper.cop"), "helper.cop", imported) };
        return StructuralCheckPolicy.Check(files, imports, exceptions ?? []);
    }

    [TestCase("Sample.Source:matches('class')", "Source")]
    [TestCase("Sample.Source:contains('class')", "Source")]
    [TestCase("Sample.Source == 'class'", "Source")]
    [TestCase("Sample.Text:startsWith('class')", "Text")]
    [TestCase("Sample.Condition == 'x'", "Condition")]
    [TestCase("Sample.Expression == 'x'", "Expression")]
    [TestCase("Sample.Arguments:contains('x')", "Arguments")]
    [TestCase("Sample.Parent.Source:contains('x')", "Source")]
    [TestCase("Sample['Source']:contains('x')", "Source")]
    [TestCase("Sample.Get('Source'):contains('x')", "Source")]
    [TestCase("read('source.cs'):contains('x')", "read()")]
    [TestCase("Source:contains('x')", "Source")]
    public void RejectsRawTextRegardlessOfOperation(string expression, string field)
    {
        var diagnostics = Check(Model + $"predicate bad(Sample) => {expression}\n");
        Assert.That(diagnostics, Has.Count.EqualTo(1));
        Assert.That(diagnostics[0].Severity, Is.EqualTo(CopDiagnosticSeverity.Error));
        Assert.That(diagnostics[0].Message, Does.Contain($"'{field}'").And.Contain("ASK THE USER"));
        Assert.That(diagnostics[0].FilePath, Is.EqualTo("checks.cop"));
        Assert.That(diagnostics[0].Line, Is.EqualTo(10));
    }

    [Test]
    public void AllowsStringComparisonsOnParsedFacts()
    {
        var diagnostics = Check(Model + """
            predicate isService(Sample) => Sample.Name:endsWith('Service')
            predicate sameName(Sample) => Sample.Parent.Name == Sample.Name
            """);
        Assert.That(diagnostics, Is.Empty);
    }

    [TestCase("Line:contains('class')")]
    [TestCase("text(Line):contains('class')")]
    [TestCase("'{Line}':contains('class')")]
    [TestCase("Line == 'class'")]
    public void RejectsImplicitSourceTextConversions(string expression)
    {
        Assert.That(Check("type Line = { Text : string }\n" +
            $"predicate bad(Line) => {expression}\n"), Has.Count.EqualTo(1));
    }

    [Test]
    public void RejectsTextConversionThroughAStringHelper()
    {
        Assert.That(Check("""
            type Line = { Text : string }
            function hasClass(value: string) : bool => value:contains('class')
            predicate bad(Line) => hasClass(Line)
            """), Has.Count.EqualTo(1));
    }

    [Test]
    public void FollowsAliasesAndCrossFileHelpers()
    {
        var diagnostics = Check(Model + """
            let samples : [Sample] = []
            let selected = samples:bad
            """, imported: "predicate bad(Sample) => Sample.Source:contains('class')\n");
        Assert.That(diagnostics, Has.Count.EqualTo(1));
        Assert.That(diagnostics[0].FilePath, Is.EqualTo("helper.cop"));
        Assert.That(diagnostics[0].Line, Is.EqualTo(1));
    }

    [Test]
    public void ChecksRawTextBeforeItCanBeHiddenInAnAlias()
    {
        var diagnostics = Check(Model + """
            let samples : [Sample] = []
            let texts = samples.Select(item.Source)
            let selected = texts:contains('class')
            """);
        Assert.That(diagnostics, Has.Count.EqualTo(2));
        Assert.That(diagnostics.Select(d => d.Message), Has.Some.Contains("'Source'"));
        Assert.That(diagnostics.Select(d => d.Message), Has.Some.Contains("unresolved text conversion"));
    }

    [Test]
    public void ChecksComputedProperties()
    {
        var diagnostics = Check("""
            type Sample = { Source : string, alias => item.Source }
            predicate bad(Sample) => Sample.alias == 'class'
            """);
        Assert.That(diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void AllowsDiagnosticExcerptsButNotSelectionBasedOnTheMessage()
    {
        var source = Model + """
            let samples : [Sample] = []
            let selected = samples:toError('Found {item.Source}')
            """;
        Assert.That(Check(source), Is.Empty);
        Assert.That(Check(source + "\nlet bad = selected.Where(item.Message:contains('class'))\n"), Has.Count.EqualTo(1));
    }

    [Test]
    public void AllowsDirectAndMethodDiagnosticMessages()
    {
        var source = Model + """
            let sample : Sample = { Name = 'A', Source = 'class', Text = '', Condition = '', Expression = '', Arguments = [] }
            let one = toError(sample, 'Found {sample.Source}')
            let two = sample.toError('Found {item.Source}')
            """;
        Assert.That(Check(source), Is.Empty);
    }

    [Test]
    public void DoesNotExemptSeverityOrCheckName()
    {
        var diagnostics = Check(Model + """
            let samples : [Sample] = []
            let bad = samples:toError('message', item.Source)
            """);
        Assert.That(diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void DoesNotTrustLocallyShadowedReportingFunctions()
    {
        var diagnostics = Check(Model + """
            function toError(value : string) : bool => value == 'class'
            predicate bad(Sample) => toError(Sample.Source)
            """);
        Assert.That(diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void NamedApprovalCoversPrivateHelpersButNotOtherCallers()
    {
        var source = Model + """
            let samples : [Sample] = []
            predicate bad(Sample) => Sample.Source:contains('class')
            let approved = samples:bad
            """;
        Assert.That(Check(source, ["approved"]), Is.Empty);
        Assert.That(Check(source + "\nlet unapproved = samples:bad\n", ["approved"]), Has.Count.EqualTo(1));
    }

    [Test]
    public void UnknownAndWildcardApprovalsAreErrors()
    {
        var diagnostics = Check("let count = 1\n", ["typo", "*"]);
        Assert.That(diagnostics, Has.Count.EqualTo(2));
        Assert.That(diagnostics.All(d => d.Message.StartsWith("Unknown text-check exception", StringComparison.Ordinal)), Is.True);
    }

    [Test]
    public void DoesNotScanUnusedImportedTextChecks()
    {
        Assert.That(Check("let count = 1\n", imported: Model +
            "predicate unused(Sample) => Sample.Source:contains('class')\n"), Is.Empty);
    }

    [Test]
    public void HandlesRecursiveHelpersWithoutHanging()
    {
        var diagnostics = Check(Model + """
            predicate first(Sample) => second(Sample)
            predicate second(Sample) => first(Sample) || Sample.Source == 'class'
            """);
        Assert.That(diagnostics, Has.Count.EqualTo(1));
    }

    [Test]
    public void VerifyCliStrictModeIsOptInAndEmitsJsonDiagnostics()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cop-structural-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "main.cop");
        File.WriteAllText(file, Model + "predicate bad(Sample) => Sample.Source == 'class'\n");
        var original = Console.Out;
        try
        {
            using var output = new StringWriter();
            Console.SetOut(output);
            Assert.That(VerifyCommand.Execute(file, json: true), Is.EqualTo(0));
            output.GetStringBuilder().Clear();
            var command = VerifyCommand.Create();
            Assert.That(command.Parse([file, "--strict-structure", "--json"]).Invoke(), Is.EqualTo(1));
            using (var json = JsonDocument.Parse(output.ToString()))
            {
                Assert.That(json.RootElement.GetProperty("errors").GetInt32(), Is.EqualTo(1));
                Assert.That(json.RootElement.GetProperty("diagnostics")[0].GetProperty("file").GetString(), Is.EqualTo(file));
            }
            output.GetStringBuilder().Clear();
            Assert.That(command.Parse([file, "--strict-structure", "--allow-text-check", "bad", "--json"]).Invoke(), Is.EqualTo(0));
            using var approved = JsonDocument.Parse(output.ToString());
            Assert.That(approved.RootElement.GetProperty("errors").GetInt32(), Is.EqualTo(0));
        }
        finally
        {
            Console.SetOut(original);
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void VerifyRejectsApprovalWithoutStrictMode()
    {
        Assert.That(VerifyCommand.Execute(null, allowTextChecks: ["bad"]), Is.EqualTo(1));
    }

    [Test]
    public void PackagedStructuralExampleVerifiesAndRunsWithoutMatchingTextDecoys()
    {
        var repo = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "cop.sln")))
            repo = repo.Parent;
        Assert.That(repo, Is.Not.Null);
        var packages = Path.Combine(repo!.FullName, "packages");
        var sample = File.ReadAllText(Path.Combine(packages, "core", "code", "samples", "check-nested-throws.cop"));
        var root = Path.Combine(Path.GetTempPath(), $"cop-structural-example-{Guid.NewGuid():N}");
        var scripts = Path.Combine(root, "cop-checks");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            var main = Path.Combine(scripts, "main.cop");
            File.WriteAllText(main, $"feed '{packages}'\n" + sample);
            File.WriteAllText(Path.Combine(target, "Example.cs"), """
                namespace Example;
                class Example
                {
                    void Throwing()
                    {
                        try { Work(); }
                        catch (System.Exception ex) { throw ex; }
                    }
                    void Logging()
                    {
                        try { Work(); }
                        catch (System.Exception) { Work(); }
                    }
                    string Decoy() => "catch (Exception ex) { throw ex; }";
                    // catch (Exception ex) { throw ex; }
                    void Outside() { throw new System.Exception(); }
                    void Work() { }
                }
                """);
            var diagnostics = VerifyCommand.CollectDiagnostics([main], scripts, File.ReadAllText, strictStructure: true);
            Assert.That(diagnostics, Is.Empty);
            var result = Engine.Run(scripts, target, additionalFeedPaths: [packages]);
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
            {
                "Example.cs(7): warning: Catch block contains a throw statement"
            }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
