using System.Reflection;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class SourceCacheSerializerTests
{
    private static readonly Type SerializerType = typeof(CodeCollectionBuilder).Assembly
        .GetType("Cop.Providers.SourceCacheSerializer", throwOnError: true)!;

    [Test]
    public void SaveAndTryLoad_RoundTripsSourceModel()
    {
        var cachePath = CreateCachePath();
        var fingerprint = new byte[] { 1, 2, 3, 4, 5 };
        var sourceFiles = new List<SourceFile> { CreateSourceFile() };

        try
        {
            Save(cachePath, fingerprint, sourceFiles);

            var loaded = TryLoad(cachePath, fingerprint);

            Assert.That(loaded, Is.Not.Null);
            Assert.That(loaded, Has.Count.EqualTo(1));

            var file = loaded![0];
            Assert.That(file.Path, Is.EqualTo("src/Test.cs"));
            Assert.That(file.Language, Is.EqualTo("csharp"));
            Assert.That(file.RawText, Is.EqualTo("class SampleType {}"));
            Assert.That(file.Usings, Is.EqualTo(new[] { "System", "System.Collections.Generic" }));
            Assert.That(file.Namespace, Is.EqualTo("Cop.Tests"));
            Assert.That(file.CommentLines.OrderBy(x => x).ToArray(), Is.EqualTo(new[] { 2, 5, 8 }));
            Assert.That(file.Regions, Has.Count.EqualTo(1));
            Assert.That(file.Regions[0].Name, Is.EqualTo("Helpers"));
            Assert.That(file.Regions[0].StartLine, Is.EqualTo(30));
            Assert.That(file.Regions[0].EndLine, Is.EqualTo(40));
            Assert.That(file.Regions[0].Content, Is.EqualTo("region body"));
            Assert.That(file.Regions[0].File, Is.Null);

            Assert.That(file.Types, Has.Count.EqualTo(1));
            var type = file.Types[0];
            Assert.That(type.Name, Is.EqualTo("SampleType"));
            Assert.That(type.Kind, Is.EqualTo(TypeKind.Class));
            Assert.That(type.Modifiers, Is.EqualTo(Modifier.Public | Modifier.Abstract));
            Assert.That(type.BaseTypes, Is.EqualTo(new[] { "BaseType", "IDisposable" }));
            Assert.That(type.Decorators, Is.EqualTo(new[] { "Serializable" }));
            Assert.That(type.Line, Is.EqualTo(10));
            Assert.That(type.HasDocComment, Is.True);
            Assert.That(type.DocComment, Is.EqualTo("Type docs"));
            Assert.That(type.File, Is.Null);
            Assert.That(type.EnumValues, Is.EqualTo(new[] { "One", "Two" }));
            Assert.That(type.Fields, Has.Count.EqualTo(1));
            Assert.That(type.Fields[0].Name, Is.EqualTo("_value"));
            Assert.That(type.Fields[0].Type?.OriginalText, Is.EqualTo("List<string>"));
            Assert.That(type.Properties, Has.Count.EqualTo(1));
            Assert.That(type.Properties[0].HasGetter, Is.True);
            Assert.That(type.Properties[0].HasSetter, Is.True);
            Assert.That(type.Properties[0].HasDocComment, Is.True);
            Assert.That(type.Properties[0].DocComment, Is.EqualTo("Property docs"));
            Assert.That(type.Events, Has.Count.EqualTo(1));
            Assert.That(type.Events[0].Name, Is.EqualTo("Changed"));
            Assert.That(type.NestedTypes, Has.Count.EqualTo(1));
            Assert.That(type.NestedTypes[0].Name, Is.EqualTo("Nested"));
            Assert.That(type.NestedTypes[0].Kind, Is.EqualTo(TypeKind.Struct));
            Assert.That(type.Constructors, Has.Count.EqualTo(1));
            Assert.That(type.Constructors[0].Parameters[0].DefaultValueText, Is.EqualTo("0"));
            Assert.That(type.Methods, Has.Count.EqualTo(1));

            var method = type.Methods[0];
            Assert.That(method.Name, Is.EqualTo("Run"));
            Assert.That(method.Modifiers, Is.EqualTo(Modifier.Public | Modifier.Async | Modifier.Virtual));
            Assert.That(method.Decorators, Is.EqualTo(new[] { "Benchmark" }));
            Assert.That(method.ReturnType?.OriginalText, Is.EqualTo("Task<string>"));
            Assert.That(method.ReturnType?.GenericArguments[0].Name, Is.EqualTo("string"));
            Assert.That(method.Parameters, Has.Count.EqualTo(2));
            Assert.That(method.Parameters[0].Name, Is.EqualTo("items"));
            Assert.That(method.Parameters[0].Type?.OriginalText, Is.EqualTo("List<string>"));
            Assert.That(method.Parameters[0].HasDefaultValue, Is.False);
            Assert.That(method.Parameters[1].IsVariadic, Is.True);
            Assert.That(method.Parameters[1].IsKwargs, Is.True);
            Assert.That(method.Parameters[1].DefaultValueText, Is.EqualTo("[]"));
            Assert.That(method.HasDocComment, Is.True);
            Assert.That(method.DocComment, Is.EqualTo("Method docs"));
            Assert.That(method.Statements, Has.Count.EqualTo(1));

            var ifStatement = method.Statements[0];
            Assert.That(ifStatement.Kind, Is.EqualTo("if"));
            Assert.That(ifStatement.IsBraced, Is.True);
            Assert.That(ifStatement.Condition, Is.EqualTo("items.Count > 0"));
            Assert.That(ifStatement.Method, Is.Null);
            Assert.That(ifStatement.Parent, Is.Null);
            Assert.That(ifStatement.File, Is.Null);
            Assert.That(ifStatement.CopIgnore, Is.EqualTo("ignore-rule"));
            Assert.That(ifStatement.Children, Has.Count.EqualTo(1));

            var childCall = ifStatement.Children[0];
            Assert.That(childCall.Kind, Is.EqualTo("call"));
            Assert.That(childCall.TypeName, Is.EqualTo("logger"));
            Assert.That(childCall.MemberName, Is.EqualTo("Log"));
            Assert.That(childCall.Arguments, Is.EqualTo(new[] { "items" }));
            Assert.That(childCall.Expression, Is.EqualTo("logger.Log(items)"));
            Assert.That(childCall.Method, Is.Null);
            Assert.That(childCall.Parent, Is.Null);
            Assert.That(childCall.File, Is.Null);

            Assert.That(file.Statements, Has.Count.EqualTo(3));
            Assert.That(file.Statements[0].Kind, Is.EqualTo("if"));
            Assert.That(file.Statements[1].Kind, Is.EqualTo("call"));
            Assert.That(file.Statements[2].Kind, Is.EqualTo("throw"));
            Assert.That(file.Statements[2].HasRethrow, Is.True);
            Assert.That(file.Statements[2].IsErrorHandler, Is.True);
            Assert.That(file.Statements[2].IsGenericErrorHandler, Is.True);
            Assert.That(file.Statements[2].Expression, Is.EqualTo("throw;"));
        }
        finally
        {
            CleanupCachePath(cachePath);
        }
    }

    [Test]
    public void TryLoad_ReturnsNull_WhenFingerprintDoesNotMatch()
    {
        var cachePath = CreateCachePath();

        try
        {
            Save(cachePath, new byte[] { 1, 2, 3 }, new List<SourceFile> { CreateSourceFile() });

            var loaded = TryLoad(cachePath, new byte[] { 9, 9, 9 });

            Assert.That(loaded, Is.Null);
        }
        finally
        {
            CleanupCachePath(cachePath);
        }
    }

    [Test]
    public void TryLoad_ReturnsNull_ForCorruptCache()
    {
        var cachePath = CreateCachePath();

        try
        {
            var directory = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(cachePath, new byte[] { 1, 2, 3, 4, 5 });

            var loaded = TryLoad(cachePath, new byte[] { 1, 2, 3 });

            Assert.That(loaded, Is.Null);
        }
        finally
        {
            CleanupCachePath(cachePath);
        }
    }

    private static SourceFile CreateSourceFile()
    {
        var listOfString = new TypeReference("List", "System.Collections.Generic", [new TypeReference("string", null, [], "string")], "List<string>");
        var eventHandler = new TypeReference("EventHandler", "System", [], "EventHandler");
        var taskOfString = new TypeReference("Task", "System.Threading.Tasks", [new TypeReference("string", null, [], "string")], "Task<string>");
        var intType = new TypeReference("int", null, [], "int");

        var constructor = new MethodDeclaration(
            ".ctor",
            Modifier.Public,
            [],
            null,
            [new ParameterDeclaration("count", intType, false, false, true, 11) { DefaultValueText = "0" }],
            11);

        var method = new MethodDeclaration(
            "Run",
            Modifier.Public | Modifier.Async | Modifier.Virtual,
            ["Benchmark"],
            taskOfString,
            [
                new ParameterDeclaration("items", listOfString, false, false, false, 13),
                new ParameterDeclaration("options", null, true, true, true, 14) { DefaultValueText = "[]" }
            ],
            15)
        {
            HasDocComment = true,
            DocComment = "Method docs"
        };

        var ifStatement = new StatementInfo("if", ["if"], null, null, [], 16, true)
        {
            Method = method,
            IsBraced = true,
            Condition = "items.Count > 0",
            CopIgnore = "ignore-rule"
        };

        var callStatement = new StatementInfo("call", [], "logger", "Log", ["items"], 17, true)
        {
            Method = method,
            Parent = ifStatement,
            Expression = "logger.Log(items)",
            CopIgnore = "ignore-rule"
        };
        ifStatement._children.Add(callStatement);
        method.Statements = [ifStatement];

        var throwStatement = new StatementInfo("throw", [], "InvalidOperationException", null, [], 18, false)
        {
            HasRethrow = true,
            IsErrorHandler = true,
            IsGenericErrorHandler = true,
            Expression = "throw;"
        };

        var type = new TypeDeclaration(
            "SampleType",
            TypeKind.Class,
            Modifier.Public | Modifier.Abstract,
            ["BaseType", "IDisposable"],
            ["Serializable"],
            [constructor],
            [method],
            [new TypeDeclaration("Nested", TypeKind.Struct, Modifier.Private, [], [], [], [], [], [], 20)],
            ["One", "Two"],
            10)
        {
            HasDocComment = true,
            DocComment = "Type docs",
            Fields = [new FieldDeclaration("_value", listOfString, Modifier.Private | Modifier.Readonly, 12)],
            Properties =
            [
                new PropertyDeclaration("Name", new TypeReference("string", null, [], "string"), Modifier.Public, 13)
                {
                    HasGetter = true,
                    HasSetter = true,
                    HasDocComment = true,
                    DocComment = "Property docs"
                }
            ],
            Events = [new EventDeclaration("Changed", eventHandler, Modifier.Public, 14)]
        };

        var file = new SourceFile(
            "src/Test.cs",
            "csharp",
            [type],
            [ifStatement, callStatement, throwStatement],
            "class SampleType {}")
        {
            Usings = ["System", "System.Collections.Generic"],
            Namespace = "Cop.Tests",
            Regions = [new RegionInfo("Helpers", 30, 40, "region body")],
            CommentLines = [8, 2, 5]
        };

        file.Types[0] = file.Types[0] with { File = file };
        file.Regions[0] = file.Regions[0] with { File = file };
        foreach (var statement in file.Statements)
            statement.File = file;

        return file;
    }

    private static void Save(string cachePath, byte[] fingerprint, List<SourceFile> sourceFiles)
    {
        SerializerType.GetMethod("Save", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [cachePath, fingerprint, sourceFiles]);
    }

    private static List<SourceFile>? TryLoad(string cachePath, byte[] fingerprint)
    {
        return (List<SourceFile>?)SerializerType
            .GetMethod("TryLoad", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
            .Invoke(null, [cachePath, fingerprint]);
    }

    private static string CreateCachePath()
    {
        var baseDirectory = Path.Combine(TestContext.CurrentContext.WorkDirectory, nameof(SourceCacheSerializerTests), Guid.NewGuid().ToString("N"));
        return Path.Combine(baseDirectory, "source-cache.bin");
    }

    private static void CleanupCachePath(string cachePath)
    {
        var directory = Path.GetDirectoryName(cachePath);
        if (directory is not null && Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);
    }
}
