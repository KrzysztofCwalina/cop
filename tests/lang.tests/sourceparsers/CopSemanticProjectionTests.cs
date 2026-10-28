using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class CopSemanticProjectionTests
{
    private string _root = null!;
    private string Target => Path.Combine(_root, "target");

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-static-cop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Target);
        ProviderErrors.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, true);
        ProviderErrors.Clear();
    }

    private static IEnumerable<StatementInfo> Calls(CopProvider provider, string target)
    {
        var deferred = (DeferredProviderResult)provider.QueryDeferred(new ProviderQuery { RootPath = target, Collection = "Calls" });
        return deferred.Collections["Calls"].Cast<StatementInfo>();
    }

    [Test]
    public void ExactOverloadsAndGenericsHaveInstantiatedFactsOnColdAndWarmRuns()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function choose(value : string) : string => value
            function choose(value : int) : int => value
            function identity(value : T) : T => value
            let first = choose('hello')
            let second = choose(7)
            let third = identity('hello')
            """);
        for (int run = 0; run < 2; run++)
        {
            var calls = Calls(new CopProvider(), Target).ToArray();
            Assert.That(calls.Select(call => call.ValueType?.OriginalText), Is.EqualTo(new[] { "string", "int", "string" }));
            Assert.That(calls.Select(call => call.ReferencedCallable?.Line), Is.EqualTo(new int?[] { 1, 2, 3 }));
            Assert.That(calls.Select(call => call.ReferencedCallable?.Parameters.Single().OriginalText),
                Is.EqualTo(new[] { "string", "int", "string" }));
            Assert.That(calls.Select(call => call.ReceiverType), Is.EqualTo(new TypeReference?[] { null, null, null }));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public void UnknownRequiredSlotSuppressesWholeCallableButKeepsKnownResult()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function opaque(value) : string => 'known'
            let result = opaque(missing)
            """);
        var call = Calls(new CopProvider(), Target).Single();
        Assert.That(call.ReferencedCallable, Is.Null);
        Assert.That(call.ValueType?.OriginalText, Is.EqualTo("string"));
        Assert.That(call.ReceiverType, Is.Null);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public void UnknownOverloadAndDynamicParameterStayUnknownInsteadOfChoosingFirst()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function choose(value : string) : string => value
            function choose(value : int) : int => value
            function dynamic(choose) => choose('hello')
            let result = choose(missing)
            """);
        var calls = Calls(new CopProvider(), Target).ToArray();
        Assert.That(calls, Has.Length.EqualTo(2));
        Assert.That(calls.Select(call => call.ReferencedCallable), Is.EqualTo(new CallableReference?[] { null, null }));
        Assert.That(calls.Select(call => call.ValueType), Is.EqualTo(new TypeReference?[] { null, null }));
    }

    [Test]
    public void LocalScopesAndScalarApplicationDistinguishReceiverFromResult()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function decorate(value : string, suffix : int) : string => value
            function MAIN() = {
                let suffix = 3
                let result = 'hello':decorate(suffix)
            }
            """);
        var call = Calls(new CopProvider(), Target).Single();
        Assert.That(call.ReceiverType?.OriginalText, Is.EqualTo("string"));
        Assert.That(call.ValueType?.OriginalText, Is.EqualTo("string"));
        Assert.That(call.ReferencedCallable?.Parameters.Select(type => type.OriginalText),
            Is.EqualTo(new[] { "string", "int" }));
    }

    [Test]
    public void ImportedSourceSignaturesRefreshAfterDependencyOnlyEditAndNeverEvaluateBodies()
    {
        var source = Path.Combine(_root, "packages", "library", "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(_root, "packages", "library", "cop.json"), """{"name":"library"}""");
        var dependency = Path.Combine(source, "library.cop");
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            import library
            let result = library.transform('hello')
            """);
        foreach (var expected in new[] { "string", "int" })
        {
            File.WriteAllText(dependency, $"export function transform(value : string) : {expected} => missing('never execute')");
            var call = Calls(new CopProvider(), Target).Single();
            Assert.That(call.ValueType?.OriginalText, Is.EqualTo(expected));
            Assert.That(call.ReferencedCallable?.ReturnType?.OriginalText, Is.EqualTo(expected));
            Assert.That(call.ReferencedCallable?.Namespace, Is.EqualTo("library"));
            Assert.That(call.ReferencedCallable?.SourceFile, Is.EqualTo("../packages/library/src/library.cop"));
            Assert.That(call.ReceiverType, Is.Null, "Package namespaces are not value receivers.");
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public void StructuralFirstDefersSemanticContextAndUnreadLaterFiles()
    {
        File.WriteAllText(Path.Combine(Target, "a.cop"), """
            import unavailable
            function first(value : string) : string => value
            let result = first('hello')
            """);
        File.WriteAllText(Path.Combine(Target, "z.cop"), "type Broken = {");
        var call = Calls(new CopProvider(), Target).First();
        Assert.That(call.MemberName, Is.EqualTo("first"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That(call.ValueType?.OriginalText, Is.EqualTo("string"));
        var errors = ProviderErrors.Drain();
        Assert.That(errors, Has.Count.EqualTo(2));
        Assert.That(errors[0], Does.Contain("z.cop"));
        Assert.That(errors[1], Does.Contain("Import 'unavailable'"));
        Assert.That(call.ValueType?.OriginalText, Is.EqualTo("string"));
        Assert.That(ProviderErrors.Drain(), Is.Empty, "One execution context should report errors only once.");
    }

    [Test]
    public void MethodStatementNavigationAlsoAttachesDeferredFacts()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function identity(value : string) : string => value
            function caller(value : string) => identity(value)
            """);
        var result = (DeferredProviderResult)new CopProvider().QueryDeferred(new ProviderQuery
        {
            RootPath = Target, Collection = "Methods"
        });
        var method = result.Collections["Methods"].Cast<MethodDeclaration>().Single(m => m.Name == "caller");
        Assert.That(method.Statements.Single().ReferencedCallable?.Name, Is.EqualTo("identity"));
        Assert.That(method.Statements.Single().ValueType?.OriginalText, Is.EqualTo("string"));
    }

    [Test]
    public void SemanticPushdownRunsAfterDeferredBindingIsAttached()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function identity(value : string) : string => value
            function numeric(value : int) : int => value
            let result = identity('hello')
            let other = numeric(3)
            """);
        var result = (DeferredProviderResult)new CopProvider().QueryDeferred(new ProviderQuery
        {
            RootPath = Target, Collection = "Calls",
            Filter = new StringOpFilter("ValueType", StringOp.Equals, "string")
        });
        Assert.That(result.Collections["Calls"].Cast<StatementInfo>().Select(call => call.MemberName),
            Is.EqualTo(new[] { "identity" }));
    }

    [Test]
    public void ImportedGenericUsesArgumentsButPrivateBodyInferenceStaysUnknown()
    {
        var source = Path.Combine(_root, "packages", "library", "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(_root, "packages", "library", "cop.json"), """{"name":"library"}""");
        File.WriteAllText(Path.Combine(source, "library.cop"), """
            let secret = 'private'
            export function identity(value : T) : T => value
            export function privateResult(value : string) => secret
            """);
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            import library
            let first = library.identity(3)
            let second = library.privateResult('hello')
            """);
        var calls = Calls(new CopProvider(), Target).ToArray();
        Assert.That(calls.Select(call => call.ValueType?.OriginalText), Is.EqualTo(new string?[] { "int", null }));
        Assert.That(calls.Select(call => call.ReferencedCallable?.Parameters.Single().OriginalText),
            Is.EqualTo(new[] { "int", "string" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }
}
