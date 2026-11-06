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

    private static async Task<IAsyncEnumerable<StatementInfo>> CallsAsync(CopProvider provider, string target, CancellationToken cancellationToken = default)
    {
        var deferred = (DeferredProviderResult?)(await provider.QueryDeferredAsync(new ProviderQuery { RootPath = target, Collection = "Calls" }, cancellationToken: cancellationToken))
            ?? throw new AssertionException("Expected a deferred provider result.");
        return deferred.Collections["Calls"].Cast<StatementInfo>();
    }

    [Test]
    public async Task NegativeParserFixturesDoNotBreakSiblingSemanticAnalysisOnColdAndWarmRuns()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function identity(value : string) : string => value
            let result = identity('hello')
            """);
        File.WriteAllText(Path.Combine(Target, "negative.cop"),
            "# @expect parse-error\npredicate broken(Type)");

        for (int run = 0; run < 2; run++)
        {
            var call = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).SingleAsync());
            Assert.That(call.MemberName, Is.EqualTo("identity"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Name, Is.EqualTo("identity"));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public async Task ExactOverloadsAndGenericsHaveInstantiatedFactsOnColdAndWarmRuns()
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
            var calls = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).ToArrayAsync());
            Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText).ToArrayAsync()), Is.EqualTo(new[] { "string", "int", "string" }));
            Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Line).ToArrayAsync()), Is.EqualTo(new int?[] { 1, 2, 3 }));
            Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Parameters.Single().OriginalText).ToArrayAsync()),
                Is.EqualTo(new[] { "string", "int", "string" }));
            Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReceiverType).ToArrayAsync()), Is.EqualTo(new TypeReference?[] { null, null, null }));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public async Task UnknownRequiredSlotSuppressesWholeCallableButKeepsKnownResult()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function opaque(value) : string => 'known'
            let result = opaque(missing)
            """);
        var call = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).SingleAsync());
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable, Is.Null);
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReceiverType, Is.Null);
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task UnknownOverloadAndDynamicParameterStayUnknownInsteadOfChoosingFirst()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function choose(value : string) : string => value
            function choose(value : int) : int => value
            function dynamic(choose) => choose('hello')
            let result = choose(missing)
            """);
        var calls = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).ToArrayAsync());
        Assert.That(calls, Has.Length.EqualTo(2));
        Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable).ToArrayAsync()), Is.EqualTo(new CallableReference?[] { null, null }));
        Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType).ToArrayAsync()), Is.EqualTo(new TypeReference?[] { null, null }));
    }

    [Test]
    public async Task LocalScopesAndScalarApplicationDistinguishReceiverFromResult()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function decorate(value : string, suffix : int) : string => value
            function MAIN() = {
                let suffix = 3
                let result = 'hello':decorate(suffix)
            }
            """);
        var call = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).SingleAsync());
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReceiverType?.OriginalText, Is.EqualTo("string"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Parameters.Select(type => type.OriginalText),
            Is.EqualTo(new[] { "string", "int" }));
    }

    [Test]
    public async Task ImportedSourceSignaturesRefreshAfterDependencyOnlyEditAndNeverEvaluateBodies()
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
            var call = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).SingleAsync());
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo(expected));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.ReturnType?.OriginalText, Is.EqualTo(expected));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Namespace, Is.EqualTo("library"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.SourceFile, Is.EqualTo("../packages/library/src/library.cop"));
            Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReceiverType, Is.Null, "Package namespaces are not value receivers.");
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public async Task InputStatisticsRespectDeferredDemandAndCountCachedFilesOncePerExecution()
    {
        File.WriteAllText(Path.Combine(Target, "a.cop"), "type First = { Name : string }");
        File.WriteAllText(Path.Combine(Target, "z.cop"), "type Second = { Name : string }");
        var provider = new CopProvider();
        for (int run = 0; run < 2; run++)
        {
            using var statistics = new ExecutionStatistics();
            var query = new ProviderQuery { RootPath = Target, Collection = "Types" };
            var result = (DeferredProviderResult?)(await provider.QueryDeferredAsync(query, cancellationToken: CancellationToken.None))
                ?? throw new AssertionException("Expected a deferred provider result.");
            Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(0));
            Assert.That((await result.Collections["Types"].Take(1).CountAsync()), Is.EqualTo(1));
            Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(1));
            Assert.That((await result.Collections["Types"].CountAsync()), Is.EqualTo(2));
            Assert.That((await result.Collections["Types"].CountAsync()), Is.EqualTo(2));
            Assert.That(statistics.Snapshot().InputFiles, Is.EqualTo(2));
            Assert.That(ProviderErrors.Drain(), Is.Empty);
        }
    }

    [Test]
    public async Task StructuralFirstDefersSemanticContextAndUnreadLaterFiles()
    {
        File.WriteAllText(Path.Combine(Target, "a.cop"), """
            import unavailable
            function first(value : string) : string => value
            let result = first('hello')
            """);
        File.WriteAllText(Path.Combine(Target, "z.cop"), "type Broken = {");
        var call = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).FirstAsync());
        Assert.That(call.MemberName, Is.EqualTo("first"));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
        var errors = ProviderErrors.Drain();
        Assert.That(errors, Has.Count.EqualTo(2));
        Assert.That(errors[0], Does.Contain("z.cop"));
        Assert.That(errors[1], Does.Contain("Import 'unavailable'"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
        Assert.That(ProviderErrors.Drain(), Is.Empty, "One execution context should report errors only once.");
    }

    [Test]
    public async Task MethodStatementNavigationAlsoAttachesDeferredFacts()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function identity(value : string) : string => value
            function caller(value : string) => identity(value)
            """);
        var result = (DeferredProviderResult?)(await new CopProvider().QueryDeferredAsync(new ProviderQuery
        {
            RootPath = Target, Collection = "Methods"
        }, cancellationToken: CancellationToken.None))
            ?? throw new AssertionException("Expected a deferred provider result.");
        var method = (await result.Collections["Methods"].Cast<MethodDeclaration>().SingleAsync(m => m.Name == "caller"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(method.Statements.Single(), CancellationToken.None)).ReferencedCallable?.Name, Is.EqualTo("identity"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(method.Statements.Single(), CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
    }

    [Test]
    public async Task SemanticPushdownRunsAfterDeferredBindingIsAttached()
    {
        File.WriteAllText(Path.Combine(Target, "main.cop"), """
            function identity(value : string) : string => value
            function numeric(value : int) : int => value
            let result = identity('hello')
            let other = numeric(3)
            """);
        var result = (DeferredProviderResult?)(await new CopProvider().QueryDeferredAsync(new ProviderQuery
        {
            RootPath = Target, Collection = "Calls",
            Filter = new StringOpFilter("ValueType", StringOp.Equals, "string")
        }, cancellationToken: CancellationToken.None))
            ?? throw new AssertionException("Expected a deferred provider result.");
        Assert.That((await result.Collections["Calls"].Cast<StatementInfo>().Select(call => call.MemberName).ToArrayAsync()),
            Is.EqualTo(new[] { "identity" }));
    }

    [Test]
    public async Task ImportedGenericUsesArgumentsButPrivateBodyInferenceStaysUnknown()
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
        var calls = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).ToArrayAsync());
        Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText).ToArrayAsync()), Is.EqualTo(new string?[] { "int", null }));
        Assert.That((await calls.ToAsyncEnumerable().Select(async (Cop.Providers.SourceModel.StatementInfo call, CancellationToken cancellationToken) => (await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Parameters.Single().OriginalText).ToArrayAsync()),
            Is.EqualTo(new[] { "int", "string" }));
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }

    [Test]
    public async Task SourceTopLevelLetsCommandsAndImportedInitializersNeverExecute()
    {
        var sentinel = Path.Combine(_root, "executed.txt");
        var literal = sentinel.Replace("\\", "\\\\", StringComparison.Ordinal);
        var source = Path.Combine(_root, "packages", "library", "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(_root, "packages", "library", "cop.json"), """{"name":"library"}""");
        File.WriteAllText(Path.Combine(source, "library.cop"), $"""
            export let sideEffect = save('{literal}', 'import executed')
            export function known(value : string) : string => value
            """);
        File.WriteAllText(Path.Combine(Target, "main.cop"), $"""
            import library
            let sideEffect = save('{literal}', 'source executed')
            let provider = object('unregistered-static-sentinel')
            command MAIN = save('{literal}', 'command executed')
            let result = library.known('hello')
            """);
        Assert.That(File.Exists(sentinel), Is.False);
        var call = (await (await CallsAsync(new CopProvider(), Target, cancellationToken: CancellationToken.None)).SingleAsync(call => call.MemberName == "known"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ValueType?.OriginalText, Is.EqualTo("string"));
        Assert.That((await AsyncSourceFacts.SemanticAsync(call, CancellationToken.None)).ReferencedCallable?.Name, Is.EqualTo("known"));
        Assert.That(File.Exists(sentinel), Is.False, "Semantic demand must not force target lets, commands, or package initializers.");
        Assert.That(ProviderErrors.Drain(), Is.Empty);
    }
}
