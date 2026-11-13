using System.Reflection;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class PythonQueryParityTests
{
    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var repo = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "cop.sln")))
        {
            repo = repo.Parent;
        }
        _repo = repo?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        _root = Path.Combine(_repo, ".test-output", "python-query-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "target"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task EnvironmentQueriesUseQualifiedParsedCallsAndSubscriptContext()
    {
        Write("Input.py", """
            import os
            def inspect():
                a = os.getenv('A')
                b = os.environ.get('B')
                c = os.environ['C']
                os.environ['D'] = 'value'
                os.putenv('E', 'value')
                os.environ.update({'F': 'value'})
                wrong.getenv('G')
                wrong.os.getenv('H')
                getenv('I')
                os.getenv
                del os.environ['J']
                os.environ.items()
            """);
        await AssertProgram("""
            import code
            import python
            let cb = python.parse()
            command MAIN = {
                foreach cb.Statements:asPython:isEnvironmentVariableAccess.OrderBy(item.Line) => 'access:{item.Line}'
                foreach cb.Statements:asPython:isEnvironmentVariableRead.OrderBy(item.Line) => 'read:{item.Line}'
                0
            }
            """,
            ["access:3", "access:4", "access:5", "access:6", "access:7", "access:8", "access:13", "access:14",
                "read:3", "read:4", "read:5"]);
    }

    [Test]
    public async Task HandlerProofRequiresEveryExplicitPathAndSafeFinally()
    {
        Write("Input.py", """
            import asyncio
            def plain():
                try:
                    work()
                except asyncio.CancelledError:
                    raise
            def both_branches(flag):
                try:
                    work()
                except asyncio.CancelledError:
                    if flag:
                        raise
                    else:
                        raise
            def fallthrough_then_raise(flag):
                try:
                    work()
                except asyncio.CancelledError:
                    if flag:
                        log()
                    raise
            def caught_name():
                try:
                    work()
                except asyncio.CancelledError as error:
                    log(error)
                    raise error
            def conditional(flag):
                try:
                    work()
                except asyncio.CancelledError:
                    if flag:
                        raise
            def returns_on_one_path(flag):
                try:
                    work()
                except asyncio.CancelledError:
                    if flag:
                        raise
                    else:
                        return
            def replacement():
                try:
                    work()
                except asyncio.CancelledError:
                    raise RuntimeError()
            def reassigned():
                try:
                    work()
                except asyncio.CancelledError as error:
                    error = RuntimeError()
                    raise error
            def nested_function():
                try:
                    work()
                except asyncio.CancelledError:
                    def inner():
                        raise
            def finally_return():
                try:
                    work()
                except asyncio.CancelledError:
                    raise
                finally:
                    return
            def finally_raise():
                try:
                    work()
                except asyncio.CancelledError:
                    raise
                finally:
                    raise RuntimeError()
            def safe_finally():
                try:
                    work()
                except asyncio.CancelledError:
                    raise
                finally:
                    cleanup()
            def nested_try():
                try:
                    work()
                except asyncio.CancelledError:
                    try:
                        raise
                    except BaseException:
                        pass
            def loop():
                try:
                    work()
                except asyncio.CancelledError:
                    while condition():
                        raise
            async def awaited_cleanup():
                try:
                    await work()
                except asyncio.CancelledError:
                    await cleanup()
                    raise
            def context_manager():
                try:
                    work()
                except asyncio.CancelledError:
                    with manager():
                        raise
            def broad():
                try:
                    work()
                except BaseException:
                    raise
            def tuple_handler():
                try:
                    work()
                except (ValueError, asyncio.CancelledError):
                    raise
            """);
        await AssertProgram("""
            import code
            import python
            let cb = python.parse()
            command MAIN = {
                foreach cb.Statements:asPython:alwaysRethrowsOriginalException.OrderBy(item.Method.Name) => 'raise:{item.Method.Name}'
                foreach cb.Statements:asPython:isCallerCancellationPreserved.OrderBy(item.Method.Name) => 'preserve:{item.Method.Name}'
                foreach cb.Statements:asPython:isCancellationHandler.OrderBy(item.Method.Name) => 'handler:{item.Method.Name}'
                0
            }
            """,
            [
                "raise:both_branches", "raise:broad", "raise:caught_name", "raise:fallthrough_then_raise",
                "raise:plain", "raise:safe_finally", "raise:tuple_handler",
                "preserve:both_branches", "preserve:broad", "preserve:caught_name", "preserve:fallthrough_then_raise",
                "preserve:plain", "preserve:safe_finally", "preserve:tuple_handler",
                "handler:awaited_cleanup", "handler:both_branches", "handler:caught_name", "handler:conditional",
                "handler:context_manager", "handler:fallthrough_then_raise", "handler:finally_raise", "handler:finally_return",
                "handler:loop", "handler:nested_function", "handler:nested_try", "handler:plain",
                "handler:reassigned", "handler:replacement", "handler:returns_on_one_path", "handler:safe_finally",
                "handler:tuple_handler"
            ]);
    }

    [Test]
    public async Task ResourceQueriesIdentifyOnlyDirectManagersAndProvenEscapeSites()
    {
        Write("Input.py", """
            def direct():
                return create()
            def nested():
                return wrap(create())
            def local():
                value = create()
                return value
            def argument():
                consume(create())
            def broken():
                try:
                    return create()
                finally:
                    return None
            class Holder:
                def set(self):
                    self.resource = create()
                def other(self, other):
                    other.resource = create()
                def rebound(self, other):
                    self = other
                    self.resource = create()
                def overwritten(self):
                    self.resource = create()
                    self.resource = None
                @staticmethod
                def static(self):
                    self.resource = create()
            def managed():
                with wrap(create()) as resource:
                    body()
                with create():
                    body()
                outside = create()
            async def async_managed():
                async with create():
                    body()
            """);
        await AssertProgram("""
            import code
            import python
            let cb = python.parse()
            command MAIN = {
                foreach cb.Calls:asPython:transfersResourceOwnership.OrderBy(item.Method.Name) => 'escape:{item.Method.Name}:{item.MemberName}'
                foreach cb.Calls:asPython:isUsingResource.OrderBy(item.Method.Name) => 'manager:{item.Method.Name}:{item.MemberName}'
                0
            }
            """,
            ["escape:direct:create", "escape:nested:wrap", "escape:set:create",
                "manager:async_managed:create", "manager:managed:wrap", "manager:managed:create"]);
    }

    [Test]
    public async Task PythonDeclarationsAndAsyncIterationRemainLanguageConstrained()
    {
        Write("Input.py", """
            from typing import Protocol
            class Contract(Protocol):
                pass
            class Box[T]:
                def map[U](self, value: U) -> U:
                    return value
            class Plain:
                pass
            async def stream(values):
                async for value in values:
                    yield value
            def sync(values):
                for value in values:
                    yield from values
            """);
        Write("Foreign.cs", """
            namespace Fixture;
            public interface ForeignInterface { }
            public class Foreign<T> { public U Map<U>(U value) => value; }
            """);
        await AssertProgram("""
            import code
            import csharp
            import python
            let cb = codebase(csharp.parse(), python.parse())
            command MAIN = {
                foreach cb.Types:asPython:isInterface => 'protocol:{item.Name}'
                foreach cb.Types:asPython:isGenericType => 'generic:{item.Name}'
                foreach cb.Methods:asPython:isGenericMethod => 'method:{item.Name}'
                foreach cb.Statements:asPython:isAwaitForeach => 'async:{item.Method.Name}'
                foreach cb.Statements:asPython:isYield.OrderBy(item.Method.Name) => 'yield:{item.Method.Name}'
                0
            }
            """, ["protocol:Contract", "generic:Box", "method:map", "async:stream", "yield:stream", "yield:sync"]);
    }

    [Test]
    public async Task SyntaxOnlyBackendAndCachePreserveConservativePositiveAndNegativeFacts()
    {
        var assembly = Assembly.LoadFrom(Path.Combine(_repo, "packages", "languages", "python", "lib", "python-provider.dll"));
        var parserType = assembly.GetType("Cop.Providers.SourceParsers.PythonSourceParser", throwOnError: true)!;
        var backendType = assembly.GetType("Cop.Providers.SourceParsers.PythonAnalysisBackend", throwOnError: true)!;
        var parser = (ISourceParser)Activator.CreateInstance(parserType, [Enum.Parse(backendType, "SyntaxOnly"), null])!;
        foreach (var name in new[] { "PythonStatementInfo", "PythonTypeDeclaration", "PythonMethodDeclaration" })
        {
            assembly.GetType("Cop.Providers.SourceModel." + name, throwOnError: true)!
                .GetMethod("RegisterCacheFactory")!.Invoke(null, null);
        }
        var file = await parser.ParseAsync(Path.Combine(_root, "fallback.py"), """
            def good(flag):
                try:
                    work()
                except asyncio.CancelledError:
                    if flag:
                        raise
                    else:
                        raise
            def bad(flag):
                try:
                    work()
                except asyncio.CancelledError:
                    if flag:
                        raise
            def overridden():
                try:
                    work()
                except asyncio.CancelledError:
                    raise
                finally:
                    return
            def resources():
                with wrap(create()):
                    body()
                return create()
            def environment():
                os.getenv('A')
                os.environ['B'] = 'value'
                value = os.environ['C']
                wrong.os.getenv('D')
            class Box[T]:
                def map[U](self, value):
                    return value
            async def stream(values):
                async for value in values:
                    yield value
            async def async_manager():
                async with manager():
                    pass
            """);
        Assert.That(file, Is.Not.Null);
        for (int round = 0; round < 2; round++)
        {
            file = SourceGraphIdentityTests.RoundTrip(file!);
            SourceGraphIdentityTests.Link(file);
            var handlers = file.Statements.Where(s => s.IsErrorHandler).OrderBy(s => s.Method?.Name).ToList();
            Assert.That(handlers.Select(s => (s.Method?.Name, Flag(s, "IsOriginalExceptionAlwaysRethrown"),
                Flag(s, "IsCancellationHandler"), Flag(s, "IsCallerCancellationPreserved"))),
                Is.EqualTo(new[] { ("bad", false, true, false), ("good", true, true, true), ("overridden", false, true, false) }));
            Assert.That(file.Statements.Where(s => Flag(s, "IsUsingResource")).Select(s => s.MemberName),
                Is.EqualTo(new[] { "wrap", "manager" }));
            Assert.That(file.Statements.Where(s => Flag(s, "IsOwnershipTransferred")).Select(s => s.MemberName),
                Is.EqualTo(new[] { "create" }));
            Assert.That(file.Statements.Where(s => Flag(s, "IsEnvironmentVariableAccess")).Select(s => s.Line),
                Is.EqualTo(new[] { 27, 28, 29 }));
            Assert.That(file.Statements.Where(s => Flag(s, "IsEnvironmentVariableRead")).Select(s => s.Line),
                Is.EqualTo(new[] { 27, 29 }));
            Assert.That(file.Types.Single().TypeParameters, Is.EqualTo(new[] { "T" }));
            Assert.That(file.Types.Single().Methods.Single().TypeParameters, Is.EqualTo(new[] { "U" }));
            Assert.That(file.Statements.Where(s => Flag(s, "IsAsyncFor")).Select(s => s.Method?.Name),
                Is.EqualTo(new[] { "stream" }));
        }
    }

    private static bool Flag(StatementInfo statement, string name) =>
        statement.LanguageFlags?.Any(pair => pair.Key == name && pair.Value) == true;

    private void Write(string name, string content) => File.WriteAllText(Path.Combine(_root, "target", name), content);

    private async Task AssertProgram(string program, string[] expected)
    {
        File.WriteAllText(Path.Combine(_root, "main.cop"), program);
        for (int run = 0; run < 2; run++)
        {
            var result = await Engine.RunAsync(_root, Path.Combine(_root, "target"), new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")],
            });
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected), $"Run {run}");
            Assert.That(result.ExitCode, Is.EqualTo(0));
        }
    }
}
