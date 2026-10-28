using System.Reflection;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class SourceGraphIdentityTests
{
    [Test]
    public void CacheRoundTrip_PreservesSameLineCallsAndSharedOwnership()
    {
        var first = new StatementInfo("call", [], null, "Run", [], 1, true);
        var second = new StatementInfo("call", [], null, "Run", [], 1, true);
        var parent = new StatementInfo("if", [], null, null, [], 1, true)
        {
            _children = [first, second]
        };
        var method = new MethodDeclaration("Execute", Modifier.Public, [], null, [], 1)
        {
            Statements = [parent, first, second]
        };
        first.Parent = second.Parent = parent;
        first.Method = second.Method = parent.Method = method;
        var nested = new TypeDeclaration("Nested", TypeKind.Class, Modifier.Public, [], [],
            [], [method], [], [], 1);
        var type = new TypeDeclaration("Outer", TypeKind.Class, Modifier.Public, [], [],
            [], [], [nested], [], 1);
        var original = new SourceFile("same.test", "test", [type], [parent, first, second], "if (x) { Run(); Run(); }")
        {
            Methods = [method]
        };
        foreach (var file in new[] { original, RoundTrip(original), RoundTrip(RoundTrip(original)) })
        {
            Link(file);
            var actualMethod = file.Types[0].NestedTypes[0].Methods.Single();
            var statements = file.Statements;
            Assert.Multiple(() =>
            {
                Assert.That(statements, Has.Count.EqualTo(3));
                Assert.That(statements[1], Is.Not.SameAs(statements[2]));
                Assert.That(statements[0].Children, Has.Count.EqualTo(2));
                Assert.That(statements[0].Children[0], Is.SameAs(statements[1]));
                Assert.That(statements[0].Children[1], Is.SameAs(statements[2]));
                Assert.That(statements[1].Parent, Is.SameAs(statements[0]));
                Assert.That(statements[2].Parent, Is.SameAs(statements[0]));
                Assert.That(statements[2].GetAncestors().Single(), Is.SameAs(statements[0]));
                Assert.That(file.Methods.Single(), Is.SameAs(actualMethod));
                Assert.That(actualMethod.Statements[1], Is.SameAs(statements[1]));
                Assert.That(actualMethod.Statements[2], Is.SameAs(statements[2]));
                Assert.That(statements.Select(s => s.Method), Is.All.SameAs(actualMethod));
                Assert.That(statements.Select(s => s.File), Is.All.SameAs(file));
                Assert.That(actualMethod.File, Is.SameAs(file));
                Assert.That(file.Types[0].File, Is.SameAs(file));
                Assert.That(file.Types[0].NestedTypes[0].File, Is.SameAs(file));
                Assert.That(CodeBindings.BuildExtractors()["Methods"](file), Has.Count.EqualTo(1));
            });
        }
    }

    [Test]
    public void LinkReferences_DoesNotCloneTypes()
    {
        var method = new MethodDeclaration("Run", Modifier.Public, [], null, [], 1);
        var type = new TypeDeclaration("Example", TypeKind.Class, Modifier.Public, [], [], [], [method], [], [], 1);
        var file = new SourceFile("example.test", "test", [type], [], "");
        Link(file);
        Assert.That(file.Types.Single(), Is.SameAs(type));
        Assert.That(type.File, Is.SameAs(file));
        Assert.That(method.File, Is.SameAs(file));
    }

    internal static SourceFile RoundTrip(SourceFile file)
    {
        var serializer = typeof(CodeCollectionBuilder).Assembly.GetType("Cop.Providers.SourceCacheSerializer", true)!;
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, $"model-{Guid.NewGuid():N}.bin");
        try
        {
            serializer.GetMethod("Save", [typeof(string), typeof(byte[]), typeof(List<SourceFile>)])!
                .Invoke(null, [path, new byte[] { 1, 2 }, new List<SourceFile> { file }]);
            var loaded = (List<SourceFile>?)serializer.GetMethod("TryLoad", [typeof(string), typeof(byte[])])!
                .Invoke(null, [path, new byte[] { 1, 2 }]);
            Assert.That(loaded, Has.Count.EqualTo(1));
            return loaded![0];
        }
        finally { File.Delete(path); }
    }

    internal static void Link(SourceFile file) => typeof(CodeCollectionBuilder)
        .GetMethod("LinkReferences", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [file]);
}
