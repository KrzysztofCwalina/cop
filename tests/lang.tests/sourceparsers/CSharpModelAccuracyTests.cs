using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public sealed class CSharpModelAccuracyTests
{
    [Test]
    public async Task StatementAdapterCopiesPreserveDeferredFactsAndGraphIdentity()
    {
        int preparations = 0;
        int bindings = 0;
        TypeReference resultType = new("Result", null, [], "Result");
        TypeReference receiverType = new("Receiver", null, [], "Receiver");
        SourceFile file = new("Example.cs", "csharp", [], [], string.Empty);
        MethodDeclaration method = new("Run", Modifier.Public, [], null, [], 1);
        StatementInfo parent = new("if", [], null, null, [], 1, true);
        StatementInfo child = new("return", [], null, null, [], 3, true);
        StatementInfo source = new("call", ["await"], "Receiver", "Run", ["1"], 2, true)
        {
            File = file, Method = method, Parent = parent, MutableChildren = [child],
            Column = 5, EndLine = 2, EndColumn = 19,
            ConstructedTypeInterfaces = ["IDisposable"]
        };
        source.DeferSemanticAnalysisAsync(new(async cancellationToken =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            preparations++;
            source.ValueType = resultType;
            return true;
        }));
        source.DeferSemanticAnalysis(new(() =>
        {
            bindings++;
            source.ReceiverType = receiverType;
            return true;
        }));

        CSharpStatementInfo copy = new(source);

        Assert.That(preparations, Is.Zero);
        Assert.That(bindings, Is.Zero);
        Assert.That(copy.File, Is.SameAs(file));
        Assert.That(copy.Method, Is.SameAs(method));
        Assert.That(copy.Parent, Is.SameAs(parent));
        Assert.That(copy.Children, Is.SameAs(source.Children));
        Assert.That(copy.Arguments, Is.SameAs(source.Arguments));
        Assert.That(copy.Line, Is.EqualTo(2));
        Assert.That(copy.Column, Is.EqualTo(5));
        Assert.That(copy.EndLine, Is.EqualTo(2));
        Assert.That(copy.EndColumn, Is.EqualTo(19));
        Assert.That(copy.ConstructedTypeInterfaces, Is.EqualTo(new[] { "IDisposable" }));

        await copy.EnsureSemanticAnalysisAsync();
        Assert.That(copy.ValueType, Is.SameAs(resultType));
        Assert.That(copy.ReceiverType, Is.SameAs(receiverType));
        await copy.EnsureSemanticAnalysisAsync();
        Assert.That(copy.ValueType, Is.SameAs(resultType));
        Assert.That(preparations, Is.EqualTo(1));
        Assert.That(bindings, Is.EqualTo(1));
    }

    [Test]
    public void StatementAdapterCopiesPreserveDeferredInferredTypeNames()
    {
        int inferences = 0;
        StatementInfo source = new("declaration", [], null, "value", [], 1, true);
        source.DeferTypeNameAnalysis(new(() =>
        {
            inferences++;
            source.TypeName = "Resolved";
            return true;
        }));

        CSharpStatementInfo copy = new(source);

        Assert.That(inferences, Is.Zero);
        Assert.That(copy.TypeName, Is.EqualTo("Resolved"));
        Assert.That(copy.TypeName, Is.EqualTo("Resolved"));
        Assert.That(inferences, Is.EqualTo(1));
    }

    [Test]
    public async Task StatementAdapterCopiesPreserveAsyncTypeNamePreparation()
    {
        int preparations = 0;
        StatementInfo source = new("declaration", [], null, "value", [], 1, true);
        source.DeferTypeNamePreparationAsync(new(async cancellationToken =>
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            preparations++;
            source.TypeName = "Prepared";
            return true;
        }));
        CSharpStatementInfo copy = new(source);

        Assert.That(preparations, Is.Zero);
        await copy.EnsureTypeNameAnalysisAsync();
        Assert.That(copy.TypeName, Is.EqualTo("Prepared"));
        await copy.EnsureTypeNameAnalysisAsync();
        Assert.That(copy.TypeName, Is.EqualTo("Prepared"));
        Assert.That(preparations, Is.EqualTo(1));
    }

    [Test]
    public void MismatchedSemanticModelsFailInsteadOfReturningIncompleteFacts()
    {
        const string source = "class Example { }";
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        SyntaxTree other = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create("ModelMismatch",
            [other], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            new CSharpSourceParser().ParseWithSemantics(
                "Example.cs", source, tree, compilation.GetSemanticModel(other)))!;

        Assert.That(error.ParamName, Is.EqualTo("model"));
        Assert.That(error.Message, Does.Contain("supplied syntax tree"));
    }

    [Test]
    public async Task TargetTypedConstructionsPreserveNamespaceAndGenericArity()
    {
        const string source = """
            namespace Positive
            {
                class Cache<TKey, TValue> { }
                class Uses
                {
                    Cache<string, int> own = new();
                    Other.Cache<string, int> other = new();
                    Other.Cache<int> differentArity = new();
                }
            }
            namespace Other
            {
                class Cache<TKey, TValue> { }
                class Cache<T> { }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create("TargetTypedControls",
            [tree], [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty);
        SourceFile file = new CSharpSourceParser().ParseWithSemantics(
            "Example.cs", source, tree, compilation.GetSemanticModel(tree))!;
        StatementInfo[] constructors = file.Statements
            .Where(statement => statement.Kind == "call" && statement.MemberName is null).ToArray();

        Assert.That((await constructors.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo statement, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(statement, CancellationToken.None)).ValueType is { } type
            ? $"{type.Namespace}.{type.Name}`{type.GenericArguments.Count}" : "unknown").ToArrayAsync()),
            Is.EqualTo(new[] { "Positive.Cache`2", "Other.Cache`2", "Other.Cache`1" }));
        Assert.That((await AsyncSourceFacts.SemanticAsync(constructors[0], CancellationToken.None)).ValueType!.GenericArguments.Select(type => type.Name),
            Is.EqualTo(new[] { "String", "Int32" }));
    }

    [Test]
    public void RecordsAndPrimaryConstructorsExposeTheirExistingInstanceApiFacts()
    {
        const string source = """
            record RecordState(int Count) { public static void Reset() { } }
            class ConstructorState(int count) { public static void Reset() { } }
            class Utility { public static void Reset() { } }
            """;
        SourceFile file = new CSharpSourceParser().Parse("Example.cs", source)!;
        CSharpTypeDeclaration[] types = file.Types.Cast<CSharpTypeDeclaration>().ToArray();

        Assert.That(types.Select(type => (type.Name, type.IsRecord, type.HasPrimaryConstructor)),
            Is.EqualTo(new[]
            {
                ("RecordState", true, true),
                ("ConstructorState", false, true),
                ("Utility", false, false)
            }));
        RuntimeBindings bindings = CSharpBindings.Build();
        Assert.That(bindings.Accessors["CSharpType"]["IsRecord"](types[0]), Is.True);
        Assert.That(bindings.Accessors["CSharpType"]["HasPrimaryConstructor"](types[1]), Is.True);
    }
}
