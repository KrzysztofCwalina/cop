using NUnit.Framework;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

/// <summary>
/// Tests that verify per-module scoping prevents cross-package collisions.
/// These test the fix for issue #16: multiple packages defining the same non-exported
/// let binding (e.g., 'let cb = ...') should not overwrite each other.
/// </summary>
[TestFixture]
public class ModuleIsolationTests
{
    private string _tempDir = null!;
    private string _feedDir = null!;

    [SetUp]
    public void SetUp()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "cop-tests-" + Guid.NewGuid().ToString("N")[..8]);
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
    /// Two packages with the same non-exported let name ('data') should each see
    /// their own value when their exported functions access it.
    /// </summary>
    [Test]
    public void NonExportedLetBindingsDoNotCollide()
    {
        // Package A: exports getA() which returns module-local 'data'
        CreatePackage("pkg-a", @"
let data = 'alpha'
export function getA() = data
");

        // Package B: exports getB() which also uses module-local 'data'
        CreatePackage("pkg-b", @"
let data = 'beta'
export function getB() = data
");

        // User script imports both and calls both functions
        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("pkg-a", bridge.Evaluator);
        loader.LoadPackage("pkg-b", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty, "Deferred let eval errors: " + string.Join(", ", errors));

        bridge.LoadSource("command main = getA() + ',' + getB()", "<test>");
        var result = bridge.RunCommand("main");

        Assert.That(result.ToString(), Is.EqualTo("alpha,beta"),
            "Each package's non-exported 'data' should retain its own value");
    }

    /// <summary>
    /// Three packages all using 'let x = ...' internally should not interfere.
    /// </summary>
    [Test]
    public void ThreePackagesWithSameLetName()
    {
        CreatePackage("pkg-1", @"
let x = 100
export function val1() = x
");
        CreatePackage("pkg-2", @"
let x = 200
export function val2() = x
");
        CreatePackage("pkg-3", @"
let x = 300
export function val3() = x
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("pkg-1", bridge.Evaluator);
        loader.LoadPackage("pkg-2", bridge.Evaluator);
        loader.LoadPackage("pkg-3", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource("command main = val1() + val2() + val3()", "<test>");
        var result = bridge.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(600),
            "100 + 200 + 300 = 600; each package sees its own 'let x'");
    }

    /// <summary>
    /// Exported let bindings ARE visible globally (they should not be isolated).
    /// </summary>
    [Test]
    public void ExportedLetBindingsAreGloballyVisible()
    {
        CreatePackage("pkg-exported", @"
export let greeting = 'hello world'
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("pkg-exported", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource("command main = greeting", "<test>");
        var result = bridge.RunCommand("main");

        Assert.That(result.ToString(), Is.EqualTo("hello world"));
    }

    /// <summary>
    /// A non-exported let should NOT be visible from the user script.
    /// Only the module's own functions should see it.
    /// </summary>
    [Test]
    public void NonExportedLetIsNotGloballyVisible()
    {
        CreatePackage("pkg-hidden", @"
let secret = 'hidden-value'
export function reveal() = secret
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("pkg-hidden", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        // reveal() should work (function captures module env)
        bridge.LoadSource("command test1 = reveal()", "<test>");
        var result = bridge.RunCommand("test1");
        Assert.That(result.ToString(), Is.EqualTo("hidden-value"));

        // Direct access to 'secret' from user code should fail (not in global env)
        // LanguageBridge.RunCommand catches CopEvaluationException and adds to Errors
        bridge.LoadSource("command test2 = secret", "<test2>");
        bridge.RunCommand("test2");
        Assert.That(bridge.Errors, Has.Some.Contains("secret"),
            "Accessing non-exported 'secret' from user code should produce an error");
    }

    /// <summary>
    /// Simulates the real-world scenario: multiple packages define 'let cb = provider(...)'
    /// and export functions that reference cb. Each function should see its own cb.
    /// This is the exact pattern that caused issue #16.
    /// </summary>
    [Test]
    public void ProviderPatternWithMultiplePackages()
    {
        // Simulate csharp package: let cb = ..., export function statements() => cb.Items
        CreatePackage("fake-csharp", @"
let cb = 'csharp-provider'
export function csharpName() = cb
");

        // Simulate python package: let cb = ..., export function statements() => cb.Items
        CreatePackage("fake-python", @"
let cb = 'python-provider'
export function pythonName() = cb
");

        // Simulate javascript package
        CreatePackage("fake-js", @"
let cb = 'js-provider'
export function jsName() = cb
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("fake-csharp", bridge.Evaluator);
        loader.LoadPackage("fake-python", bridge.Evaluator);
        loader.LoadPackage("fake-js", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        // Each function should see its own module's 'cb'
        bridge.LoadSource(@"
command main = csharpName() + '|' + pythonName() + '|' + jsName()
", "<test>");
        var result = bridge.RunCommand("main");

        Assert.That(result.ToString(), Is.EqualTo("csharp-provider|python-provider|js-provider"),
            "Each package's 'let cb' should be isolated in its own module scope");
    }

    /// <summary>
    /// Module-local lets can reference global environment values (like provider collections).
    /// </summary>
    [Test]
    public void ModuleLocalLetCanAccessGlobalBindings()
    {
        // Register a global value before loading packages
        var bridge = new LanguageBridge();
        bridge.RegisterValue("globalData", new CopString("shared-value"));

        CreatePackage("pkg-uses-global", @"
let local = globalData
export function getLocal() = local
");

        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("pkg-uses-global", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource("command main = getLocal()", "<test>");
        var result = bridge.RunCommand("main");

        Assert.That(result.ToString(), Is.EqualTo("shared-value"),
            "Module-local let should be able to reference global environment bindings");
    }

    /// <summary>
    /// Transitive imports: package C imports package A, both define 'let x'.
    /// C's exported function should see C's x, not A's.
    /// </summary>
    [Test]
    public void TransitiveImportsDoNotCollide()
    {
        CreatePackage("base-pkg", @"
let x = 'base'
export function baseVal() = x
");

        CreatePackage("derived-pkg", @"
import base-pkg
let x = 'derived'
export function derivedVal() = x
");

        var bridge = new LanguageBridge();
        var loader = new ModuleLoader([_feedDir]);
        loader.LoadPackage("derived-pkg", bridge.Evaluator);

        var errors = new List<string>();
        loader.EvalDeferredLetBindings(bridge.Evaluator, errors);
        Assert.That(errors, Is.Empty);

        bridge.LoadSource("command main = baseVal() + '|' + derivedVal()", "<test>");
        var result = bridge.RunCommand("main");

        Assert.That(result.ToString(), Is.EqualTo("base|derived"),
            "Transitive imports should each have isolated module scopes");
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
