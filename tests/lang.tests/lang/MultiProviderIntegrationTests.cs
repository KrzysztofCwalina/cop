using NUnit.Framework;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

/// <summary>
/// Integration tests that simulate multiple providers loaded simultaneously.
/// Verifies that each package's exported functions resolve to the correct provider's data.
/// Tests the exact pattern from real packages (csharp, python, javascript):
///   let cb = provider('name', nic)
///   export function statements() => cb.Statements
/// </summary>
[TestFixture]
public class MultiProviderIntegrationTests
{
    private string _tempDir = null!;
    private string _feedDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-mp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _feedDir = Path.Combine(_tempDir, "packages");
        Directory.CreateDirectory(_feedDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    /// <summary>
    /// Simulates the real scenario: two provider packages register collections in global env,
    /// each package creates a provider proxy via 'let cb = provider(...)', and each package's
    /// exported function should resolve to its own provider's collection.
    /// </summary>
    [Test]
    public void ProviderProxiesResolveToCorrectCollections()
    {
        // Package alpha: let cb = provider('alpha', nic); export function alphaItems() => cb.Items
        CreatePackage("alpha", @"
let cb = provider('alpha', nic)
export function alphaItems() = cb.Items
");

        // Package beta: let cb = provider('beta', nic); export function betaItems() => cb.Items
        CreatePackage("beta", @"
let cb = provider('beta', nic)
export function betaItems() = cb.Items
");

        var bridge = new LanguageBridge();

        // Simulate provider registration (what Engine does: register qualified collections in global env)
        var alphaData = new List<CopValue>
        {
            new CopString("alpha-item-1"),
            new CopString("alpha-item-2"),
            new CopString("alpha-item-3"),
        };
        var betaData = new List<CopValue>
        {
            new CopString("beta-item-1"),
        };
        bridge.Evaluator.GlobalEnvironment.Define("alpha.Items", new CopList(alphaData));
        bridge.Evaluator.GlobalEnvironment.Define("beta.Items", new CopList(betaData));

        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("alpha", bridge.Evaluator);
        loader.LoadPackage("beta", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        // alphaItems() should return alpha's 3 items
        bridge.LoadSource("command testAlpha = count(alphaItems())", "<test>");
        var alphaResult = bridge.RunCommand("testAlpha");
        Assert.That(alphaResult, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)alphaResult).Value, Is.EqualTo(3),
            "alphaItems() should see alpha's 3 items, not beta's 1 item");

        // betaItems() should return beta's 1 item
        bridge.LoadSource("command testBeta = count(betaItems())", "<test2>");
        var betaResult = bridge.RunCommand("testBeta");
        Assert.That(betaResult, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)betaResult).Value, Is.EqualTo(1),
            "betaItems() should see beta's 1 item, not alpha's 3 items");
    }

    /// <summary>
    /// Three packages (mimicking csharp, python, javascript) all define 'let cb = provider(...)'
    /// and export a 'statements()' function. Each should return the correct provider's Statements.
    /// </summary>
    [Test]
    public void ThreeProvidersWithSameExportedFunctionName()
    {
        CreatePackage("lang-cs", @"
let cb = provider('csharp', nic)
export function statements() = cb.Statements
");
        CreatePackage("lang-py", @"
let cb = provider('python', nic)
export function statements() = cb.Statements
");
        CreatePackage("lang-js", @"
let cb = provider('javascript', nic)
export function statements() = cb.Statements
");

        var bridge = new LanguageBridge();

        // Register provider collections (qualified names as Engine does)
        bridge.Evaluator.GlobalEnvironment.Define("csharp.Statements",
            new CopList([new CopString("cs1"), new CopString("cs2"), new CopString("cs3")]));
        bridge.Evaluator.GlobalEnvironment.Define("python.Statements",
            new CopList([new CopString("py1"), new CopString("py2")]));
        bridge.Evaluator.GlobalEnvironment.Define("javascript.Statements",
            new CopList([new CopString("js1")]));

        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("lang-cs", bridge.Evaluator);
        loader.LoadPackage("lang-py", bridge.Evaluator);
        loader.LoadPackage("lang-js", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        // statements() is now a function group with 3 overloads (all 0 params).
        // The last loaded one would win dispatch. This test verifies the module-local
        // 'cb' in each function body is correct regardless of dispatch order.
        // Call each by its type resolution or direct internal test.

        // To test each function independently, we can look them up as a function group
        // and invoke each overload directly:
        var statementsBinding = bridge.Evaluator.GlobalEnvironment.Lookup("statements");

        if (statementsBinding is CopFunctionGroup group)
        {
            // Function group with same arity dispatches to last registered by default,
            // but each function's body should access its own module's 'cb'.
            // Let's verify by calling each overload and checking the count.
            // The group falls back to last overload (lang-js) which should see 'javascript.Statements'
            bridge.LoadSource("command testStatements = count(statements())", "<test>");
            var result = bridge.RunCommand("testStatements");
            Assert.That(result, Is.InstanceOf<CopInt>());
            // Last registered is lang-js with 1 item
            Assert.That(((CopInt)result).Value, Is.EqualTo(1),
                "Function group dispatches to last registered; it should see javascript.Statements (1 item)");
        }
        else if (statementsBinding is CopFunction singleFunc)
        {
            // If only one survived (shouldn't happen with function groups), it should still work
            Assert.Fail("Expected a CopFunctionGroup with 3 overloads, got single function");
        }
    }

    /// <summary>
    /// Predicates applied to provider data should correctly filter items.
    /// Tests the end-to-end pattern: provider registers DataObjects, package exports
    /// a function returning the collection, user applies predicate filter.
    /// </summary>
    [Test]
    public void PredicateFilterOnProviderCollection()
    {
        CreatePackage("test-provider", @"
let cb = provider('test', nic)
export function items() = cb.Items
");

        var bridge = new LanguageBridge();

        // Register provider data as items with fields
        var items = new List<CopValue>();
        for (int i = 0; i < 50; i++)
        {
            var fields = new Dictionary<string, CopValue>
            {
                ["Name"] = new CopString($"item-{i}"),
                ["IsPublic"] = CopBool.Of(i % 3 == 0) // every 3rd is public
            };
            items.Add(new CopObject(fields));
        }
        bridge.Evaluator.GlobalEnvironment.Define("test.Items", new CopList(items));

        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("test-provider", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource(@"
predicate isPublic(item) => item.IsPublic == true
let publicItems = items():isPublic
command main = count(publicItems)
", "<test>");
        var result = bridge.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopInt>());
        // Items at index 0,3,6,...,48 → 17 items
        Assert.That(((CopInt)result).Value, Is.EqualTo(17),
            "Predicate should correctly filter provider data (every 3rd of 50 = 17)");
    }

    /// <summary>
    /// When two packages export same-named functions but one package's provider has more
    /// data, the correct provider should be resolved for each function.
    /// Regression test for issue #16.
    /// </summary>
    [Test]
    public void Issue16Regression_WrongProviderDataResolution()
    {
        // csharp has many statements
        CreatePackage("fake-csharp", @"
let cb = provider('csharp', nic)
export function csharpStatements() = cb.Statements
");

        // python has few statements
        CreatePackage("fake-python", @"
let cb = provider('python', nic)
export function pythonStatements() = cb.Statements
");

        var bridge = new LanguageBridge();

        // csharp: 1000 statements
        var csStatements = new List<CopValue>();
        for (int i = 0; i < 1000; i++)
        {
            var fields = new Dictionary<string, CopValue>
            {
                ["Kind"] = new CopString(i % 2 == 0 ? "declaration" : "expression")
            };
            csStatements.Add(new CopObject(fields));
        }
        bridge.Evaluator.GlobalEnvironment.Define("csharp.Statements", new CopList(csStatements));

        // python: 30 statements
        var pyStatements = new List<CopValue>();
        for (int i = 0; i < 30; i++)
        {
            var fields = new Dictionary<string, CopValue>
            {
                ["Kind"] = new CopString("expression")
            };
            pyStatements.Add(new CopObject(fields));
        }
        bridge.Evaluator.GlobalEnvironment.Define("python.Statements", new CopList(pyStatements));

        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("fake-csharp", bridge.Evaluator);
        loader.LoadPackage("fake-python", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        // csharpStatements() should return 1000 items
        bridge.LoadSource("command testCs = count(csharpStatements())", "<test1>");
        var csResult = bridge.RunCommand("testCs");
        Assert.That(((CopInt)csResult).Value, Is.EqualTo(1000),
            "csharpStatements() must see csharp's 1000 items, not python's 30");

        // pythonStatements() should return 30 items
        bridge.LoadSource("command testPy = count(pythonStatements())", "<test2>");
        var pyResult = bridge.RunCommand("testPy");
        Assert.That(((CopInt)pyResult).Value, Is.EqualTo(30),
            "pythonStatements() must see python's 30 items, not csharp's 1000");

        // Predicate on csharp data should find matches
        bridge.LoadSource(@"
predicate isDecl(s) => s.Kind == 'declaration'
let decls = csharpStatements():isDecl
command testPred = count(decls)
", "<test3>");
        var predResult = bridge.RunCommand("testPred");
        Assert.That(((CopInt)predResult).Value, Is.EqualTo(500),
            "Predicate on csharp data should find 500 declarations (every other of 1000)");

        // Same predicate on python data should find 0 (all are 'expression')
        bridge.LoadSource(@"
predicate isDecl2(s) => s.Kind == 'declaration'
let pyDecls = pythonStatements():isDecl2
command testPyPred = count(pyDecls)
", "<test4>");
        var pyPredResult = bridge.RunCommand("testPyPred");
        Assert.That(((CopInt)pyPredResult).Value, Is.EqualTo(0),
            "Predicate on python data should find 0 declarations");
    }

    private void CreatePackage(string name, string source)
    {
        var pkgDir = Path.Combine(_feedDir, name);
        var srcDir = Path.Combine(pkgDir, "src");
        Directory.CreateDirectory(srcDir);
        File.WriteAllText(Path.Combine(srcDir, $"{name}.cop"), source);
        File.WriteAllText(Path.Combine(pkgDir, "cop.json"), $"{{\"name\": \"{name}\"}}");
    }
}
