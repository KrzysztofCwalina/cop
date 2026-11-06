// Exercises caller-token identity, internal deadlines, and consumed cancellation tasks.

using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public sealed class CSharpCancellationFactsTests
{
    [TestCase("OperationCanceledException", true)]
    [TestCase("global::System.OperationCanceledException", true)]
    [TestCase("Canceled", true)]
    [TestCase("TaskCanceledException", true)]
    [TestCase("DerivedCancellation", true)]
    [TestCase("Exception", false)]
    [TestCase("Other.OperationCanceledException", false)]
    public async Task CancellationHandlerUsesBoundExceptionIdentity(string type, bool expected)
    {
        CSharpStatementInfo handler = Parse("await Work(ct);", "", type: type);
        Assert.That((await AsyncSourceFacts.SemanticAsync(handler, CancellationToken.None))
            .IsCancellationHandler, Is.EqualTo(expected));
        Assert.That(CSharpBindings.Build().Accessors["CSharpStatement"]
            ["IsCancellationHandler"](handler), Is.EqualTo(expected));
    }

    [TestCase("!ct.IsCancellationRequested", true)]
    [TestCase("ct.IsCancellationRequested == false", true)]
    [TestCase("false == ct.IsCancellationRequested", true)]
    [TestCase("!ct.IsCancellationRequested && sharedTask.IsCanceled", true)]
    [TestCase("!ct.IsCancellationRequested && !_lifetime.IsCancellationRequested", true)]
    [TestCase("timeout.IsCancellationRequested && !ct.IsCancellationRequested", true)]
    [TestCase("!other.IsCancellationRequested", false)]
    [TestCase("!ct.IsCancellationRequested || flag", false)]
    [TestCase("ct.IsCancellationRequested", false)]
    [TestCase("flag", false)]
    public async Task CancellationFiltersMustExcludeTheOperationLinkedCaller(
        string filter, bool expected)
    {
        await AssertPreserves(Parse("await Work(ct);", "return 7;",
            filter: filter, prelude: "using var timeout = new CancellationTokenSource(100);"),
            expected);
    }

    [TestCase("ct.ThrowIfCancellationRequested(); return 7;", true)]
    [TestCase("if (flag) await Cleanup(); ct.ThrowIfCancellationRequested(); return 7;", true)]
    [TestCase("if (ct.IsCancellationRequested) throw; return 7;", true)]
    [TestCase("if (!ct.IsCancellationRequested) return 7; throw;", true)]
    [TestCase("if (flag) throw; else ct.ThrowIfCancellationRequested();", true)]
    [TestCase("throw ex;", true)]
    [TestCase("throw new OperationCanceledException(ct);", true)]
    [TestCase("", false)]
    [TestCase("return 7;", false)]
    [TestCase("if (flag) ct.ThrowIfCancellationRequested(); return 7;", false)]
    [TestCase("other.ThrowIfCancellationRequested(); return 7;", false)]
    [TestCase("if (flag) throw; return 7;", false)]
    [TestCase("throw new TimeoutException();", false)]
    [TestCase("throw new OperationCanceledException(other);", false)]
    [TestCase("throw new OperationCanceledException();", false)]
    [TestCase("throw new DerivedCancellation(ct);", false)]
    [TestCase("ex = new OperationCanceledException(other); throw ex;", false)]
    [TestCase("Action callback = () => ct.ThrowIfCancellationRequested(); return 7;", false)]
    [TestCase("try { ct.ThrowIfCancellationRequested(); } catch { } return 7;", false)]
    [TestCase("Consume(flag ? 1 : throw new Exception()); ct.ThrowIfCancellationRequested();",
        false)]
    public async Task CancellationGuardsMustCoverEveryExplicitExit(string body, bool expected)
    {
        await AssertPreserves(Parse("await Work(ct);", body), expected);
    }

    [TestCase("await Work(linked.Token);", "ct.ThrowIfCancellationRequested();", true)]
    [TestCase("await Work(linked.Token);", "other.ThrowIfCancellationRequested();", false)]
    [TestCase("await Work(alias);", "ct.ThrowIfCancellationRequested();", true)]
    [TestCase("await Work(ct); await Work(other);", "ct.ThrowIfCancellationRequested();", false)]
    [TestCase("await Work(ct); await Work(other);",
        "ct.ThrowIfCancellationRequested(); other.ThrowIfCancellationRequested();", true)]
    public async Task CancellationProvenanceFollowsStableAliasesAndLinkedSources(
        string operation, string body, bool expected)
    {
        const string prelude = """
            using var timeout = new CancellationTokenSource(100);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            CancellationToken alias = ct;
            """;
        await AssertPreserves(Parse(operation, body, prelude: prelude), expected);
    }

    [TestCase("await Work(timeout.Token);", "", true)]
    [TestCase("await Work(timeout.Token).ConfigureAwait(false);", "", true)]
    [TestCase("await Work(ct);", "", false)]
    [TestCase("await Cleanup();", "", false)]
    [TestCase("await Work(timeout.Token); await Cleanup();", "", false)]
    [TestCase("await Work(timeout.Token); throw new OperationCanceledException(ct);", "", false)]
    public async Task InternalDeadlineHandlingRequiresKnownAwaitedTokenProvenance(
        string operation, string body, bool expected)
    {
        await AssertPreserves(Parse(operation, body,
            prelude: "using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));"),
            expected);
    }

    [TestCase("ProcessObjectProvider", "!ct.IsCancellationRequested "
        + "&& !_lifetime.IsCancellationRequested", "throw new TimeoutException();", true)]
    [TestCase("ToolchainJsonWorker", "",
        "if (flag) await Cleanup(); ct.ThrowIfCancellationRequested(); return 7;", true)]
    [TestCase("ToolProviderBase", "timeout.IsCancellationRequested "
        + "&& !ct.IsCancellationRequested",
        "await Cleanup(); await sharedTask; throw new TimeoutException();", true)]
    [TestCase("AiProvider", "!ct.IsCancellationRequested", "return 7;", false)]
    [TestCase("HttpProvider", "!ct.IsCancellationRequested", "return 7;", false)]
    [TestCase("AsyncProviderValue", "!ct.IsCancellationRequested && sharedTask.IsCanceled",
        "lock (this) { if (flag) Consume(sharedTask); }", false)]
    public async Task ProductionCancellationContractsKeepCallerAndDeadlinePathsDistinct(
        string contract, string filter, string body, bool usesLinkedToken)
    {
        const string prelude = """
            using var timeout = new CancellationTokenSource(100);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            """;
        string operation = contract == "AsyncProviderValue"
            ? "await sharedTask.WaitAsync(ct).ConfigureAwait(false);"
            : usesLinkedToken ? "await Work(linked.Token).ConfigureAwait(false);"
            : "await Work(ct).ConfigureAwait(false);";
        await AssertPreserves(Parse(operation, body, filter: filter, prelude: prelude), true);
    }

    [TestCase("throw new OperationCanceledException(\"interrupted\", nested, linked.Token);",
        true)]
    [TestCase("throw new OperationCanceledException(ct);", true)]
    [TestCase("throw ex;", true)]
    [TestCase("", true)]
    [TestCase("if (flag) throw new OperationCanceledException(ct);", true)]
    [TestCase("throw;", false)]
    [TestCase("throw nested;", false)]
    [TestCase("return 7;", false)]
    [TestCase("throw new TimeoutException();", false)]
    [TestCase("throw new OperationCanceledException(other);", false)]
    [TestCase("if (flag) return 7; throw new OperationCanceledException(ct);", false)]
    [TestCase("try { throw new OperationCanceledException(ct); } "
        + "catch (OperationCanceledException) { } return 7;", false)]
    public async Task NestedCleanupHandlersMustPreserveCancellationOnEveryExplicitPath(
        string recovery, bool expected)
    {
        string body = $$"""
            await Cleanup();
            try { await sharedTask; }
            catch (Exception nested) when (nested is System.IO.IOException
                or InvalidOperationException or OperationCanceledException)
            {
                {{recovery}}
            }
            throw;
            """;
        await AssertPreserves(Parse("await Work(linked.Token);", body,
            prelude: "using var linked = CancellationTokenSource.CreateLinkedTokenSource("
                + "ct, _lifetime.Token);",
            filter: "linked.IsCancellationRequested"), expected);
    }

    [TestCase("ct", true)]
    [TestCase("other", false)]
    public async Task LinkedCleanupReplacementRequiresTheActualOperationCaller(
        string linkedInput, bool expected)
    {
        const string body = """
            try { await sharedTask; }
            catch (Exception nested)
            {
                throw new OperationCanceledException("interrupted", nested, linked.Token);
            }
            throw;
            """;
        await AssertPreserves(Parse("await Work(ct);", body,
            prelude: "using var linked = CancellationTokenSource.CreateLinkedTokenSource("
                + linkedInput + ", _lifetime.Token);"), expected);
    }

    [TestCase("OperationCanceledException", true)]
    [TestCase("System.IO.IOException", false)]
    public async Task CapturedNestedRethrowUsesTheActiveBoundExceptionType(
        string exceptionType, bool expected)
    {
        string body = $$"""
            try { await Cleanup(); }
            catch ({{exceptionType}} nested) { throw nested; }
            throw;
            """;
        await AssertPreserves(Parse("await Work(ct);", body), expected);
    }

    [Test]
    public async Task LinkedDeadlineCleanupCancellationHasTheExactProcessProviderBoundary()
    {
        const string source = """
            using System;
            using System.IO;
            using System.Threading;
            using System.Threading.Tasks;
            class Example
            {
                readonly CancellationTokenSource _lifetime = new();
                static Task StopProcessAsync() => Task.CompletedTask;
                async ValueTask<byte[]> SendRequestAsync(CancellationToken cancellationToken)
                {
                    using CancellationTokenSource deadline =
                        CancellationTokenSource.CreateLinkedTokenSource(
                            cancellationToken, _lifetime.Token);
                    deadline.CancelAfter(TimeSpan.FromSeconds(1));
                    try
                    {
                        Task<byte[]> exchange = Task.FromResult(Array.Empty<byte>());
                        try { return await exchange.WaitAsync(deadline.Token); }
                        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
                        {
                            await StopProcessAsync();
                            try { await exchange; }
                            catch (Exception ex) when (ex is IOException
                                or InvalidOperationException or OperationCanceledException)
                            {
                                throw new OperationCanceledException(
                                    "Provider exchange was interrupted.", ex, deadline.Token);
                            }
                            throw;
                        }
                    }
                    catch (OperationCanceledException) when (
                        cancellationToken.IsCancellationRequested)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw;
                    }
                    catch (OperationCanceledException ex) when (
                        !cancellationToken.IsCancellationRequested
                            && !_lifetime.IsCancellationRequested)
                    {
                        throw new TimeoutException("Provider deadline expired.", ex);
                    }
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = Compilation(tree);
        SourceFile file = new CSharpSourceParser().ParseWithSemantics(
            "Boundary.cs", source, tree, compilation.GetSemanticModel(tree))!;
        CSharpStatementInfo[] handlers = file.Statements.OfType<CSharpStatementInfo>()
            .Where(statement => statement.IsCancellationHandler).ToArray();
        Assert.That(handlers.Length, Is.EqualTo(3));
        Assert.That(handlers[0].IsOriginalExceptionAlwaysRethrown, Is.False,
            "Nested transport replacement must not change the original-rethrow fact.");
        foreach (CSharpStatementInfo handler in handlers)
        {
            await AssertPreserves(handler, true);
        }
    }

    [TestCase("ct = other;", "await Work(ct);", "ct.ThrowIfCancellationRequested();")]
    [TestCase("CancellationToken alias = ct; alias = other;", "await Work(alias);",
        "ct.ThrowIfCancellationRequested();")]
    [TestCase("CancellationToken alias = ct; (alias) = other;", "await Work(alias);",
        "ct.ThrowIfCancellationRequested();")]
    [TestCase("CancellationToken alias = ct; int value; (alias, value) = (other, 1);",
        "await Work(alias);", "ct.ThrowIfCancellationRequested();")]
    [TestCase("CancellationToken alias = ct; Action change = () => alias = other; change();",
        "await Work(alias);", "ct.ThrowIfCancellationRequested();")]
    [TestCase("CancellationToken alias = (CancellationToken)(TokenWrapper)ct;",
        "await Work(alias);", "ct.ThrowIfCancellationRequested();")]
    public async Task ReassignedTokensDoNotProveCallerIdentity(
        string prelude, string operation, string body)
    {
        await AssertPreserves(Parse(operation, body, prelude: prelude), false);
    }

    [TestCase("completion.SetCanceled(ct); return await completion.Task;", "", true)]
    [TestCase("completion.TrySetCanceled(ct); return await completion.Task;", "", true)]
    [TestCase("completion.TrySetCanceled(ex.CancellationToken); return await completion.Task;",
        "", true)]
    [TestCase("completion.SetCanceled(ct); await completion.Task.ConfigureAwait(false);",
        "", true)]
    [TestCase("completion.TrySetCanceled(ct);", "return await completion.Task;", true)]
    [TestCase("completion.TrySetCanceled(ct); return 7;", "", false)]
    [TestCase("completion.TrySetCanceled(other); return await completion.Task;", "", false)]
    [TestCase("completion.TrySetCanceled(); return await completion.Task;", "", false)]
    [TestCase("if (flag) completion.TrySetCanceled(ct); return await completion.Task;", "", false)]
    [TestCase("completion.TrySetCanceled(ct); return await sharedTask;", "", false)]
    [TestCase("completion.TrySetResult(7); completion.TrySetCanceled(ct); "
        + "return await completion.Task;", "", false)]
    [TestCase("Action finish = () => completion.TrySetResult(7); finish(); "
        + "completion.TrySetCanceled(ct); return await completion.Task;", "", false)]
    [TestCase("Consume(completion); completion.TrySetCanceled(ct); "
        + "return await completion.Task;", "", false)]
    public async Task CancellationCompletionMustBeFreshCorrectlyCanceledAndConsumed(
        string body, string tail, bool expected)
    {
        await AssertPreserves(Parse("await Work(ct);", body,
            prelude: "var completion = new TaskCompletionSource<int>();",
            tail: tail.Length == 0 ? "return 7;" : tail), expected);
    }

    [Test]
    public async Task CapturedCancellationTokenMustBeTheFrameworkExceptionProperty()
    {
        await AssertPreserves(Parse("await Work(ct);",
            "completion.TrySetCanceled(ex.CancellationToken); return await completion.Task;",
            prelude: "var completion = new TaskCompletionSource<int>();",
            type: "HiddenTokenCancellation"), false);
    }

    [Test]
    public async Task DeferredCancellationFactsUseTheActualCompilationAndGlobalAliases()
    {
        const string source = """
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            class Example
            {
                async Task Run(CancellationToken ct)
                {
                    try { await Task.Delay(1, ct); }
                    catch (Hidden) when (!ct.IsCancellationRequested) { }
                }
            }
            """;
        SourceFile original = new CSharpSourceParser().Parse("Cancellation.cs", source)!;
        CSharpStatementInfo handler = original.Statements.OfType<CSharpStatementInfo>()
            .Single(statement => statement.Kind == "catch");
        Assert.That(handler.IsCancellationHandler, Is.False, "unknown standalone alias");
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        SyntaxTree aliases = CSharpSyntaxTree.ParseText(
            "global using Hidden = System.OperationCanceledException;");
        CSharpCompilation compilation = Compilation(tree, aliases);
        CSharpSourceParser.DeferTypeNames(tree, original.Statements,
            () => compilation.GetSemanticModel(tree), () => compilation.GetSemanticModel(tree));
        await AssertPreserves(handler, true);
    }

    private static CSharpStatementInfo Parse(string operation, string body,
        string filter = "", string prelude = "", string tail = "return 7;",
        string type = "OperationCanceledException")
    {
        string source = $$"""
            using System;
            using System.Threading;
            using System.Threading.Tasks;
            using Canceled = System.OperationCanceledException;
            namespace Other { class OperationCanceledException : Exception { } }
            class DerivedCancellation : OperationCanceledException
            {
                public DerivedCancellation() { }
                public DerivedCancellation(CancellationToken token) { }
            }
            class HiddenTokenCancellation : OperationCanceledException
            {
                public new CancellationToken CancellationToken => default;
            }
            readonly struct TokenWrapper
            {
                public static implicit operator TokenWrapper(CancellationToken token) => default;
                public static implicit operator CancellationToken(TokenWrapper token) => default;
            }
            class Example
            {
                readonly CancellationTokenSource _lifetime = new();
                readonly Task<int> sharedTask = Task.FromResult(7);
                static Task<int> Work(CancellationToken token) => Task.FromResult(7);
                static Task Cleanup() => Task.CompletedTask;
                static void Consume(object value) { }
                async Task<int> Run(CancellationToken ct, CancellationToken other, bool flag)
                {
                    {{prelude}}
                    try { {{operation}} }
                    catch ({{type}} ex) {{(filter.Length == 0 ? "" : $"when ({filter})")}}
                    {
                        {{body}}
                    }
                    {{tail}}
                }
            }
            """;
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source);
        CSharpCompilation compilation = Compilation(tree);
        SourceFile file = new CSharpSourceParser().ParseWithSemantics(
            "Cancellation.cs", source, tree, compilation.GetSemanticModel(tree))!;
        return file.Statements.OfType<CSharpStatementInfo>()
            .First(statement => statement.Kind == "catch");
    }

    private static CSharpCompilation Compilation(params SyntaxTree[] trees)
    {
        CSharpCompilation compilation = CSharpCompilation.Create("CancellationControls", trees,
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        Assert.That(compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error),
            Is.Empty, string.Join(Environment.NewLine, trees.Select(tree => tree.ToString())));
        return compilation;
    }

    private static async Task AssertPreserves(CSharpStatementInfo handler, bool expected)
    {
        Assert.That((await AsyncSourceFacts.SemanticAsync(handler, CancellationToken.None))
            .IsCallerCancellationPreserved, Is.EqualTo(expected));
        Assert.That(handler.IsCallerCancellationPreserved, Is.EqualTo(expected), "repeated demand");
        Assert.That(await CSharpBindings.Build().AsyncAccessors["CSharpStatement"]
            ["IsCallerCancellationPreserved"](handler, CancellationToken.None),
            Is.EqualTo(expected), "async runtime binding");
    }
}
