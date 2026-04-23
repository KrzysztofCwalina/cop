using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ApiSurfaceTests
{
    // ── StubLine formatting ──

    [Test]
    public void StubLine_Method_ReturnsVoid_UsesEmptyBraces()
    {
        var type = CreateType("MyClass");
        var method = new MethodDeclaration("DoWork", Modifier.Public, [], TR("void"), [], 10);
        var entry = ApiEntry.ForMethod(type, method);
        Assert.That(entry.StubLine, Is.EqualTo("public void DoWork() { }"));
    }

    [Test]
    public void StubLine_Method_ReturnsValue_UsesThrowNull()
    {
        var type = CreateType("MyClass");
        var method = new MethodDeclaration("GetName", Modifier.Public | Modifier.Virtual, [], TR("string"), [], 10);
        var entry = ApiEntry.ForMethod(type, method);
        Assert.That(entry.StubLine, Is.EqualTo("public virtual string GetName() { throw null; }"));
    }

    [Test]
    public void StubLine_Method_WithParameters()
    {
        var type = CreateType("MyClass");
        var method = new MethodDeclaration("Add", Modifier.Public | Modifier.Static, [], TR("int"), [
            Param("a", "int"),
            Param("b", "int")
        ], 10);
        var entry = ApiEntry.ForMethod(type, method);
        Assert.That(entry.StubLine, Is.EqualTo("public static int Add(int a, int b) { throw null; }"));
    }

    [Test]
    public void StubLine_Property_GetSet()
    {
        var type = CreateType("MyClass");
        var prop = new PropertyDeclaration("Name", TR("string"), Modifier.Public, 10)
        {
            HasGetter = true,
            HasSetter = true
        };
        var entry = ApiEntry.ForProperty(type, prop);
        Assert.That(entry.StubLine, Is.EqualTo("public string Name { get { throw null; } set { } }"));
    }

    [Test]
    public void StubLine_Property_GetOnly()
    {
        var type = CreateType("MyClass");
        var prop = new PropertyDeclaration("Count", TR("int"), Modifier.Public, 10)
        {
            HasGetter = true,
            HasSetter = false
        };
        var entry = ApiEntry.ForProperty(type, prop);
        Assert.That(entry.StubLine, Is.EqualTo("public int Count { get { throw null; } }"));
    }

    [Test]
    public void StubLine_Constructor()
    {
        var type = CreateType("MyClass");
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            Param("name", "string")
        ], 10);
        var entry = ApiEntry.ForConstructor(type, ctor);
        Assert.That(entry.StubLine, Is.EqualTo("public MyClass(string name) { }"));
    }

    [Test]
    public void StubLine_EnumValue()
    {
        var type = CreateType("Colors", TypeKind.Enum);
        var entry = ApiEntry.ForEnumValue(type, "Red");
        Assert.That(entry.StubLine, Is.EqualTo("Red,"));
    }

    [Test]
    public void StubLine_Event()
    {
        var type = CreateType("MyClass");
        var evt = new EventDeclaration("Changed", TR("EventHandler"), Modifier.Public, 10);
        var entry = ApiEntry.ForEvent(type, evt);
        Assert.That(entry.StubLine, Is.EqualTo("public event EventHandler Changed { add { } remove { } }"));
    }

    [Test]
    public void StubLine_Field_Static_Readonly()
    {
        var type = CreateType("MyClass");
        var field = new FieldDeclaration("Empty", TR("string"),
            Modifier.Public | Modifier.Static | Modifier.Readonly, 10);
        var entry = ApiEntry.ForField(type, field);
        Assert.That(entry.StubLine, Is.EqualTo("public static readonly string Empty;"));
    }

    [Test]
    public void StubLine_Type_ClassWithBaseType()
    {
        var type = CreateType("MyClient") with { BaseTypes = ["ClientBase", "IDisposable"] };
        var entry = ApiEntry.ForType(type);
        Assert.That(entry.StubLine, Is.EqualTo("public partial class MyClient : ClientBase, IDisposable"));
    }

    [Test]
    public void StubLine_Type_Interface()
    {
        var type = CreateType("IMyService", TypeKind.Interface);
        var entry = ApiEntry.ForType(type);
        Assert.That(entry.StubLine, Is.EqualTo("public partial interface IMyService"));
    }

    [Test]
    public void StubLine_Type_Enum()
    {
        var type = CreateType("Colors", TypeKind.Enum);
        var entry = ApiEntry.ForType(type);
        Assert.That(entry.StubLine, Is.EqualTo("public enum Colors"));
    }

    // ── AssemblyApiReader ──

    [Test]
    public void AssemblyApiReader_LoadsTestAssembly()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location;
        var sourceFile = AssemblyApiReader.ReadAssembly(assemblyPath);

        Assert.That(sourceFile.Types.Count, Is.GreaterThan(0));

        // Find our own test class (fully qualified name includes namespace)
        var testClass = sourceFile.Types.FirstOrDefault(t => t.Name.Contains("ApiSurfaceTests"));
        Assert.That(testClass, Is.Not.Null);
        Assert.That(testClass!.Kind, Is.EqualTo(TypeKind.Class));
    }

    [Test]
    public void AssemblyApiReader_ExtractsMethodsAndProperties()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location;
        var sourceFile = AssemblyApiReader.ReadAssembly(assemblyPath);

        var testClass = sourceFile.Types.First(t => t.Name.Contains("ApiSurfaceTests"));
        Assert.That(testClass.Methods.Count, Is.GreaterThan(0));

        var methodNames = testClass.Methods.Select(m => m.Name).ToList();
        Assert.That(methodNames, Does.Contain("AssemblyApiReader_LoadsTestAssembly"));
    }

    [Test]
    public void AssemblyApiReader_TypesHaveFullyQualifiedNames()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location;
        var sourceFile = AssemblyApiReader.ReadAssembly(assemblyPath);

        var testClass = sourceFile.Types.First(t => t.Name.Contains("ApiSurfaceTests"));
        // Full name should include namespace dot separator
        Assert.That(testClass.Name, Does.Contain("."));
    }

    // ── Code.Load() parser integration ──

    [Test]
    public void CodeLoad_ParsesCorrectly()
    {
        var cop = "let baseline = Code.Load('test.dll')";
        var script = ScriptParser.Parse(cop, "test.cop");
        var letDecl = script.LetDeclarations.First(l => l.Name == "baseline");
        Assert.That(letDecl.IsCodeLoad, Is.True);
        Assert.That(letDecl.IsValueBinding, Is.True);
    }

    [Test]
    public void CodeLoad_InterpreterResolvesCollection()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location.Replace("\\", "\\\\");
        // Use SAVE to produce file output — this avoids needing imported CHECK command
        var cop = $@"
let baseline = Code.Load('{assemblyPath}')
predicate anyApi(Api) => Api.Kind != ''
export command api-list = SAVE('api.txt', '{{Api.Signature}}', baseline:anyApi)
";

        var registry = new TypeRegistry();
        CodeTypeRegistrar.Register(registry);
        registry.RegisterProgramType();

        var codeLoader = CreateTestCodeLoader();
        var interpreter = new ScriptInterpreter(registry, externalCodeLoader: codeLoader);

        var script = ScriptParser.Parse(cop, "test.cop");
        var documents = new List<Document>();
        var result = interpreter.Run([script], documents, commandName: "api-list");

        Assert.That(result.FileOutputs.Count, Is.GreaterThan(0), "Expected file outputs");
        var apiFile = result.FileOutputs.First(f => f.Path == "api.txt");
        Assert.That(apiFile.Content.Length, Is.GreaterThan(0), "Expected api.txt content");

        // Verify some known signatures appear
        Assert.That(apiFile.Content, Does.Contain("class"));
    }

    // ── Helpers ──

    private static TypeDeclaration CreateType(string name, TypeKind kind = TypeKind.Class) =>
        new(name, kind, Modifier.Public, [], [], [], [], [], [], 1);

    private static TypeReference TR(string text) =>
        new(text, null, [], text);

    private static ParameterDeclaration Param(string name, string type) =>
        new(name, TR(type), false, false, false, 0);

    private static Func<string, List<object>> CreateTestCodeLoader()
    {
        return (string path) =>
        {
            var sourceFile = AssemblyApiReader.ReadAssembly(path);
            var entries = new List<object>();
            foreach (var type in sourceFile.Types)
            {
                entries.Add(ApiEntry.ForType(type));
                if (type.Kind == TypeKind.Enum)
                {
                    foreach (var value in type.EnumValues)
                        entries.Add(ApiEntry.ForEnumValue(type, value));
                    continue;
                }
                foreach (var ctor in type.Constructors)
                {
                    if (ctor.IsPublic || ctor.IsProtected)
                        entries.Add(ApiEntry.ForConstructor(type, ctor));
                }
                foreach (var method in type.Methods)
                {
                    if (method.IsPublic || method.IsProtected)
                        entries.Add(ApiEntry.ForMethod(type, method));
                }
                foreach (var prop in type.Properties)
                {
                    if (prop.IsPublic || prop.IsProtected)
                        entries.Add(ApiEntry.ForProperty(type, prop));
                }
                foreach (var evt in type.Events)
                {
                    if (evt.IsPublic || evt.IsProtected)
                        entries.Add(ApiEntry.ForEvent(type, evt));
                }
                foreach (var field in type.Fields)
                {
                    if (field.IsPublic || field.IsProtected)
                        entries.Add(ApiEntry.ForField(type, field));
                }
            }
            return entries;
        };
    }
}
