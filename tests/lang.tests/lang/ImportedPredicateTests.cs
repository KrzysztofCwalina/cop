using NUnit.Framework;
using Cop.Lang;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

/// <summary>
/// Tests for exported predicates, functions, and let bindings from imported packages.
/// These reproduce bugs where imported package exports return empty/zero results
/// even though locally-defined equivalents work correctly.
///
/// Root issue: when a package exports a predicate and another file imports it,
/// using that predicate as a filter should produce the same results as a locally
/// defined predicate with identical logic.
/// </summary>
[TestFixture]
public class ImportedPredicateTests
{
    private string _tempDir = null!;
    private string _feedDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-import-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _feedDir = Path.Combine(_tempDir, "packages");
        Directory.CreateDirectory(_feedDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    // ========================================================================
    // Bug: Imported predicate returns 0 when filtering
    // ========================================================================

    /// <summary>
    /// An exported predicate from a package should work identically to a local predicate
    /// when used as a filter on a collection registered in the global environment.
    /// </summary>
    [Test]
    public void ExportedPredicateFilter_MatchesLocalPredicate()
    {
        // Package exports a predicate that checks a field
        CreatePackage("checks", @"
export predicate isPublic(t) => t.Visibility == 'public'
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        // Register collection data in global env
        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Foo"), ("Visibility", "public")),
            CreateObj("Type", ("Name", "Bar"), ("Visibility", "internal")),
            CreateObj("Type", ("Name", "Baz"), ("Visibility", "public")),
        };
        bridge.RegisterCollection("Types", types);

        // Load the package
        loader.LoadPackage("checks", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty, "Deferred let errors: " + string.Join(", ", errors));

        // Use the imported predicate as a filter
        bridge.LoadSource(@"
command main = {
    let filtered = Types:isPublic
    print(filtered.Count)
}", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs, Has.Count.EqualTo(1));
        Assert.That(bridge.Outputs[0], Is.EqualTo("2"),
            "Imported predicate should filter just like a local predicate");
    }

    /// <summary>
    /// An exported predicate that uses string matching (:contains) should work
    /// when imported and used as a filter.
    /// This reproduces the csharp-checks isVarDeclaration bug.
    /// </summary>
    [Test]
    public void ExportedPredicateWithContains_WorksWhenImported()
    {
        // Package exports a predicate checking Keywords:contains
        CreatePackage("checks", @"
export predicate isVarDecl(s) => s.Kind == 'declaration' && s.Keywords:contains('var')
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        // Register statements with Keywords as a list field
        var stmts = new List<DataObject>
        {
            CreateObj("Statement", ("Kind", "declaration"), ("Keywords", new List<string> { "var" }), ("MemberName", "x")),
            CreateObj("Statement", ("Kind", "declaration"), ("Keywords", new List<string> { "const" }), ("MemberName", "y")),
            CreateObj("Statement", ("Kind", "call"), ("Keywords", new List<string>()), ("MemberName", "z")),
            CreateObj("Statement", ("Kind", "declaration"), ("Keywords", new List<string> { "var" }), ("MemberName", "w")),
        };
        bridge.RegisterCollection("Statements", stmts);

        loader.LoadPackage("checks", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
command main = {
    let filtered = Statements:isVarDecl
    print(filtered.Count)
}", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("2"),
            "Imported predicate with :contains should match 2 var declarations");
    }

    /// <summary>
    /// Both a local predicate and an imported predicate with the same logic
    /// should produce identical results on the same data.
    /// </summary>
    [Test]
    public void ImportedVsLocalPredicate_SameResults()
    {
        CreatePackage("checks", @"
export predicate isHigh(item) => item.Score > 10
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        var items = new List<DataObject>
        {
            CreateObj("Item", ("Score", 5)),
            CreateObj("Item", ("Score", 15)),
            CreateObj("Item", ("Score", 25)),
        };
        bridge.RegisterCollection("items", items);

        loader.LoadPackage("checks", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
predicate localHigh(item) => item.Score > 10
command main = {
    let importedCount = items:isHigh.Count
    let localCount = items:localHigh.Count
    print(importedCount)
    print(localCount)
}", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("2"), "Imported predicate");
        Assert.That(bridge.Outputs[1], Is.EqualTo("2"), "Local predicate");
        Assert.That(bridge.Outputs[0], Is.EqualTo(bridge.Outputs[1]),
            "Imported and local predicates should produce identical results");
    }

    // ========================================================================
    // Bug: Exported let bindings resolve to empty
    // ========================================================================

    /// <summary>
    /// An exported let binding that filters a global collection should resolve
    /// to the correct count when accessed from an importing file.
    /// This reproduces the csharp-checks var-declarations bug.
    /// </summary>
    [Test]
    public void ExportedLetBinding_FilteringGlobalCollection_ResolvesCorrectly()
    {
        // Package defines a let that filters a global collection
        CreatePackage("checks", @"
predicate isPublic(t) => t.Visibility == 'public'
export let public-types = Types:isPublic
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Foo"), ("Visibility", "public")),
            CreateObj("Type", ("Name", "Bar"), ("Visibility", "internal")),
            CreateObj("Type", ("Name", "Baz"), ("Visibility", "public")),
        };
        bridge.RegisterCollection("Types", types);

        loader.LoadPackage("checks", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty, "Deferred let errors: " + string.Join(", ", errors));

        bridge.LoadSource(@"
command main = print(public-types.Count)", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("2"),
            "Exported let binding should resolve to filtered collection with 2 items");
    }

    /// <summary>
    /// An exported let binding that depends on a function from a transitively
    /// imported package should resolve correctly.
    /// This simulates: csharp.cop exports statements(), csharp-checks uses it.
    /// </summary>
    [Test]
    public void ExportedLetBinding_ViaTransitiveFunction_ResolvesCorrectly()
    {
        // Package "provider-pkg" exports a function that returns a collection
        CreatePackage("provider-pkg", @"
export function getItems() = Items
");

        // Package "checks-pkg" imports provider-pkg and exports a filtered let
        CreatePackage("checks-pkg", @"
import provider-pkg
predicate isActive(item) => item.Active == true
export let active-items = getItems():isActive
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        var items = new List<DataObject>
        {
            CreateObj("Item", ("Name", "A"), ("Active", true)),
            CreateObj("Item", ("Name", "B"), ("Active", false)),
            CreateObj("Item", ("Name", "C"), ("Active", true)),
        };
        bridge.RegisterCollection("Items", items);

        loader.LoadPackage("checks-pkg", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
command main = print(active-items.Count)", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("2"),
            "Exported let via transitive function should see 2 active items");
    }

    /// <summary>
    /// Multiple exported let bindings from the same package that use different
    /// predicates should all resolve correctly.
    /// </summary>
    [Test]
    public void MultipleExportedLetBindings_AllResolveCorrectly()
    {
        CreatePackage("checks", @"
predicate isPublic(t) => t.Visibility == 'public'
predicate isClass(t) => t.Kind == 'class'
export let public-types = Types:isPublic
export let class-types = Types:isClass
export let all-checks = public-types + class-types
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Foo"), ("Visibility", "public"), ("Kind", "class")),
            CreateObj("Type", ("Name", "Bar"), ("Visibility", "internal"), ("Kind", "class")),
            CreateObj("Type", ("Name", "IBaz"), ("Visibility", "public"), ("Kind", "interface")),
        };
        bridge.RegisterCollection("Types", types);

        loader.LoadPackage("checks", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
command main = {
    print(public-types.Count)
    print(class-types.Count)
    print(all-checks.Count)
}", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("2"), "public-types should have 2");
        Assert.That(bridge.Outputs[1], Is.EqualTo("2"), "class-types should have 2");
        Assert.That(bridge.Outputs[2], Is.EqualTo("4"), "all-checks = 2 + 2 = 4");
    }

    // ========================================================================
    // Bug: Exported function returning filtered collection
    // ========================================================================

    /// <summary>
    /// An exported function that returns a filtered collection should work
    /// when called from an importing file.
    /// </summary>
    [Test]
    public void ExportedFunction_ReturningFilteredCollection_Works()
    {
        CreatePackage("provider-pkg", @"
predicate isPublic(t) => t.Visibility == 'public'
export function publicTypes() = Types:isPublic
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Foo"), ("Visibility", "public")),
            CreateObj("Type", ("Name", "Bar"), ("Visibility", "internal")),
        };
        bridge.RegisterCollection("Types", types);

        loader.LoadPackage("provider-pkg", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
command main = {
    let result = publicTypes()
    print(result.Count)
}", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("1"),
            "Exported function should return 1 public type");
    }

    // ========================================================================
    // Bug: Predicate with chained filters from imported package
    // ========================================================================

    /// <summary>
    /// An exported predicate that uses chained filters (e.g., field:contains)
    /// should work through transitive imports.
    /// </summary>
    [Test]
    public void ExportedPredicate_ChainedContainsFilter_ViaTransitiveImport()
    {
        // Simulates csharp package exporting statements()
        CreatePackage("lang-pkg", @"
export function statements() = Statements
");

        // Simulates csharp-checks importing lang-pkg and filtering
        CreatePackage("checks-pkg", @"
import lang-pkg
export predicate isVarDecl(s) => s.Kind == 'declaration' && s.Keywords:contains('var')
export let var-decls = statements():isVarDecl
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        var stmts = new List<DataObject>
        {
            CreateObj("Statement", ("Kind", "declaration"), ("Keywords", new List<string> { "var" }), ("MemberName", "x")),
            CreateObj("Statement", ("Kind", "declaration"), ("Keywords", new List<string> { "const" }), ("MemberName", "y")),
            CreateObj("Statement", ("Kind", "call"), ("Keywords", new List<string>()), ("MemberName", "z")),
        };
        bridge.RegisterCollection("Statements", stmts);

        loader.LoadPackage("checks-pkg", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
command main = {
    print(var-decls.Count)
}", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("1"),
            "Exported let via transitive function + chained filter should find 1 var declaration");
    }

    // ========================================================================
    // Regression: importing a package should not break locally-defined predicates
    // ========================================================================

    /// <summary>
    /// Importing a package that exports predicates should not break locally-defined
    /// predicates with different names that operate on the same data.
    /// </summary>
    [Test]
    public void ImportingPackage_DoesNotBreak_LocalPredicates()
    {
        CreatePackage("checks", @"
export predicate isPublic(t) => t.Visibility == 'public'
export let public-types = Types:isPublic
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);

        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Foo"), ("Visibility", "public"), ("Kind", "class")),
            CreateObj("Type", ("Name", "Bar"), ("Visibility", "internal"), ("Kind", "interface")),
        };
        bridge.RegisterCollection("Types", types);

        loader.LoadPackage("checks", bridge.Evaluator);
        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
predicate isClass(t) => t.Kind == 'class'
command main = {
    let classes = Types:isClass
    print(classes.Count)
}", "<test>");
        bridge.RunCommand("main");

        Assert.That(bridge.Outputs[0], Is.EqualTo("1"),
            "Local predicate should still work after importing a package");
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private void CreatePackage(string name, string source)
    {
        var pkgDir = Path.Combine(_feedDir, name);
        var srcDir = Path.Combine(pkgDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, $"{name}.cop"), source);
        File.WriteAllText(Path.Combine(pkgDir, "cop.json"), $"{{\"name\": \"{name}\"}}");
    }

    private static DataObject CreateObj(string type, params (string Key, object? Value)[] fields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in fields)
            dict[k] = v;
        return new DataObject(type, dict);
    }
}
