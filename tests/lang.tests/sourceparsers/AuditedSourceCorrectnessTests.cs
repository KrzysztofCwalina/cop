using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class AuditedSourceCorrectnessTests
{
    [Test]
    public void CSharp_ConditionalGenericAndNestedConstructors_HaveIndependentFacts()
    {
        const string text = """
            interface IA {} interface IB {}
            class A : IA {} class B : IB {}
            class C { void Run(C client) { client?.Ping(); Generic<int>(); Outer(new A(), new B()); }
            void Ping() {} void Generic<T>() {} void Outer(object a, object b) {} }
            """;
        var tree = CSharpSyntaxTree.ParseText(text);
        var model = CSharpCompilation.Create("sample", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)).GetSemanticModel(tree);
        var file = new CSharpSourceParser().ParseWithSemantics("sample.cs", text, tree, model)!;
        var calls = file.Statements.Where(s => s.Kind == "call").ToArray();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new string?[] { "Ping", "Generic", "Outer", null, null }));
        Assert.That(calls[0].CallTarget?.Kind, Is.EqualTo("memberAccess"));
        Assert.That(calls[0].CallTarget?.Target?.Name, Is.EqualTo("client"));
        Assert.That(calls.Select(s => s.ConstructedTypeInterfaces.ToArray()),
            Is.EqualTo(new[] { Array.Empty<string>(), [], [], new[] { "IA" }, new[] { "IB" } }));
        Assert.That(calls.Select(s => s.Method?.Name), Is.EqualTo(Enumerable.Repeat("Run", 5)));
        Assert.That(calls[3].Parent, Is.SameAs(calls[2]));
        Assert.That(calls[4].Parent, Is.SameAs(calls[2]));
    }

    [Test]
    public void CSharp_SameLineMethodsAndNestedTypes_UseSpanIdentity()
    {
        const string text = """
            interface IA {} interface IB {}
            class Outer : IA { class Inner : IB {} void First() { Outer(Inner()); } void Second() { Other(Nested()); } }
            """;
        var tree = CSharpSyntaxTree.ParseText(text);
        var model = CSharpCompilation.Create("sample", [tree],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)]).GetSemanticModel(tree);
        var file = new CSharpSourceParser().ParseWithSemantics("sample.cs", text, tree, model)!;
        var type = file.Types.Single(t => t.Name == "Outer");
        Assert.That(type.Interfaces, Is.EqualTo(new[] { "IA" }));
        Assert.That(type.NestedTypes.Single().Interfaces, Is.EqualTo(new[] { "IB" }));
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => (s.MemberName, s.Method?.Name)),
            Is.EqualTo(new[] { ("Outer", "First"), ("Inner", "First"), ("Other", "Second"), ("Nested", "Second") }));
    }

    [Test]
    public void CSharp_FinallyLoopsLocalsAndAccessors_AreStructured()
    {
        var file = new CSharpSourceParser().Parse("sample.cs", """
            class C {
                int Value { get { int found = Read(); return found; } }
                void Run() {
                    try {} catch { throw new System.Exception(); } finally { int cleanup = 1; Cleanup(); }
                    do { continue; } while (Ready());
                    foreach (var (a,b) in Pairs()) { break; }
                    done: Ping();
                    void Local() { int local = Read(); }
                }
            }
            """)!;
        Assert.That(file.Statements.Where(s => s.Kind == "catch").Select(s => s.HasRethrow), Is.EqualTo(new[] { false }));
        Assert.That(file.Statements.Single(s => s.MemberName == "cleanup").Parent?.Kind, Is.EqualTo("finally"));
        Assert.That(file.Statements.Single(s => s.Kind == "continue").Parent?.Kind, Is.EqualTo("do"));
        Assert.That(file.Statements.Single(s => s.Kind == "break").Parent?.Kind, Is.EqualTo("foreach"));
        Assert.That(file.Statements.Single(s => s.MemberName == "Ping").Parent?.Kind, Is.EqualTo("label"));
        Assert.That(file.Statements.Single(s => s.MemberName == "found").Method?.Name, Is.EqualTo("get_Value"));
        Assert.That(file.Statements.Single(s => s.MemberName == "local").Method?.Name, Is.EqualTo("Local"));
        Assert.That(file.Types.Single().Methods.Select(m => m.Name), Is.EqualTo(new[] { "get_Value", "Run", "Local" }));
    }

    [Test]
    public void CSharp_GenericsOptionalNamespacesAndImports_AreNeutralFacts()
    {
        var file = new CSharpSourceParser().Parse("sample.cs", """
            namespace First { using Alias = System.String; class A<T> { void Run<U>(int value = 2) {} } }
            namespace Second { using static System.Math; class B {} }
            """)!;
        Assert.That(file.Types.Select(t => t.Namespace), Is.EqualTo(new[] { "First", "Second" }));
        Assert.That(file.Types[0].TypeParameters, Is.EqualTo(new[] { "T" }));
        var method = file.Types[0].Methods.Single();
        Assert.That(method.TypeParameters, Is.EqualTo(new[] { "U" }));
        Assert.That(method.Parameters.Single().IsOptional, Is.True);
        Assert.That(file.Imports.Select(i => (i.Module, i.Alias, i.IsStatic)),
            Is.EqualTo(new[] { ("System.String", (string?)"Alias", false), ("System.Math", (string?)null, true) }));
        Assert.That((method.Line, method.Column, method.EndLine, method.EndColumn), Is.EqualTo((1, 61, 1, 90)));
    }

    [Test]
    public void CSharp_ProjectBoundariesPreventUnreferencedSymbolLeakage_OnBothQueryPaths()
    {
        var root = TemporaryDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "first"));
            Directory.CreateDirectory(Path.Combine(root, "second"));
            File.WriteAllText(Path.Combine(root, "first", "first.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "second", "second.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "first", "First.cs"), "namespace Shared; public class OnlyFirst { public static void Make() {} }");
            File.WriteAllText(Path.Combine(root, "second", "Second.cs"), "class Second { void Run() { Shared.OnlyFirst.Make(); } }");
            var query = new ProviderQuery { RootPath = root };
            var eager = (Dictionary<string, List<object>>)new CSharpProvider().Query(query)!;
            var deferred = (DeferredProviderResult)new CSharpProvider().QueryDeferred(query);
            foreach (var statements in new[] { eager["Statements"], deferred.Collections["Statements"].ToList() })
                Assert.That(statements.Cast<StatementInfo>().Single(s => s.MemberName == "Make").TypeName, Is.EqualTo("Shared.OnlyFirst"),
                    "Unresolved source target must not resolve against the unrelated project.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void CSharp_AssemblyApiIncludesProtectedMembersAndOperators_NotPrivateMembers()
    {
        var root = TemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "surface.dll");
            var tree = CSharpSyntaxTree.ParseText("""
                public class Surface {
                    protected Surface() {}
                    protected void Family() {}
                    private void Hidden() {}
                    protected int Value { get; set; }
                    protected int Field;
                    protected class Nested {}
                    public static Surface operator +(Surface a, Surface b) => a;
                }
                """);
            var compilation = CSharpCompilation.Create("surface", [tree],
                [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
            Assert.That(compilation.Emit(path).Success, Is.True);
            var type = AssemblyApiReader.ReadAssembly(path).Types.Single();
            Assert.That(type.Constructors.Select(c => c.Modifiers), Is.EqualTo(new[] { Modifier.Protected }));
            Assert.That(type.Methods.Select(m => m.Name), Is.EquivalentTo(new[] { "Family", "op_Addition" }));
            Assert.That(type.Properties.Select(p => p.Name), Is.EqualTo(new[] { "Value" }));
            Assert.That(type.Fields.Select(f => f.Name), Is.EqualTo(new[] { "Field" }));
            Assert.That(type.NestedTypes.Select(t => t.Name), Is.EqualTo(new[] { "Surface+Nested" }));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void Cop_MalformedSourceReportsDiagnosticsInsteadOfEmptySuccess()
    {
        var file = new CopSourceParser().Parse("broken.cop", "function broken(")!;
        Assert.That(file.ParseErrors, Has.Count.EqualTo(1));
        Assert.That(file.ParseErrors[0], Does.Contain("broken.cop").And.Contain("error:"));
    }

    [Test]
    public void Cop_CachePreservesDiagnosticsAndCanonicalStatementHierarchy()
    {
        var root = TemporaryDirectory();
        try
        {
            var source = new CopSourceParser().Parse("sample.cop",
                "function MAIN() { foreach ['x'] => print(wrap(item)) }")!;
            var broken = new CopSourceParser().Parse("broken.cop", "function broken(")!;
            var path = Path.Combine(root, "cache.bin");
            SourceCacheSerializer.Save(path, [1, 2, 3], [source, broken]);
            var loaded = SourceCacheSerializer.TryLoad(path, [1, 2, 3])!;
            Assert.That(loaded[1].ParseErrors, Is.EqualTo(broken.ParseErrors));
            var loop = loaded[0].Statements.Single(s => s.Kind == "foreach");
            var print = loaded[0].Statements.Single(s => s.MemberName == "print");
            var wrap = loaded[0].Statements.Single(s => s.MemberName == "wrap");
            Assert.That(loop.Children.Single(), Is.SameAs(print));
            Assert.That(print.Parent, Is.SameAs(loop));
            Assert.That(print.Children.Single(), Is.SameAs(wrap));
            Assert.That(wrap.Method, Is.SameAs(loaded[0].Methods.Single()));
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void Cop_CommentsHierarchyAndOwnership_ExcludeQuotedHashes()
    {
        var file = new CopSourceParser().Parse("sample.cop", """
            ## command
            function MAIN() {
                let values = read()
                foreach values =>
                    print(wrap(item))
            }
            let ordinary = '# not a comment'
            let raw = '''
            # not a comment either
            '''
            # real comment
            """)!;
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.CommentLines.Order(), Is.EqualTo(new[] { 1, 11 }));
        var calls = file.Statements.Where(s => s.Kind == "call").ToArray();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new[] { "read", "print", "wrap" }));
        Assert.That(calls[0].Parent?.Kind, Is.EqualTo("declaration"));
        Assert.That(calls[1].Parent?.Kind, Is.EqualTo("foreach"));
        Assert.That(calls[2].Parent, Is.SameAs(calls[1]));
        Assert.That(calls.Select(s => s.Method?.Name), Is.EqualTo(new[] { "MAIN", "MAIN", "MAIN" }));
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "cop-provider-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
