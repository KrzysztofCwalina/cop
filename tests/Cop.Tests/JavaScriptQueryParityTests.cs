using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
[NonParallelizable]
public class JavaScriptQueryParityTests
{
    private string _root = null!;

    private static string RepoRoot
    {
        get
        {
            string? directory = TestContext.CurrentContext.TestDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "cop.sln")))
            {
                directory = Path.GetDirectoryName(directory);
            }
            return directory ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.GetFullPath(".cop-javascript-queries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(_root, recursive: true);
    }

    [TestCase(false)]
    [TestCase(true)]
    public Task CallsTo_UsesSyntacticReceiverAndDecodedArgumentPositions_ColdAndWarm(bool fallback) =>
        AssertQueries(fallback, """
            function run(name) {
                ffi.Register("print", "TOKEN");
                ffi.Register(name, "print");
                other.Register("print", "TOKEN");
            }
            """, """
            import javascript
            import code
            let cb = javascript.parse()
            let calls = cb.Calls:callsTo('ffi', ['Register'])
            command TEST = print(calls.Count) &
                print(calls.First.TypeName) &
                print(calls.First.ArgumentExpressions.First.StringValue) &
                print(calls.Last.ArgumentExpressions.First.Kind) &
                print(calls.Last.ArgumentExpressions.Last.StringValue)
            """, ["2", "ffi", "print", "identifier", "print"]);

    [TestCase(false)]
    [TestCase(true)]
    public Task EnvironmentAccess_RecognizesPropertiesIndexesWritesAndDeletes_ColdAndWarm(bool fallback) =>
        AssertQueries(fallback, """
            function run(name) {
                consume(process.env.TOKEN);
                consume(process.env['TOKEN']);
                consume(process['env'][name]);
                process.env.TOKEN = 'changed';
                delete process.env.TOKEN;
                process.env.TOKEN ??= 'default';
                consume(other.process.env.TOKEN);
                consume('process.env.TOKEN');
            }
            """, """
            import javascript
            let cb = javascript.parse()
            let accesses = cb.Statements:asJavaScript:isEnvironmentVariableAccess
            let reads = cb.Statements:asJavaScript:isEnvironmentVariableRead
            command TEST = print(accesses.Count) & print(reads.Count) &
                foreach accesses => '{item.Line}|{item.IsEnvironmentVariableRead}'
            """, ["6", "4", "2|true", "3|true", "4|true", "5|false", "6|false", "7|true"]);

    [TestCase(false)]
    [TestCase(true)]
    public Task OriginalRethrows_ProveAllExplicitBranchesAndRejectMutationFinallyAndNestedFunctions_ColdAndWarm(bool fallback) =>
        AssertQueries(fallback, """
            function direct() { try { work(); } catch (e) { log(e); throw e; } }
            function branches() { try { work(); } catch (e) { if (flag) { throw e; } else { throw (e); } } }
            function fallthrough() { try { work(); } catch (e) { if (flag) { log(e); } throw e; } }
            function conditional() { try { work(); } catch (e) { if (flag) { throw e; } } }
            function replacement() { try { work(); } catch (e) { throw new Error('replacement'); } }
            function mutation() { try { work(); } catch (e) { e = new Error(); throw e; } }
            function nested() { try { work(); } catch (e) { function later() { throw e; } } }
            function finalizer() { try { work(); } catch (e) { throw e; } finally { return; } }
            function abort() { try { work(); } catch (e) { if (e.name === 'AbortError') { throw e; } return; } }
            function swallowedAbort() { try { work(); } catch (e) { if (e.name === 'AbortError') { log(e); } return; } }
            function changedAbort() { try { work(); } catch (e) { if (e.name === 'AbortError') { e = replacement(); throw e; } } }
            function anyName() { try { work(); } catch (e) { if (e.name === 'OtherError') { throw e; } } }
            function abortFinally() { try { work(); } catch (e) { if (e.name === 'AbortError') { throw e; } } finally { return; } }
            function abortNested() { try { work(); } catch (e) { if (e.name === 'AbortError') { function later() { throw e; } } } }
            function evalMutation() { try { work(); } catch (e) { eval('e = other'); throw e; } }
            """, """
            import javascript
            let cb = javascript.parse()
            command TEST = foreach cb.Statements:asJavaScript.Where(item.Kind == 'catch') =>
                '{item.Method.Name}|{item.IsOriginalExceptionAlwaysRethrown}|{item.IsCancellationHandler}|{item.IsCallerCancellationPreserved}'
            """, [
                "direct|true|false|false",
                "branches|true|false|false",
                "fallthrough|true|false|false",
                "conditional|false|false|false",
                "replacement|false|false|false",
                "mutation|false|false|false",
                "nested|false|false|false",
                "finalizer|false|false|false",
                "abort|false|true|true",
                "swallowedAbort|false|true|false",
                "changedAbort|false|true|false",
                "anyName|false|false|false",
                "abortFinally|false|true|false",
                "abortNested|false|true|false",
                "evalMutation|false|false|false"
            ]);

    [TestCase(false)]
    [TestCase(true)]
    public Task Resources_SeparateDirectTransfersAndUsingInitializersFromNestedCalls_ColdAndWarm(bool fallback) =>
        AssertQueries(fallback, """
            class Owner {
                create() { return new Resource(); }
                store() { this.value = new Resource(); }
                local() { const value = new Resource(); return value; }
                argument() { consume(new Resource()); }
                transformed() { return new Resource().transform(); }
                overridden() { try { return new Resource(); } finally { return other; } }
                use() { using value = new Resource(make()); }
                async useAsync() { await using value = acquire(); }
            }
            """, """
            import javascript
            let cb = javascript.parse()
            let transfers = cb.Calls:asJavaScript:transfersResourceOwnership
            let resources = cb.Calls:asJavaScript:isUsingResource
            command TEST = print(transfers.Count) &
                print('transfer|{transfers.First.Method.Name}|{transfers.First.MemberName}') &
                print('transfer|{transfers.Last.Method.Name}|{transfers.Last.MemberName}') &
                print(resources.Count) &
                print('using|{resources.First.Method.Name}|{resources.First.MemberName}') &
                print('using|{resources.Last.Method.Name}|{resources.Last.MemberName}')
            """, [
                "2", "transfer|create|Resource", "transfer|store|Resource",
                "2", "using|use|Resource", "using|useAsync|acquire"
            ]);

    [TestCase(false)]
    [TestCase(true)]
    public Task DeclarationAndIterationHelpers_UseActualParsedFacts_ColdAndWarm(bool fallback) =>
        AssertQueries(fallback, """
            export interface Contract { map<U>(value: U): U; }
            export class Factory<T> { make<U>(value: U): U { return value; } }
            export function identity<T>(value: T): T { return value; }
            export const arrow = <T>(value: T): T => value;
            export async function* values(items) { for await (const value of items) { yield value; } }
            """, """
            import javascript
            let cb = javascript.parse()
            command TEST = print(cb.Types:isInterface.First.Name) &
                print(cb.Types:asJavaScript:isGenericType.First.Name) &
                print(cb.Methods:asJavaScript:isGenericMethod.Count) &
                print(cb.Methods:asJavaScript:isGeneratorMethod.First.Name) &
                print(cb.Methods:asJavaScript:isArrowMethod.First.Name) &
                print(cb.Statements:asJavaScript:isYield.Count) &
                print(cb.Statements:asJavaScript:isAwaitForeach.Count)
            """, ["Contract", "Factory", "4", "values", "arrow", "1", "1"]);

    private async Task AssertQueries(bool fallback, string source, string query, string[] expected)
    {
        File.WriteAllText(Path.Combine(_root, "sample.ts"), source);
        string script = Path.Combine(_root, "check.cop");
        File.WriteAllText(script, query);
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            if (fallback)
            {
                Environment.SetEnvironmentVariable("PATH", "");
            }
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var result = await Engine.RunProjectAsync([Path.Combine(RepoRoot, "packages")],
                    ["javascript"], _root, ["TEST"], new EngineRunOptions { ScriptFiles = [script] });
                Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
                Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected),
                    $"fallback={fallback}, run={repeat}: {string.Join("; ", result.Warnings ?? [])}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }
}
