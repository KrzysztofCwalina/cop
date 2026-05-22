using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Tests that the CSharp/Code provider correctly honors per-collection filter queries.
/// Verifies that CollectionFilters are applied inline during extraction, reducing
/// the number of items returned to only those matching the filter.
/// </summary>
[TestFixture]
public class CodeProviderQueryTests
{
    // Sample source files with known types for testing
    private static readonly List<SourceFile> TestFiles = CreateTestFiles();

    private static List<SourceFile> CreateTestFiles()
    {
        var file1 = new SourceFile(
            "src/providers/HttpProvider.cs", "csharp",
            Types:
            [
                new TypeDeclaration("HttpProvider", TypeKind.Class, Modifier.Public | Modifier.Sealed,
                    ["ObjectProvider"], [], [], [], [], [], 10) { HasDocComment = true },
                new TypeDeclaration("IHttpClient", TypeKind.Interface, Modifier.Public,
                    [], [], [], [], [], [], 50) { HasDocComment = false },
            ],
            Statements:
            [
                new StatementInfo("return", ["return"], "HttpProvider", "Query", ["result"], 15, true),
                new StatementInfo("throw", ["throw"], "HttpProvider", "Validate", ["ArgumentException"], 20, true),
            ],
            RawText: "// line1\n// line2\n// line3\n")
        { Namespace = "Cop.Providers" };

        // Wire File references
        for (int i = 0; i < file1.Types.Count; i++)
            file1.Types[i] = file1.Types[i] with { File = file1 };
        for (int i = 0; i < file1.Statements.Count; i++)
            file1.Statements[i].File = file1;

        var file2 = new SourceFile(
            "src/services/UserService.cs", "csharp",
            Types:
            [
                new TypeDeclaration("UserService", TypeKind.Class, Modifier.Public,
                    [], [], [], [], [], [], 5) { HasDocComment = true },
                new TypeDeclaration("UserServiceOptions", TypeKind.Class, Modifier.Internal,
                    [], [], [], [], [], [], 30),
                new TypeDeclaration("ServiceStatus", TypeKind.Enum, Modifier.Public,
                    [], [], [], [], [], ["Active", "Inactive"], 60),
            ],
            Statements:
            [
                new StatementInfo("if", ["if"], "UserService", "Process", [], 10, true),
                new StatementInfo("return", ["return"], "UserService", "GetUser", ["user"], 25, true),
            ],
            RawText: "// line1\n// line2\n")
        { Namespace = "Cop.Services" };

        for (int i = 0; i < file2.Types.Count; i++)
            file2.Types[i] = file2.Types[i] with { File = file2 };
        for (int i = 0; i < file2.Statements.Count; i++)
            file2.Statements[i].File = file2;

        var file3 = new SourceFile(
            "src/clients/GraphClient.cs", "csharp",
            Types:
            [
                new TypeDeclaration("GraphClient", TypeKind.Class, Modifier.Public | Modifier.Abstract,
                    ["IDisposable"], [], [],
                    [new MethodDeclaration("Execute", Modifier.Public, [], null, [], 15)],
                    [], [], 10) { HasDocComment = true },
                new TypeDeclaration("GraphClientFactory", TypeKind.Class, Modifier.Public | Modifier.Static,
                    [], [], [], [], [], [], 40),
            ],
            Statements:
            [
                new StatementInfo("try", ["try", "catch"], "GraphClient", "Execute", [], 16, true)
                { IsErrorHandler = true },
            ],
            RawText: "// line1\n")
        { Namespace = "Cop.Clients" };

        for (int i = 0; i < file3.Types.Count; i++)
            file3.Types[i] = file3.Types[i] with { File = file3 };
        for (int i = 0; i < file3.Statements.Count; i++)
            file3.Statements[i].File = file3;

        return [file1, file2, file3];
    }

    [Test]
    public void Filter_NameEndsWith_ReturnsOnlyMatchingTypes()
    {
        // Query: Types where Name:endsWith('Provider')
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = new StringOpFilter("Name", StringOp.EndsWith, "Provider")
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        Assert.That(types, Has.Count.EqualTo(1));
        Assert.That(((TypeDeclaration)types[0]).Name, Is.EqualTo("HttpProvider"));
    }

    [Test]
    public void Filter_NameStartsWith_ReturnsOnlyMatchingTypes()
    {
        // Query: Types where Name:startsWith('Graph')
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = new StringOpFilter("Name", StringOp.StartsWith, "Graph")
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        Assert.That(types, Has.Count.EqualTo(2)); // GraphClient, GraphClientFactory
        var names = types.Select(t => ((TypeDeclaration)t).Name).ToList();
        Assert.That(names, Does.Contain("GraphClient"));
        Assert.That(names, Does.Contain("GraphClientFactory"));
    }

    [Test]
    public void Filter_NameContains_ReturnsOnlyMatchingTypes()
    {
        // Query: Types where Name:contains('Service')
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = new StringOpFilter("Name", StringOp.Contains, "Service")
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        Assert.That(types, Has.Count.EqualTo(3)); // UserService, UserServiceOptions, ServiceStatus
        var names = types.Select(t => ((TypeDeclaration)t).Name).OrderBy(n => n).ToList();
        Assert.That(names, Is.EqualTo(new[] { "ServiceStatus", "UserService", "UserServiceOptions" }));
    }

    [Test]
    public void Filter_FlagsIsSet_ReturnsOnlyPublicTypes()
    {
        // Query: Types where Modifiers:isSet(Public) — flag value 1
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = new FlagsFilter("Modifiers", FlagsOp.IsSet, (long)Modifier.Public)
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        // UserServiceOptions is Internal, not Public — should be excluded
        Assert.That(types, Has.Count.EqualTo(6)); // all except UserServiceOptions
        var names = types.Select(t => ((TypeDeclaration)t).Name).ToList();
        Assert.That(names, Does.Not.Contain("UserServiceOptions"));
    }

    [Test]
    public void Filter_BoolProperty_Documented_ReturnsOnlyDocumentedTypes()
    {
        // Query: Types where Documented == true
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = new PropertyFilter("Documented", true)
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        Assert.That(types, Has.Count.EqualTo(3)); // HttpProvider, UserService, GraphClient
        var names = types.Select(t => ((TypeDeclaration)t).Name).OrderBy(n => n).ToList();
        Assert.That(names, Is.EqualTo(new[] { "GraphClient", "HttpProvider", "UserService" }));
    }

    [Test]
    public void Filter_AndCombination_PublicAndEndsWith()
    {
        // Query: Types where Modifiers:isSet(Public) AND Name:endsWith('Client')
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = FilterExpression.And(
                new FlagsFilter("Modifiers", FlagsOp.IsSet, (long)Modifier.Public),
                new StringOpFilter("Name", StringOp.EndsWith, "Client"))
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        Assert.That(types, Has.Count.EqualTo(2)); // IHttpClient, GraphClient
        var names = types.Select(t => ((TypeDeclaration)t).Name).OrderBy(n => n).ToList();
        Assert.That(names, Is.EqualTo(new[] { "GraphClient", "IHttpClient" }));
    }

    [Test]
    public void Filter_OrCombination_ProviderOrClient()
    {
        // Query: Types where Name:endsWith('Provider') OR Name:endsWith('Client')
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = FilterExpression.Or(
                new StringOpFilter("Name", StringOp.EndsWith, "Provider"),
                new StringOpFilter("Name", StringOp.EndsWith, "Client"))
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        Assert.That(types, Has.Count.EqualTo(3)); // HttpProvider, IHttpClient, GraphClient
        var names = types.Select(t => ((TypeDeclaration)t).Name).OrderBy(n => n).ToList();
        Assert.That(names, Is.EqualTo(new[] { "GraphClient", "HttpProvider", "IHttpClient" }));
    }

    [Test]
    public void Filter_NotCombination_ExcludesInterfaces()
    {
        // Query: Types where NOT Kind:equals('Interface')
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = new NotFilter(new StringOpFilter("Kind", StringOp.Equals, "Interface"))
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Types", filters);

        var types = collections["Types"];
        // IHttpClient is the only interface — 7 total minus 1
        Assert.That(types, Has.Count.EqualTo(6));
        var names = types.Select(t => ((TypeDeclaration)t).Name).ToList();
        Assert.That(names, Does.Not.Contain("IHttpClient"));
    }

    [Test]
    public void Filter_OnStatements_ByKind_ReturnsOnlyMatchingStatements()
    {
        // Query: Statements where Kind:equals('return')
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Statements"] = new StringOpFilter("Kind", StringOp.Equals, "return")
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, "Statements", filters);

        var stmts = collections["Statements"];
        Assert.That(stmts, Has.Count.EqualTo(2)); // return in HttpProvider.Query and UserService.GetUser
        Assert.That(stmts.All(s => ((StatementInfo)s).Kind == "return"), Is.True);
    }

    [Test]
    public void Filter_PerCollection_DoesNotAffectOtherCollections()
    {
        // Query: Types with filter, Lines without filter — Lines should be unaffected
        var filters = new Dictionary<string, FilterExpression>
        {
            ["Types"] = new StringOpFilter("Name", StringOp.EndsWith, "Provider") // matches 1
        };

        var collections = CodeCollectionBuilder.ExtractCollections(TestFiles, null, filters);

        // Types should be filtered
        Assert.That(collections["Types"], Has.Count.EqualTo(1));
        // Lines should NOT be filtered (no filter for Lines collection)
        // TestFiles: file1 has 4 lines, file2 has 3 lines, file3 has 2 lines = 9 total
        Assert.That(collections["Lines"], Has.Count.EqualTo(9));
    }
}
