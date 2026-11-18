using Cop.Cli.Commands;
using Cop.Lang.Ast;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

[TestFixture]
public sealed class CheckCatalogueAnalyzerTests
{
    [Test]
    public void Analyze_ExpandsAliasesAndUnions_AndListsUnreferencedChecks()
    {
        const string source = """
            type Violation = { Message : string }
            export let checkA : [Violation] = []
            export let checkB : [Violation] = []
            export let unused : [Violation] = []
            let alias = checkA
            export let suite = alias + checkB
            command MAIN = CHECK(suite)
            """;

        CheckCatalogueAnalysis result = AnalyzeLocal(source);

        Assert.That(result.Checks.Select(check => check.Name).OrderBy(name => name),
            Is.EqualTo(new[] { "checkA", "checkB", "unused" }));
        CheckCatalogueCommandEntry main = result.Commands.Single(command => command.Name == "MAIN");
        Assert.That(CheckNames(result, main), Is.EqualTo(new[] { "checkA", "checkB" }));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Analyze_ExpandsNestedImportedBundles_AndKeepsDuplicateCommandIdentitiesDistinct()
    {
        const string localSource = """
            type Violation = { Message : string }
            import csharp-checks
            let scope = 'workspace-scope'
            command MAIN = CHECK(csharpChecks(scope))
            """;
        const string packageSource = """
            export function leaf(cb: string) : [Violation] => []
            export function inner(cb: string) : [Violation] => leaf(cb)
            export function csharpChecks(cb: string) : [Violation] => inner(cb)
            """;
        const string secondEntrySource = """
            import csharp-checks
            command MAIN = CHECK(csharpChecks('package-scope'))
            """;

        string root = Path.Combine(Path.GetTempPath(), "catalogue-test");
        ModuleNode localModule = CopParser.Parse(localSource, Path.Combine(root, "cop-checks", "main.cop"));
        ModuleNode packageModule = CopParser.Parse(packageSource,
            Path.Combine(root, "packages", "csharp-checks", "src", "csharp-checks.cop"));
        ModuleNode secondEntryModule = CopParser.Parse(secondEntrySource,
            Path.Combine(root, "cop-checks", "cop", "packages", "main.cop"));
        CheckCatalogueAnalysis result = CheckCatalogueAnalyzer.Analyze(root,
        [
            new CheckCatalogueSource(localModule, Path.Combine(root, "cop-checks", "main.cop"), localSource, null),
            new CheckCatalogueSource(secondEntryModule,
                Path.Combine(root, "cop-checks", "cop", "packages", "main.cop"),
                secondEntrySource, null),
            new CheckCatalogueSource(packageModule,
                Path.Combine(root, "packages", "csharp-checks", "src", "csharp-checks.cop"),
                packageSource, "csharp-checks")
        ], []);

        Assert.That(result.Commands.Count(command => command.Name == "MAIN"), Is.EqualTo(2));
        CheckCatalogueCommandEntry workspaceMain = result.Commands.Single(command =>
            command.FilePath == "cop-checks/main.cop");
        CheckCatalogueCommandEntry packageMain = result.Commands.Single(command =>
            command.FilePath == "cop-checks/cop/packages/main.cop");
        Assert.That(CheckNames(result, workspaceMain), Is.EqualTo(new[] { "leaf" }));
        Assert.That(CheckNames(result, packageMain), Is.EqualTo(new[] { "leaf" }));
        Assert.That(workspaceMain.Scopes, Is.EqualTo(new[] { "scope = 'workspace-scope'" }));
        Assert.That(packageMain.Scopes, Is.EqualTo(new[] { "'package-scope'" }));
        Assert.That(result.Checks.Select(check => check.Name), Is.EqualTo(new[] { "leaf" }));
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Analyze_ConditionalMembershipIsExplicitlyMarkedAsUncertain()
    {
        const string source = """
            type Violation = { Message : string }
            export let checkA : [Violation] = []
            export let checkB : [Violation] = []
            export let selected : [Violation] = true ? checkA : checkB
            command MAIN = CHECK(selected)
            """;

        CheckCatalogueAnalysis result = AnalyzeLocal(source);
        CheckCatalogueCommandEntry main = result.Commands.Single();

        Assert.That(CheckNames(result, main), Is.EqualTo(new[] { "checkA", "checkB" }));
        Assert.That(main.Unresolved, Has.One.Contains("Conditional check membership"));
        Assert.That(result.Warnings, Has.Some.Contains("Conditional check membership"));
    }

    [Test]
    public void Analyze_UsesNamedCheckForQualifiedOverloadedViolationFunction()
    {
        const string localSource = """
            type Violation = { Message : string }
            import ai
            export let aiCheck = ai.judge('review')
            command AI = CHECK(aiCheck)
            """;
        const string aiSource = """
            export function judge(prompt: string) : [Violation] => []
            export function judge(prompt: string, value: string) : [Violation] => []
            """;

        string root = Path.Combine(Path.GetTempPath(), "catalogue-test");
        string localPath = Path.Combine(root, "cop-checks", "main.cop");
        string aiPath = Path.Combine(root, "packages", "ai", "src", "ai.cop");
        CheckCatalogueAnalysis result = CheckCatalogueAnalyzer.Analyze(root,
        [
            new CheckCatalogueSource(CopParser.Parse(localSource, localPath), localPath, localSource, null),
            new CheckCatalogueSource(CopParser.Parse(aiSource, aiPath), aiPath, aiSource, "ai")
        ], []);

        CheckCatalogueCommandEntry aiCommand = result.Commands.Single();
        Assert.That(CheckNames(result, aiCommand), Is.EqualTo(new[] { "aiCheck" }));
        Assert.That(aiCommand.Unresolved, Is.Empty);
    }

    [Test]
    public void Analyze_ReportsCyclesAndUnresolvedImportsInsteadOfClaimingCompleteness()
    {
        const string source = """
            type Violation = { Message : string }
            import absent-package
            export let first : [Violation] = second
            export let second : [Violation] = first
            command MAIN = CHECK(first)
            """;

        string root = Path.Combine(Path.GetTempPath(), "catalogue-test");
        string filePath = Path.Combine(root, "main.cop");
        ModuleNode module = CopParser.Parse(source, filePath);
        CheckCatalogueAnalysis result = CheckCatalogueAnalyzer.Analyze(root,
            [new CheckCatalogueSource(module, filePath, source, null)],
            ["Import 'absent-package' could not be resolved."]);

        Assert.That(result.Warnings, Has.Some.Contains("absent-package"));
        Assert.That(result.Warnings, Has.Some.Contains("Cyclic check composition"));
        Assert.That(result.Commands.Single().Unresolved, Has.Some.Contains("Cyclic check composition"));
    }

    [Test]
    public void Analyze_ExpandsNestedCommandHelpersContainingCheckCalls()
    {
        const string source = """
            type Violation = { Message : string }
            export let checkA : [Violation] = []
            function runChecks() => runNested()
            function runNested() => CHECK(checkA)
            command MAIN = runChecks()
            """;

        CheckCatalogueAnalysis result = AnalyzeLocal(source);
        CheckCatalogueCommandEntry main = result.Commands.Single();

        Assert.That(CheckNames(result, main), Is.EqualTo(new[] { "checkA" }));
        Assert.That(main.Unresolved, Is.Empty);
        Assert.That(result.Warnings, Is.Empty);
    }

    [Test]
    public void Groups_PreserveNestedFoldersAndDirectChecks_WithStableOrdering()
    {
        CheckCatalogueDefinition direct = new("direct", "direct", "csharp\\direct.cop",
            "workspace", 1, null, "", "let");
        CheckCatalogueDefinition nested = new("nested", "nested", "csharp/derived/naming/rule.cop",
            "workspace", 1, null, "", "let");
        CheckCatalogueDefinition rootCheck = new("root", "root", "main.cop",
            "workspace", 1, null, "", "let");

        IReadOnlyList<CheckCatalogueGroup> groups = CheckCatalogueGroup.Build(
            [nested, rootCheck, direct]);

        Assert.That(groups.Select(group => group.Name), Is.EqualTo(new[] { "Repository", "csharp" }));
        Assert.That(groups[0].CheckIds, Is.EqualTo(new[] { "root" }));
        CheckCatalogueGroup csharp = groups[1];
        Assert.That(csharp.CheckIds, Is.EqualTo(new[] { "direct" }));
        CheckCatalogueGroup derived = csharp.Groups.Single();
        Assert.That(derived.Name, Is.EqualTo("derived"));
        Assert.That(derived.CheckIds, Is.Empty);
        CheckCatalogueGroup naming = derived.Groups.Single();
        Assert.That(naming.Path, Is.EqualTo("csharp/derived/naming"));
        Assert.That(naming.CheckIds, Is.EqualTo(new[] { "nested" }));
        Assert.That(System.Text.Json.JsonSerializer.Serialize(
                CheckCatalogueGroup.Build([direct, nested, rootCheck])),
            Is.EqualTo(System.Text.Json.JsonSerializer.Serialize(groups)));
    }

    [Test]
    public void Groups_SeparateImportedPackagesFromIdenticallyNamedWorkspaceFolders()
    {
        CheckCatalogueDefinition local = new("local", "local", "rules/security/check.cop",
            "workspace", 1, null, "", "let");
        CheckCatalogueDefinition imported = new("imported", "imported", "rules/src/security/rules.cop",
            "rules", 1, null, "", "function");
        CheckCatalogueDefinition packageRoot = new("package-root", "packageRoot", "rules/src/rules.cop",
            "rules", 2, null, "", "function");

        IReadOnlyList<CheckCatalogueGroup> groups = CheckCatalogueGroup.Build(
            [packageRoot, imported, local]);

        Assert.That(groups.Select(group => group.Name), Is.EqualTo(new[] { "rules", "rules" }));
        Assert.That(groups.Select(group => group.Id).Distinct().Count(), Is.EqualTo(2));
        Assert.That(groups[0].Origin, Is.EqualTo("workspace"));
        Assert.That(groups[0].Groups.Single().CheckIds, Is.EqualTo(new[] { "local" }));
        Assert.That(groups[1].Origin, Is.EqualTo("rules"));
        Assert.That(groups[1].CheckIds, Is.EqualTo(new[] { "package-root" }));
        Assert.That(groups[1].Groups.Single().Name, Is.EqualTo("security"));
        Assert.That(groups[1].Groups.Single().CheckIds, Is.EqualTo(new[] { "imported" }));
    }

    [Test]
    public void HtmlRenderer_EncodesRepositoryTextInsideEmbeddedData()
    {
        const string injected = "</script><script>alert('no')</script>";
        CheckCatalogueDefinition check = new CheckCatalogueDefinition(
            "workspace:main.cop:1:check",
            "check",
            "main.cop",
            "workspace",
            1,
            injected,
            injected,
            "let");
        CheckCatalogueCommandEntry command = new CheckCatalogueCommandEntry(
            "workspace:main.cop:2:MAIN",
            "MAIN",
            "main.cop",
            "workspace",
            2,
            [check.Id],
            [],
            []);

        string html = CheckCatalogueHtml.Render("Catalogue", new([check], [command], []));

        Assert.That(html, Does.Not.Contain(injected));
        Assert.That(html, Does.Contain("\\u003C/script\\u003E"));
    }

    [Test]
    public void HtmlRenderer_ExplainsHowToRegenerateAndProvidesCopyButton()
    {
        string html = CheckCatalogueHtml.Render("Catalogue", new([], [], []));

        Assert.That(html, Does.Contain("run in a terminal from the repository root"));
        Assert.That(html, Does.Contain("<code>cop checks</code>"));
        Assert.That(html, Does.Contain("id=\"copy-command\""));
        Assert.That(html, Does.Contain("navigator.clipboard.writeText('cop checks')"));
        Assert.That(html, Does.Contain("Command selected; press Ctrl+C to copy."));
    }

    private static CheckCatalogueAnalysis AnalyzeLocal(string source)
    {
        string root = Path.Combine(Path.GetTempPath(), "catalogue-test");
        string filePath = Path.Combine(root, "main.cop");
        ModuleNode module = CopParser.Parse(source, filePath);
        return CheckCatalogueAnalyzer.Analyze(root,
            [new CheckCatalogueSource(module, filePath, source, null)], []);
    }

    private static string[] CheckNames(
        CheckCatalogueAnalysis analysis,
        CheckCatalogueCommandEntry command)
    {
        Dictionary<string, string> names = analysis.Checks.ToDictionary(check => check.Id, check => check.Name);
        return command.CheckIds.Select(id => names[id]).OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }
}
