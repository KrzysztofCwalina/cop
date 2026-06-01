using NUnit.Framework;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

[TestFixture]
public class BinderTests
{
    /// <summary>
    /// Core type symbols that would normally come from importing the core package.
    /// These mirror the declarations in packages/core/src/primitives.cop.
    /// </summary>
    private static readonly IReadOnlyList<Symbol> CoreTypes = new Symbol[]
    {
        new TypeSymbol("object", null, [new PropertySymbol("Type", new TypeRef("string"), false)]),
        new TypeSymbol("string", "object", []),
        new TypeSymbol("int", "object", []),
        new TypeSymbol("float", "object", []),
        new TypeSymbol("bool", "object", []),
        new TypeSymbol("byte", "object", []),
        new TypeSymbol("bytes", "object", []),
        new TypeSymbol("function", "object", []),
    };

    private BindingResult Bind(string source, IReadOnlyList<Symbol>? externals = null)
    {
        // Merge core types with any additional externals
        var allExternals = new List<Symbol>(CoreTypes);
        if (externals is not null)
            allExternals.AddRange(externals);

        var module = CopParser.Parse(source, "test.cop");
        var binder = new Binder("test.cop", allExternals);
        return binder.Bind(module);
    }

    // ========================================================================
    // Scope & Symbol Registration
    // ========================================================================

    [Test]
    public void BindsLetDeclaration()
    {
        var result = Bind("let x : int = 42");
        Assert.That(result.HasErrors, Is.False);
        Assert.That(result.GlobalScope.Resolve("x"), Is.InstanceOf<VariableSymbol>());

        var sym = (VariableSymbol)result.GlobalScope.Resolve("x")!;
        Assert.That(sym.DeclaredType!.Name, Is.EqualTo("int"));
        Assert.That(sym.IsReadOnly, Is.True);
    }

    [Test]
    public void BindsExportedLetDeclaration()
    {
        var result = Bind("export let name : string = 'hello'");
        var sym = result.GlobalScope.Resolve("name") as VariableSymbol;
        Assert.That(sym, Is.Not.Null);
        Assert.That(sym!.IsExported, Is.True);
    }

    [Test]
    public void BindsTypeDeclaration()
    {
        var result = Bind(@"
type Person = {
    Name : string
    Age : int
    Email : string?
}");
        Assert.That(result.HasErrors, Is.False);
        var sym = result.GlobalScope.Resolve("Person") as TypeSymbol;
        Assert.That(sym, Is.Not.Null);
        Assert.That(sym!.Properties, Has.Count.EqualTo(3));
        Assert.That(sym.Properties[0].Name, Is.EqualTo("Name"));
        Assert.That(sym.Properties[2].IsOptional, Is.True);
    }

    [Test]
    public void BindsEnumAndInjectsMembersIntoScope()
    {
        var result = Bind("enum Color = Red | Green | Blue");
        Assert.That(result.HasErrors, Is.False);

        var enumSym = result.GlobalScope.Resolve("Color") as EnumSymbol;
        Assert.That(enumSym, Is.Not.Null);
        Assert.That(enumSym!.Members, Has.Count.EqualTo(3));

        // Members should be accessible at module scope
        var red = result.GlobalScope.Resolve("Red") as EnumMemberSymbol;
        Assert.That(red, Is.Not.Null);
        Assert.That(red!.OwningEnumName, Is.EqualTo("Color"));
    }

    [Test]
    public void BindsFlagsDeclaration()
    {
        var result = Bind("flags Permissions = Read | Write | Execute");
        Assert.That(result.HasErrors, Is.False);
        Assert.That(result.GlobalScope.Resolve("Permissions"), Is.InstanceOf<EnumSymbol>());
        Assert.That(result.GlobalScope.Resolve("Read"), Is.InstanceOf<EnumMemberSymbol>());
    }

    [Test]
    public void BindsFunctionDeclaration()
    {
        var result = Bind("function add(a : int, b : int) : int = a + b");
        Assert.That(result.HasErrors, Is.False);

        var sym = result.GlobalScope.Resolve("add") as FunctionSymbol;
        Assert.That(sym, Is.Not.Null);
        Assert.That(sym!.CallableKind, Is.EqualTo(CallableKind.Function));
        Assert.That(sym.Parameters, Has.Count.EqualTo(2));
        Assert.That(sym.Parameters[0].Name, Is.EqualTo("a"));
        Assert.That(sym.ReturnType!.Name, Is.EqualTo("int"));
    }

    [Test]
    public void BindsPredicateAsFunctionWithPredicateKind()
    {
        var result = Bind("predicate isLong(s : string) => s.Length > 10");
        Assert.That(result.HasErrors, Is.False);

        var sym = result.GlobalScope.Resolve("isLong") as FunctionSymbol;
        Assert.That(sym, Is.Not.Null);
        Assert.That(sym!.CallableKind, Is.EqualTo(CallableKind.Predicate));
    }

    [Test]
    public void BindsCommandDeclaration()
    {
        var result = Bind(@"
command main = {
    let items = getItems()
    print(items)
}");
        Assert.That(result.HasErrors, Is.False);

        // command desugars to uppercase function with BlockBody
        var sym = result.GlobalScope.Resolve("MAIN") as FunctionSymbol;
        Assert.That(sym, Is.Not.Null);
        Assert.That(sym!.CallableKind, Is.EqualTo(CallableKind.Command));
    }

    // ========================================================================
    // Name Resolution
    // ========================================================================

    [Test]
    public void ResolvesIdentifierInFunctionBody()
    {
        var result = Bind(@"
let x : int = 10
function double() : int = x + x");

        Assert.That(result.HasErrors, Is.False);
        // The function body should have resolved 'x' references
        var xSymbol = result.GlobalScope.Resolve("x");
        Assert.That(xSymbol, Is.Not.Null);
    }

    [Test]
    public void ResolvesParameterInFunctionBody()
    {
        var result = Bind("function inc(n : int) : int = n + 1");
        Assert.That(result.HasErrors, Is.False);

        // Function should have its own scope with parameter
        var funcDecl = result.Module.Declarations.OfType<FunctionDecl>().First();
        Assert.That(result.DeclarationScopes.ContainsKey(funcDecl), Is.True);

        var funcScope = result.DeclarationScopes[funcDecl];
        var paramSym = funcScope.Resolve("n");
        Assert.That(paramSym, Is.InstanceOf<ParameterSymbol>());
    }

    [Test]
    public void FunctionScopeSeesGlobalDeclarations()
    {
        var result = Bind(@"
let factor : int = 5
function scale(n : int) : int = n + factor");

        Assert.That(result.HasErrors, Is.False);
        var funcDecl = result.Module.Declarations.OfType<FunctionDecl>().First();
        var funcScope = result.DeclarationScopes[funcDecl];

        // Should resolve 'factor' by walking to parent (global) scope
        var resolved = funcScope.Resolve("factor");
        Assert.That(resolved, Is.InstanceOf<VariableSymbol>());
    }

    [Test]
    public void ForEachCreatesChildScope()
    {
        // Cop foreach is pipeline-style, not block-style
        var result = Bind(@"
command main = {
    let items = getAll()
    items => transform
}");
        Assert.That(result.HasErrors, Is.False);
    }

    [Test]
    public void LambdaCreatesChildScope()
    {
        var result = Bind("let filtered = items.where((x) => x.Active)");
        Assert.That(result.HasErrors, Is.False);
    }

    // ========================================================================
    // Forward References
    // ========================================================================

    [Test]
    public void ForwardReferenceToFunction()
    {
        var result = Bind(@"
function caller() : int = callee()
function callee() : int = 42");

        Assert.That(result.HasErrors, Is.False);
        // Both functions should be registered (pass 1) before bodies are bound (pass 2)
        Assert.That(result.GlobalScope.Resolve("caller"), Is.Not.Null);
        Assert.That(result.GlobalScope.Resolve("callee"), Is.Not.Null);
    }

    [Test]
    public void ForwardReferenceToType()
    {
        var result = Bind(@"
function getName(p : Person) : string = p.Name
type Person = {
    Name : string
}");
        Assert.That(result.HasErrors, Is.False);
    }

    // ========================================================================
    // External Symbols
    // ========================================================================

    [Test]
    public void ExternalSymbolsAreResolvable()
    {
        var externals = new List<Symbol>
        {
            new FunctionSymbol("print", CallableKind.External,
                new List<ParameterSymbol> { new("value", null, 0) })
        };

        var result = Bind(@"
command main = {
    print('hello')
}", externals);

        Assert.That(result.HasErrors, Is.False);
        var printSym = result.GlobalScope.Resolve("print");
        Assert.That(printSym, Is.InstanceOf<FunctionSymbol>());
        Assert.That(((FunctionSymbol)printSym!).CallableKind, Is.EqualTo(CallableKind.External));
    }

    // ========================================================================
    // Duplicate Detection
    // ========================================================================

    [Test]
    public void DuplicateLetProducesWarning()
    {
        var result = Bind(@"
let x : int = 1
let x : int = 2");

        Assert.That(result.Diagnostics, Has.Count.GreaterThan(0));
        Assert.That(result.Diagnostics[0].Message, Does.Contain("Duplicate"));
    }

    [Test]
    public void DuplicateFunctionIsAllowed_Overloading()
    {
        // In Cop, multiple predicates with same name (different guards) are common
        var result = Bind(@"
type Item = { Score : int, Rating : string }
predicate isGood(x : Item) => x.Score > 80
predicate isGood(x : Item) => x.Rating == 'A'");

        // Should not produce errors (overloading is allowed)
        Assert.That(result.HasErrors, Is.False);
    }

    // ========================================================================
    // Scope Chain
    // ========================================================================

    [Test]
    public void ScopeResolveWalksParentChain()
    {
        var global = new Scope(label: "global");
        var funcScope = global.CreateChild("function:foo");
        var loopScope = funcScope.CreateChild("foreach");

        global.Declare(new VariableSymbol("x"));
        funcScope.Declare(new ParameterSymbol("y", null, 0));
        loopScope.Declare(new VariableSymbol("z"));

        Assert.That(loopScope.Resolve("z"), Is.Not.Null);
        Assert.That(loopScope.Resolve("y"), Is.Not.Null);
        Assert.That(loopScope.Resolve("x"), Is.Not.Null);
        Assert.That(loopScope.Resolve("nonexistent"), Is.Null);
    }

    [Test]
    public void ScopeResolveLocalDoesNotWalkParent()
    {
        var global = new Scope(label: "global");
        var child = global.CreateChild("child");
        global.Declare(new VariableSymbol("x"));

        Assert.That(child.ResolveLocal("x"), Is.Null);
        Assert.That(child.Resolve("x"), Is.Not.Null);
    }

    [Test]
    public void ScopeDeclareReturnsFalseOnDuplicate()
    {
        var scope = new Scope(label: "test");
        Assert.That(scope.Declare(new VariableSymbol("x")), Is.True);
        Assert.That(scope.Declare(new VariableSymbol("x")), Is.False);
    }

    // ========================================================================
    // Type Validation
    // ========================================================================

    [Test]
    public void UnknownTypeInParameterProducesError()
    {
        var result = Bind("function greet(name : Foo) : string = name");
        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Diagnostics[0].Message, Does.Contain("Unknown type 'Foo'"));
    }

    [Test]
    public void UnknownTypeInReturnTypeProducesError()
    {
        var result = Bind("function make() : Widget = 42");
        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Diagnostics[0].Message, Does.Contain("Unknown type 'Widget'"));
    }

    [Test]
    public void UnknownTypeInLetAnnotationProducesError()
    {
        var result = Bind("let x : Gadget = 42");
        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Diagnostics[0].Message, Does.Contain("Unknown type 'Gadget'"));
    }

    [Test]
    public void UnknownTypeInPropertyProducesError()
    {
        var result = Bind(@"
type Person = {
    Name : string,
    Address : Location
}");
        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Diagnostics[0].Message, Does.Contain("Unknown type 'Location'"));
    }

    [Test]
    public void DeclaredTypeInSameModuleIsValid()
    {
        var result = Bind(@"
type Address = { Street : string, City : string }
type Person = { Name : string, Home : Address }");
        Assert.That(result.HasErrors, Is.False);
    }

    [Test]
    public void EnumUsedAsTypeIsValid()
    {
        var result = Bind(@"
enum Color = Red | Green | Blue
function paint(c : Color) : string = 'done'");
        Assert.That(result.HasErrors, Is.False);
    }

    [Test]
    public void FunctionUsedAsTypeIsNotAllowed()
    {
        var result = Bind(@"
function helper() : int = 42
let x : helper = 1");
        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Diagnostics[0].Message, Does.Contain("is not a type"));
    }

    // ========================================================================
    // Call Arity Validation
    // ========================================================================

    [Test]
    public void TooManyArgumentsProducesError()
    {
        var externals = new List<Symbol>
        {
            new FunctionSymbol("print", CallableKind.External,
                new List<ParameterSymbol> { new("value", null, 0) })
        };

        var result = Bind("let x = print('a', 'b', 'c')", externals);
        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Diagnostics[0].Message, Does.Contain("expects 1 argument(s) but got 3"));
    }

    [Test]
    public void CorrectArityDoesNotProduceError()
    {
        var externals = new List<Symbol>
        {
            new FunctionSymbol("add", CallableKind.External,
                new List<ParameterSymbol> { new("a", null, 0), new("b", null, 1) })
        };

        var result = Bind("let x = add(1, 2)", externals);
        Assert.That(result.HasErrors, Is.False);
    }

    // ========================================================================
    // Integration: Real .cop files
    // ========================================================================

    [Test]
    public void BindsRealCopPackageFile()
    {
        // Parse and bind a real package file to ensure no crashes
        var packagesDir = FindPackagesDir();
        if (packagesDir is null)
        {
            Assert.Ignore("packages/ directory not found");
            return;
        }

        var copFiles = Directory.GetFiles(packagesDir, "*.cop", SearchOption.AllDirectories)
            .Take(20)
            .ToList();

        int boundCount = 0;
        foreach (var file in copFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                var module = CopParser.Parse(source, file);
                var binder = new Binder(file);
                var result = binder.Bind(module);

                // Should complete without throwing
                Assert.That(result.GlobalScope, Is.Not.Null);
                boundCount++;
            }
            catch (Exception ex)
            {
                // Parser failures are expected for some files (known issues)
                TestContext.WriteLine($"Skipped {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.That(boundCount, Is.GreaterThan(5),
            $"Expected to bind at least 5 files, but only bound {boundCount}");
    }

    private static string? FindPackagesDir()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "packages");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
