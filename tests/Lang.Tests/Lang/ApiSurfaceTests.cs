using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ApiSurfaceTests
{
    // ── ApiAsText formatting ──

    [Test]
    public void ApiAsText_Method_ReturnsVoid_UsesEmptyBraces()
    {
        var type = CreateType("MyClass");
        var method = new MethodDeclaration("DoWork", Modifier.Public, [], TR("void"), [], 10);
        var entry = ApiEntry.ForMethod(type, method);
        Assert.That(entry.ApiAsText, Is.EqualTo("public void DoWork() { }"));
    }

    [Test]
    public void ApiAsText_Method_ReturnsValue_UsesThrowNull()
    {
        var type = CreateType("MyClass");
        var method = new MethodDeclaration("GetName", Modifier.Public | Modifier.Virtual, [], TR("string"), [], 10);
        var entry = ApiEntry.ForMethod(type, method);
        Assert.That(entry.ApiAsText, Is.EqualTo("public virtual string GetName() { throw null; }"));
    }

    [Test]
    public void ApiAsText_Method_WithParameters()
    {
        var type = CreateType("MyClass");
        var method = new MethodDeclaration("Add", Modifier.Public | Modifier.Static, [], TR("int"), [
            Param("a", "int"),
            Param("b", "int")
        ], 10);
        var entry = ApiEntry.ForMethod(type, method);
        Assert.That(entry.ApiAsText, Is.EqualTo("public static int Add(int a, int b) { throw null; }"));
    }

    [Test]
    public void ApiAsText_Property_GetSet()
    {
        var type = CreateType("MyClass");
        var prop = new PropertyDeclaration("Name", TR("string"), Modifier.Public, 10)
        {
            HasGetter = true,
            HasSetter = true
        };
        var entry = ApiEntry.ForProperty(type, prop);
        Assert.That(entry.ApiAsText, Is.EqualTo("public string Name { get { throw null; } set { } }"));
    }

    [Test]
    public void ApiAsText_Property_GetOnly()
    {
        var type = CreateType("MyClass");
        var prop = new PropertyDeclaration("Count", TR("int"), Modifier.Public, 10)
        {
            HasGetter = true,
            HasSetter = false
        };
        var entry = ApiEntry.ForProperty(type, prop);
        Assert.That(entry.ApiAsText, Is.EqualTo("public int Count { get { throw null; } }"));
    }

    [Test]
    public void ApiAsText_Constructor()
    {
        var type = CreateType("MyClass");
        var ctor = new MethodDeclaration(".ctor", Modifier.Public, [], null, [
            Param("name", "string")
        ], 10);
        var entry = ApiEntry.ForConstructor(type, ctor);
        Assert.That(entry.ApiAsText, Is.EqualTo("public MyClass(string name) { }"));
    }

    [Test]
    public void ApiAsText_EnumValue()
    {
        var type = CreateType("Colors", TypeKind.Enum);
        var entry = ApiEntry.ForEnumValue(type, "Red");
        Assert.That(entry.ApiAsText, Is.EqualTo("Red,"));
    }

    [Test]
    public void ApiAsText_Event()
    {
        var type = CreateType("MyClass");
        var evt = new EventDeclaration("Changed", TR("EventHandler"), Modifier.Public, 10);
        var entry = ApiEntry.ForEvent(type, evt);
        Assert.That(entry.ApiAsText, Is.EqualTo("public event EventHandler Changed { add { } remove { } }"));
    }

    [Test]
    public void ApiAsText_Field_Static_Readonly()
    {
        var type = CreateType("MyClass");
        var field = new FieldDeclaration("Empty", TR("string"),
            Modifier.Public | Modifier.Static | Modifier.Readonly, 10);
        var entry = ApiEntry.ForField(type, field);
        Assert.That(entry.ApiAsText, Is.EqualTo("public static readonly string Empty;"));
    }

    [Test]
    public void ApiAsText_Type_ClassWithBaseType()
    {
        var type = CreateType("MyClient") with { BaseTypes = ["ClientBase", "IDisposable"] };
        var entry = ApiEntry.ForType(type);
        Assert.That(entry.ApiAsText, Is.EqualTo("public partial class MyClient : ClientBase, IDisposable"));
    }

    [Test]
    public void ApiAsText_Type_Interface()
    {
        var type = CreateType("IMyService", TypeKind.Interface);
        var entry = ApiEntry.ForType(type);
        Assert.That(entry.ApiAsText, Is.EqualTo("public partial interface IMyService"));
    }

    [Test]
    public void ApiAsText_Type_Enum()
    {
        var type = CreateType("Colors", TypeKind.Enum);
        var entry = ApiEntry.ForType(type);
        Assert.That(entry.ApiAsText, Is.EqualTo("public enum Colors"));
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

    // ── Load() parser integration (now uses csharp.Load provider function) ──

    [Test]
    public void Load_ParsesAsLetDeclaration()
    {
        // After removing magic, csharp.Load('test.dll') is parsed as a let declaration
        // (parser treats it as path-parameterized collection, runtime resolves as function call)
        var cop = "let baseline = csharp.Load('test.dll')";
        var script = ScriptParser.Parse(cop, "test.cop");
        var letDecl = script.LetDeclarations.First(l => l.Name == "baseline");
        Assert.That(letDecl, Is.Not.Null);
        Assert.That(letDecl.SourceExpression, Is.Not.Null);
    }

    [Test]
    public void Load_InterpreterResolvesCollection()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location.Replace("\\", "\\\\");
        var cop = $@"
let baseline = csharp.Load('{assemblyPath}')
predicate anyApi(Api) => Api.Kind != ''
let apiText = baseline.Api:anyApi.text('{{item.Signature}}')
export command api-list = save('api.txt', apiText)
";

        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        registry.RegisterProgramType();

        RegisterCSharpLoadFunction(registry);
        var interpreter = new ScriptInterpreter(registry);

        var script = ScriptParser.Parse(cop, "test.cop");
        var documents = new List<Document>();
        var result = interpreter.Run([script, TestInterpreter.CorePackage], documents, commandName: "api-list");

        Assert.That(result.FileOutputs.Count, Is.GreaterThan(0), "Expected file outputs");
        var apiFile = result.FileOutputs.First(f => f.Path == "api.txt");
        Assert.That(apiFile.Content.Length, Is.GreaterThan(0), "Expected api.txt content");

        // Verify some known signatures appear
        Assert.That(apiFile.Content, Does.Contain("class"));
    }

    [Test]
    public void Load_TypesSubCollection()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location.Replace("\\", "\\\\");
        var cop = $@"
let baseline = csharp.Load('{assemblyPath}')
let typesText = baseline.Types.text('{{item.Name}}')
export command types = save('types.txt', typesText)
";

        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        registry.RegisterProgramType();

        RegisterCSharpLoadFunction(registry);
        var interpreter = new ScriptInterpreter(registry);

        var script = ScriptParser.Parse(cop, "test.cop");
        var documents = new List<Document>();
        var result = interpreter.Run([script, TestInterpreter.CorePackage], documents, commandName: "types");

        Assert.That(result.FileOutputs.Count, Is.GreaterThan(0), "Expected file outputs");
        var typesFile = result.FileOutputs.First(f => f.Path == "types.txt");
        Assert.That(typesFile.Content.Length, Is.GreaterThan(0), "Expected types.txt content");
        // The test assembly has the ApiSurfaceTests class
        Assert.That(typesFile.Content, Does.Contain("ApiSurfaceTests"));
    }

    [Test]
    public void Load_BareReferenceUsedAsExpression()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location.Replace("\\", "\\\\");
        // Bare csharp.Load returns a DataObject — access sub-collections via .Types, .Api
        var cop = $@"
let baseline = csharp.Load('{assemblyPath}')
let typesText = baseline.Types.text('{{item.Name}}')
export command check = save('types.txt', typesText)
";

        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        registry.RegisterProgramType();

        RegisterCSharpLoadFunction(registry);
        var interpreter = new ScriptInterpreter(registry);

        var script = ScriptParser.Parse(cop, "test.cop");
        var documents = new List<Document>();
        var result = interpreter.Run([script, TestInterpreter.CorePackage], documents, commandName: "check");
        Assert.That(result.FileOutputs.Count, Is.GreaterThan(0));
    }

    // ── .text() filter and save() ──

    [Test]
    public void TextFilter_FlattensCollectionToJoinedString()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location.Replace("\\", "\\\\");
        var cop = $@"
let baseline = csharp.Load('{assemblyPath}')
predicate anyApi(Api) => Api.Kind != ''
let apiText = baseline.Api:anyApi.text('{{item.Signature}}')
export command print-text = foreach apiText => '{{item}}'
";

        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        registry.RegisterProgramType();

        RegisterCSharpLoadFunction(registry);
        var interpreter = new ScriptInterpreter(registry);

        var script = ScriptParser.Parse(cop, "test.cop");
        var documents = new List<Document>();
        var result = interpreter.Run([script], documents, commandName: "print-text");

        // .text() should produce exactly one output (the joined string)
        Assert.That(result.Outputs.Count, Is.EqualTo(1), "Expected single output from .text() flattened collection");
        var text = result.Outputs[0].Message;
        Assert.That(text, Does.Contain("class"));
        // Verify it contains multiple lines (newline-separated)
        Assert.That(text.Split('\n').Length, Is.GreaterThan(1), "Expected multiple lines in .text() output");
    }

    [Test]
    public void Save_WritesValueToFile()
    {
        var assemblyPath = typeof(ApiSurfaceTests).Assembly.Location.Replace("\\", "\\\\");
        var cop = $@"
let baseline = csharp.Load('{assemblyPath}')
predicate anyApi(Api) => Api.Kind != ''
let apiText = baseline.Api:anyApi.text('{{item.Signature}}')
export command save-api = save('api-surface.txt', apiText)
";

        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        registry.RegisterProgramType();

        RegisterCSharpLoadFunction(registry);
        var interpreter = new ScriptInterpreter(registry);

        var script = ScriptParser.Parse(cop, "test.cop");
        var documents = new List<Document>();
        var result = interpreter.Run([script, TestInterpreter.CorePackage], documents, commandName: "save-api");

        Assert.That(result.FileOutputs.Count, Is.GreaterThan(0), "Expected file output from save()");
        var apiFile = result.FileOutputs.First(f => f.Path == "api-surface.txt");
        Assert.That(apiFile.Content, Does.Contain("class"));
        // Verify the content is the joined text (not the path)
        Assert.That(apiFile.Content.Split('\n').Length, Is.GreaterThan(1), "Expected multiline content");
    }

    // ── Helpers ──

    private static TypeDeclaration CreateType(string name, TypeKind kind = TypeKind.Class) =>
        new(name, kind, Modifier.Public, [], [], [], [], [], [], 1);

    private static TypeReference TR(string text) =>
        new(text, null, [], text);

    private static ParameterDeclaration Param(string name, string type) =>
        new(name, TR(type), false, false, false, 0);

    private static void RegisterCSharpLoadFunction(TypeRegistry registry)
    {
        var extractors = CodeBindings.BuildExtractors();
        registry.RegisterProviderFunction("csharp", "Load", args =>
        {
            var path = args[0]?.ToString()!;
            var sourceFile = AssemblyApiReader.ReadAssembly(path);
            for (int i = 0; i < sourceFile.Types.Count; i++)
                sourceFile.Types[i] = sourceFile.Types[i] with { File = sourceFile };

            var obj = new DataObject("Codebase");
            obj.WithFieldResolver(fieldName =>
            {
                if (extractors.TryGetValue(fieldName, out var extractor))
                    return extractor(sourceFile);
                return null;
            });
            return Task.FromResult<object?>(obj);
        });
    }
}
