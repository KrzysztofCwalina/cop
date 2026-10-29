using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class JavaScriptProviderParityTests
{
    private string _root = null!;
    private static string RepoRoot
    {
        get
        {
            var directory = TestContext.CurrentContext.TestDirectory;
            while (directory is not null && !File.Exists(Path.Combine(directory, "cop.sln")))
                directory = Path.GetDirectoryName(directory);
            return directory ?? throw new InvalidOperationException("Repository root not found.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "cop-js-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, true);

    [Test]
    public void ShippedChecks_ReportConsoleAndAwaitLiteral_AndDoNotRejectAThenable()
    {
        File.WriteAllText(Path.Combine(_root, "sample.ts"), """
            export async function run() {
                console.log('output');
                await 42;
                await Promise.resolve(1);
                await { then(resolve) { resolve(1); } };
            }
            """);
        for (int run = 0; run < 2; run++)
        {
            var result = Engine.RunProject([Path.Combine(RepoRoot, "packages")], ["javascript-checks"], _root, []);
            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
            {
                "sample.ts(2): warning: Avoid console.log in production code [console-calls]",
                "sample.ts(3): error: Unexpected await of a non-Promise (non-Thenable) value [await-thenable]"
            }));
        }
    }

    [TestCase(".js")]
    [TestCase(".ts")]
    [TestCase(".jsx")]
    [TestCase(".tsx")]
    public void PublicCollections_KeepModuleMethodsNestedCallsAndDecodedLiterals_ColdAndWarm(string extension)
    {
        File.WriteAllText(Path.Combine(_root, "sample" + extension), """
            export function run() { return outer(inner('decoded')); }
            export const arrow = () => log('arrow');
            topLevel();
            """);
        var script = Path.Combine(_root, "check.cop");
        File.WriteAllText(script, """
            import javascript
            import code
            let cb = javascript.parse()
            command MAIN = print(cb.Methods.Count) & print(cb.Calls.Count) &
                print(cb.Calls.Where(item.MemberName == 'inner').First.ArgumentExpressions.First.StringValue) &
                print(cb.Calls.Where(item.MemberName == 'inner').First.Method.Name)
            """);
        for (int run = 0; run < 2; run++)
        {
            var result = Engine.RunProject([Path.Combine(RepoRoot, "packages")], ["javascript"], _root, ["MAIN"],
                additionalScriptFiles: [script]);
            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "2", "4", "decoded", "run" }));
        }
    }

    [Test]
    public void PublicBindingQueries_SelectOverloadsAndRefreshImportedDeclarations()
    {
        var library = Path.Combine(_root, "library.ts");
        File.WriteAllText(Path.Combine(_root, "main.ts"), """
            import { factory, Other } from './library';
            const value = factory();
            value.convert('text');
            value.convert(1);
            new Other().convert('text');
            declare const dynamic: any;
            dynamic.convert('unknown');
            missing();
            """);
        var script = Path.Combine(_root, "check.cop");
        File.WriteAllText(script, """
            import javascript
            let cb = javascript.parse()
            let calls = cb.Calls.Where(item.File.Path == 'main.ts')
            let first = calls.Where(item.Line == 3).First
            let second = calls.Where(item.Line == 4).First
            let third = calls.Where(item.Line == 5 && item.MemberName == 'convert').First
            command TEST = print(calls.Where(item.MemberName == 'factory').First.ValueType.Name) &
                print('{first.ReceiverType.Name}|{first.ValueType.Name}|{first.ReferencedCallable.DeclaringType}|{first.ReferencedCallable.Signature}|{first.ReferencedCallable.Parameters.First.Name}') &
                print('{second.ReceiverType.Name}|{second.ValueType.Name}|{second.ReferencedCallable.DeclaringType}|{second.ReferencedCallable.Signature}|{second.ReferencedCallable.Parameters.First.Name}') &
                print('{third.ReceiverType.Name}|{third.ValueType.Name}|{third.ReferencedCallable.DeclaringType}|{third.ReferencedCallable.Signature}|{third.ReferencedCallable.Parameters.First.Name}') &
                print(calls.Where(item.Line == 7).First.ReferencedCallable == nic) &
                print(calls.Where(item.MemberName == 'missing').First.ValueType == nic)
            """);
        foreach (var (type, returnType, resultValue) in new[]
            { ("First", "number", "1"), ("Changed", "boolean", "true") })
        {
            File.WriteAllText(library, $$"""
                export class {{type}} {
                    convert(value: string): {{returnType}};
                    convert(value: number): string;
                    convert(value: string | number): {{returnType}} | string { return {{resultValue}}; }
                }
                export class Other { convert(value: string): boolean { return true; } }
                export function factory(): {{type}} { return new {{type}}(); }
                """);
            for (int repeat = 0; repeat < 2; repeat++)
            {
                var result = Engine.RunProject([Path.Combine(RepoRoot, "packages")],
                    ["javascript"], _root, ["TEST"], additionalScriptFiles: [script]);
                Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
                Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
                {
                    type,
                    $"{type}|{returnType}|{type}|(value: string): {returnType}|string",
                    $"{type}|string|{type}|(value: number): string|number",
                    "Other|boolean|Other|(value: string): boolean|string",
                    "true", "true"
                }), string.Join("; ", result.Warnings ?? []));
            }
        }
    }

    [Test]
    public void AzureConsoleLogging_UsesParsedReceiver_AndExcludesOtherReceivers()
    {
        File.WriteAllText(Path.Combine(_root, "sample.ts"), """
            export class StorageClient {
                send(logger: { log(value: string): void }) {
                    console.log('output');
                    logger.log('clean');
                    const text = 'console.log("not a call")';
                }
            }
            """);
        var script = Path.Combine(_root, "check.cop");
        File.WriteAllText(script, """
            import javascript-library-azure-checks
            command TEST = CHECK(no-console-logging)
            """);
        var result = Engine.RunProject([Path.Combine(RepoRoot, "packages")],
            ["javascript-library-azure-checks"], _root, ["TEST"], additionalScriptFiles: [script]);
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[]
        {
            "sample.ts(3): error: Do not use console for logging — use @azure/logger instead"
        }));
    }

    [TestCase("FIRST")]
    [TestCase("TAKE")]
    public void DeferredSelection_DoesNotOpenAnUnrelatedLockedLaterFile(string command)
    {
        File.WriteAllText(Path.Combine(_root, "a.ts"), "export class First {}");
        var later = Path.Combine(_root, "z.ts");
        File.WriteAllText(later, "export class Later {}");
        var script = Path.Combine(_root, "check.cop");
        File.WriteAllText(script, """
            import javascript
            let cb = javascript.parse()
            command FIRST = print(cb.Types.First.Name)
            command TAKE = foreach cb.Types.take(1) => '{item.Name}'
            """);
        using var locked = new FileStream(later, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var result = Engine.RunProject([Path.Combine(RepoRoot, "packages")], ["javascript"], _root, [command],
            additionalScriptFiles: [script]);
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(new[] { "First" }));
        Assert.That((result.Warnings ?? []).Any(w => w.Contains("z.ts")), Is.False, string.Join("; ", result.Warnings ?? []));
    }
}
