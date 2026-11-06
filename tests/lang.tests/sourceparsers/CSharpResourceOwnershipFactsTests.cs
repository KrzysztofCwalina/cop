using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public sealed class CSharpResourceOwnershipFactsTests
{
    [TestCase("return new Resource();", true)]
    [TestCase("return (new Resource());", true)]
    [TestCase("Resource item = new Resource(); return item;", true)]
    [TestCase("Resource item = new Resource(); return (item);", true)]
    [TestCase("Resource item = new Resource(); return new Container(item);", true)]
    [TestCase("Resource item = new Resource(); return new Container((item));", true)]
    [TestCase("Resource item = new Resource(); item.Value = 1; return item;", true)]
    [TestCase("_owned = new Resource(); return null;", true)]
    [TestCase("Owned = new Resource(); return null;", true)]
    [TestCase("Resource item = new Resource(); _owned = item; return null;", true)]
    [TestCase("Resource item = new Resource(); Owned = item; return null;", true)]
    [TestCase("Resource item = new Resource(); if (flag) Owned = item; return null;",
        false)]
    [TestCase("Resource item = new Resource(); if (flag) return item; return null;",
        false)]
    [TestCase("Resource item = new Resource(); if (flag) return null; return item;",
        false)]
    [TestCase("Resource item = new Resource(); if (flag) Owned = item; " +
        "else _owned = item; return null;", true)]
    [TestCase("Resource item = new Resource(); if (flag) return item; " +
        "else return new Container(item);", true)]
    [TestCase("Resource item = new Resource(); if (flag) Owned = item; return item;",
        true)]
    [TestCase("Resource item = new Resource(); Owned ??= item; return null;", false)]
    [TestCase("Resource item = new Resource(); Resource alias = item; return null;", false)]
    [TestCase("Resource item = new Resource(); Resource _owned = item; return null;", false)]
    [TestCase("Resource item = new Resource(); item = null; Owned = item; return null;", false)]
    [TestCase("Resource item = new Resource(); Consume(item); return null;", false)]
    [TestCase("Resource item = new Resource(); return Consume(item);", false)]
    [TestCase("Resource item = new Resource(); return new Container(Consume(item));", false)]
    [TestCase("Resource item = new Resource(); return new Container(new object());", false)]
    [TestCase("Resource item = new Resource(); item = null; return item;", false)]
    [TestCase("Resource item = new Resource(); Replace(ref item); return item;", false)]
    [TestCase("Resource item = new Resource(); " +
        "System.Func<object> deferred = () => { return item; }; return deferred;", false)]
    [TestCase("{ Resource item = new Resource(); Consume(item); } " +
        "{ object item = new object(); return item; }", false)]
    public async Task ReturnedOwnershipUsesTheExactCreatedLocal(string body, bool expected)
    {
        string source = $$"""
            class Resource { public int Value { get; set; } }
            class Container { public Container(object value) { } }
            class Example
            {
                bool flag;
                Resource _owned;
                Resource Owned { get; set; }
                static object Consume(object value) => value;
                static void Replace(ref Resource value) { }
                object Run() { {{body}} }
            }
            """;
        await AssertCreation(source, expected);
    }

    [TestCase("object Run() => new Resource();", true)]
    [TestCase("void Run() => new Resource();", false)]
    public async Task ExpressionBodiesMustActuallyReturnTheCreatedValue(string member, bool expected)
    {
        await AssertCreation("class Resource { } class Example { " + member + " }", expected);
    }

    [TestCase("readonly Resource _owned = new Resource();")]
    [TestCase("Resource Owned { get; } = new Resource();")]
    [TestCase("static readonly Resource _owned = new Resource();")]
    public async Task MemberInitializersTransferToTheOwningObjectOrType(string member)
    {
        await AssertCreation("class Resource { } class Example { " + member + " }", true);
    }

    [TestCase("", "")]
    [TestCase(": System.IDisposable", "public void Dispose() { }")]
    public async Task MemberEscapeDoesNotProveTheReceiverActuallyDisposes(
        string interfaces, string disposal)
    {
        await AssertCreation($$"""
            class Resource : System.IDisposable { public void Dispose() { } }
            class RequestLogger {{interfaces}}
            {
                Resource _cached;
                void Cache() { _cached = new Resource(); }
                {{disposal}}
            }
            """, true);
    }

    [Test]
    public async Task ProcessLifetimeMemberStorageDoesNotRequireADisposableReceiver()
    {
        await AssertCreation("""
            class Resource : System.IDisposable { public void Dispose() { } }
            static class ProcessCache
            {
                static readonly Resource s_client = new Resource();
            }
            """, true);
    }

    private static async Task AssertCreation(string source, bool expected)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = CSharpCompilation.Create(
            "ReturnedOwnershipControls", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty, source);
        SourceFile file = new CSharpSourceParser().ParseWithSemantics(
            "Example.cs", source, tree, compilation.GetSemanticModel(tree))!;
        CSharpStatementInfo creation = (await file.Statements.OfType<CSharpStatementInfo>().ToAsyncEnumerable()
            .SingleAsync(async (Cop.Providers.SourceModel.CSharpStatementInfo statement, CancellationToken cancellationToken) => statement.Kind == "call" && (await AsyncSourceFacts.TypeNameAsync(statement, CancellationToken.None)).TypeName == "Resource"));

        Assert.That((await AsyncSourceFacts.SemanticAsync(creation, CancellationToken.None)).IsOwnershipTransferred, Is.EqualTo(expected), source);
        Assert.That((await AsyncSourceFacts.SemanticAsync(creation, CancellationToken.None)).IsOwnershipTransferred, Is.EqualTo(expected), "repeated demand");
        Assert.That(CSharpBindings.Build().Accessors["CSharpStatement"]
            ["IsOwnershipTransferred"](creation), Is.EqualTo(expected), "runtime binding");
    }
}
