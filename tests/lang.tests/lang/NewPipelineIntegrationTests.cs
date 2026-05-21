using NUnit.Framework;
using Cop.Lang;
using Cop.Lang.Interpreter;

namespace Cop.Tests.Lang;

/// <summary>
/// Integration tests that validate the new pipeline handles real Cop language patterns.
/// These tests use patterns found in actual packages under packages/.
/// </summary>
[TestFixture]
public class NewPipelineIntegrationTests
{
    // ========================================================================
    // Real Package Patterns
    // ========================================================================

    [Test]
    public void TypeDeclarationAndFieldAccess()
    {
        // Pattern from packages/code/src/code.cop
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
type LineKind = code | comment | blank

type Line = {
    Number : int
    Text : string
    Kind : LineKind
}

command main = {
    print('type defined')
}");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("type defined"));
        Assert.That(runner.ParseErrors, Is.Empty);
    }

    [Test]
    public void PredicateDeclarationAndFilter()
    {
        // Pattern: predicate isPublic(Type) => Type.Visibility:equals('public')
        var runner = new NewPipelineRunner();

        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Foo"), ("Visibility", "public")),
            CreateObj("Type", ("Name", "Bar"), ("Visibility", "internal")),
            CreateObj("Type", ("Name", "Baz"), ("Visibility", "public")),
        };
        runner.RegisterCollection("Types", types);

        runner.LoadSource(@"
predicate isPublic(t) => t.Visibility == 'public'
command main = foreach Types:isPublic => print(item.Name)");
        runner.Run();

        Assert.That(runner.Outputs, Is.EqualTo(new[] { "Foo", "Baz" }));
    }

    [Test]
    public void FunctionWithExpressionBody()
    {
        // Pattern: function fullName(Type) = Type.Namespace + '.' + Type.Name
        var runner = new NewPipelineRunner();

        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Widget"), ("Namespace", "Acme")),
        };
        runner.RegisterCollection("Types", types);

        runner.LoadSource(@"
function fullName(t) = t.Namespace + '.' + t.Name
command main = foreach Types => print(fullName(item))");
        runner.Run();

        Assert.That(runner.Outputs[0], Is.EqualTo("Acme.Widget"));
    }

    [Test]
    public void LetBindingWithCollectionCount()
    {
        var runner = new NewPipelineRunner();

        var items = new List<DataObject>
        {
            CreateObj("File", ("Path", "a.cs")),
            CreateObj("File", ("Path", "b.cs")),
            CreateObj("File", ("Path", "c.cs")),
        };
        runner.RegisterCollection("Files", items);

        runner.LoadSource(@"
command main = {
    let count = Files.Count
    print('Found ' + count + ' files')
}");
        runner.Run();

        Assert.That(runner.Outputs[0], Is.EqualTo("Found 3 files"));
    }

    [Test]
    public void InterpolatedStringTemplate()
    {
        // Pattern: print('{item.Name} ({item.Kind})')
        var runner = new NewPipelineRunner();

        var items = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Widget"), ("Kind", "Class")),
            CreateObj("Type", ("Name", "IService"), ("Kind", "Interface")),
        };
        runner.RegisterCollection("Types", items);

        runner.LoadSource(@"
command main = foreach Types => print('{item.Name} ({item.Kind})')");
        runner.Run();

        Assert.That(runner.Outputs, Is.EqualTo(new[] { "Widget (Class)", "IService (Interface)" }));
    }

    [Test]
    public void MultiplePredicatesComposed()
    {
        // Pattern: predicate isPublicClass(Type:isPublic:isClass) => ...
        var runner = new NewPipelineRunner();

        var types = new List<DataObject>
        {
            CreateObj("Type", ("Name", "Foo"), ("Visibility", "public"), ("Kind", "Class")),
            CreateObj("Type", ("Name", "Bar"), ("Visibility", "internal"), ("Kind", "Class")),
            CreateObj("Type", ("Name", "IBaz"), ("Visibility", "public"), ("Kind", "Interface")),
        };
        runner.RegisterCollection("Types", types);

        runner.LoadSource(@"
predicate isPublic(t) => t.Visibility == 'public'
predicate isClass(t) => t.Kind == 'Class'
predicate isPublicClass(t) => isPublic(t) && isClass(t)
command main = foreach Types:isPublicClass => print(item.Name)");
        runner.Run();

        Assert.That(runner.Outputs, Is.EqualTo(new[] { "Foo" }));
    }

    [Test]
    public void ConditionalExpression()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
function label(n : int) : string = n > 10 ? 'big' : 'small'
command main = {
    print(label(5))
    print(label(15))
}");
        runner.Run();
        Assert.That(runner.Outputs, Is.EqualTo(new[] { "small", "big" }));
    }

    [Test]
    public void ListLiteralAndForEach()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
let items = ['alpha', 'beta', 'gamma']
command main = foreach items => print(item)");
        runner.Run();
        Assert.That(runner.Outputs, Is.EqualTo(new[] { "alpha", "beta", "gamma" }));
    }

    [Test]
    public void NestedFunctionCalls()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
function add(a : int, b : int) : int = a + b
function double(n : int) : int = n + n
command main = print(double(add(3, 4)))");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("14"));
    }

    [Test]
    public void FilterWithNegation()
    {
        var runner = new NewPipelineRunner();

        var items = new List<DataObject>
        {
            CreateObj("Item", ("Name", "A"), ("Active", true)),
            CreateObj("Item", ("Name", "B"), ("Active", false)),
            CreateObj("Item", ("Name", "C"), ("Active", true)),
        };
        runner.RegisterCollection("items", items);

        runner.LoadSource(@"
predicate isActive(x) => x.Active == true
command main = foreach items:!isActive => print(item.Name)");
        runner.Run();

        Assert.That(runner.Outputs, Is.EqualTo(new[] { "B" }));
    }

    [Test]
    public void RecursiveFunction()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
function factorial(n : int) : int = n <= 1 ? 1 : n * factorial(n - 1)
command main = print(factorial(5))");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("120"));
    }

    [Test]
    public void MultipleFiles()
    {
        // Simulates loading multiple .cop files from a package (like core + checks)
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
function prefix(s : string) : string = '[INFO] ' + s
", "utils.cop");
        runner.LoadSource(@"
command main = print(prefix('hello'))
", "main.cop");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("[INFO] hello"));
    }

    [Test]
    public void EnumDeclaration()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
enum Severity = Error | Warning | Info
command main = print('enum loaded')");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("enum loaded"));
    }

    [Test]
    public void LambdaExpression()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
let double = (n) => n + n
command main = print(double(21))");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("42"));
    }

    [Test]
    public void ObjectLiteral()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
command main = {
    let obj = { Name = 'test', Value = 42 }
    print(obj.Name + ' = ' + obj.Value)
}");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("test = 42"));
    }

    [Test]
    public void CommandWithBlockBody()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
command main = {
    let x = 10
    let y = 20
    print(x + y)
}");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("30"));
    }

    [Test]
    public void ExportedFunctionsAreAccessible()
    {
        var runner = new NewPipelineRunner();
        runner.LoadSource(@"
export function greet(name : string) : string = 'Hello ' + name
command main = print(greet('World'))");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("Hello World"));
    }

    [Test]
    public void CollectionDotCount()
    {
        var runner = new NewPipelineRunner();

        var items = new List<DataObject>
        {
            CreateObj("Item", ("X", 1)),
            CreateObj("Item", ("X", 2)),
        };
        runner.RegisterCollection("items", items);

        runner.LoadSource(@"
command main = {
    let n = items.Count
    print(n)
}");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("2"));
    }

    [Test]
    public void FilterThenCount()
    {
        var runner = new NewPipelineRunner();

        var items = new List<DataObject>
        {
            CreateObj("Item", ("Score", 5)),
            CreateObj("Item", ("Score", 15)),
            CreateObj("Item", ("Score", 25)),
        };
        runner.RegisterCollection("items", items);

        runner.LoadSource(@"
predicate highScore(i) => i.Score > 10
command main = {
    let filtered = items:highScore
    print(filtered.Count)
}");
        runner.Run();
        Assert.That(runner.Outputs[0], Is.EqualTo("2"));
    }

    [Test]
    public void MemberAccessOnFilteredCollectionItems()
    {
        var runner = new NewPipelineRunner();

        var items = new List<DataObject>
        {
            CreateObj("Person", ("Name", "Alice"), ("Age", 30)),
            CreateObj("Person", ("Name", "Bob"), ("Age", 17)),
            CreateObj("Person", ("Name", "Carol"), ("Age", 25)),
        };
        runner.RegisterCollection("people", items);

        runner.LoadSource(@"
predicate isAdult(p) => p.Age >= 18
command main = foreach people:isAdult => print(item.Name + ' is ' + item.Age)");
        runner.Run();

        Assert.That(runner.Outputs, Is.EqualTo(new[] { "Alice is 30", "Carol is 25" }));
    }

    [Test]
    public void BooleanFieldFilterDirect()
    {
        var runner = new NewPipelineRunner();

        var items = new List<DataObject>
        {
            CreateObj("Type", ("Name", "PublicWidget"), ("IsPublic", true)),
            CreateObj("Type", ("Name", "InternalService"), ("IsPublic", false)),
        };
        runner.RegisterCollection("Types", items);

        runner.LoadSource(@"
predicate isPublic(t) => t.IsPublic == true
command main = foreach Types:isPublic => print(item.Name)");
        runner.Run();

        Assert.That(runner.Outputs, Is.EqualTo(new[] { "PublicWidget" }));
    }

    [Test]
    public void ParsesAllCorePackageFiles()
    {
        // Ensure the new parser can parse all files in packages/core/src/
        var coreDir = FindPackageDir("core");
        if (coreDir is null)
        {
            Assert.Ignore("packages/core/src/ not found");
            return;
        }

        var errors = ParseDirectoryOnly(coreDir);
        Assert.That(errors, Is.Empty, $"Parse errors: {string.Join("\n", errors)}");
    }

    [Test]
    public void ParsesCodePackageFiles()
    {
        var codeDir = FindPackageDir("code");
        if (codeDir is null)
        {
            Assert.Ignore("packages/code/src/ not found");
            return;
        }

        var errors = ParseDirectoryOnly(codeDir);
        Assert.That(errors, Is.Empty, $"Parse errors: {string.Join("\n", errors)}");
    }

    [Test]
    public void ParsesFilesPackageFiles()
    {
        var filesDir = FindPackageDir("files");
        if (filesDir is null)
        {
            Assert.Ignore("packages/files/src/ not found");
            return;
        }

        var errors = ParseDirectoryOnly(filesDir);
        Assert.That(errors, Is.Empty, $"Parse errors: {string.Join("\n", errors)}");
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static DataObject CreateObj(string type, params (string Key, object? Value)[] fields)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in fields)
            dict[k] = v;
        return new DataObject(type, dict);
    }

    private static List<string> ParseDirectoryOnly(string srcDir)
    {
        var errors = new List<string>();
        var copFiles = Directory.GetFiles(srcDir, "*.cop");
        Array.Sort(copFiles, StringComparer.Ordinal);

        foreach (var file in copFiles)
        {
            try
            {
                var source = File.ReadAllText(file);
                Cop.Lang.Parser.CopParser.Parse(source, file);
            }
            catch (Exception ex)
            {
                errors.Add($"{file}: {ex.Message}");
            }
        }
        return errors;
    }

    private static string? FindPackageDir(string packageName)
    {
        // Walk up from test assembly location to find packages/
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        for (int i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir!, "packages", packageName, "src");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
            if (dir is null) break;
        }
        return null;
    }
}
