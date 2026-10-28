using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

[TestFixture]
public class JavaScriptParityTests
{
    [SetUp]
    public void RequireInstalledCompiler()
    {
        if (JavaScriptToolchain.Discover("module.ts") is null)
            Assert.Ignore("Native compiler projection requires an already-installed Node.js and TypeScript.");
    }

    [TestCase("--require")]
    [TestCase("--import")]
    public async Task Cli_InheritedNodePreload_DoesNotExecuteDuringNativeAnalysis(string option)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-js-preload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var target = Path.Combine(root, "target");
            Directory.CreateDirectory(target);
            var marker = Path.Combine(root, "executed.txt");
            var preload = Path.Combine(root, "preload.cjs");
            File.WriteAllText(preload, "require('fs').writeFileSync(" +
                System.Text.Json.JsonSerializer.Serialize(marker) + ", 'executed');");
            File.WriteAllText(Path.Combine(target, "tsconfig.json"), """{ "include": ["*.ts"] }""");
            File.WriteAllText(Path.Combine(target, "sample.ts"), """
                export function run(): number { return outer(inner('decoded')); }
                export const arrow = () => log('arrow');
                topLevel();
                run();
                """);
            var repository = TestContext.CurrentContext.TestDirectory;
            while (!File.Exists(Path.Combine(repository, "cop.sln")))
                repository = Path.GetDirectoryName(repository)
                    ?? throw new InvalidOperationException("Repository root not found.");
            var script = Path.Combine(root, "check.cop");
            File.WriteAllText(script, $"""
                feed '{Path.Combine(repository, "packages").Replace("\\", "\\\\").Replace("'", "\\'")}'
                import javascript
                import code
                let cb = javascript.parse()
                command MAIN = print(cb.Projects.Count) & print(cb.Methods.Count) & print(cb.Calls.Count) &
                    print(cb.Calls.Where(item.MemberName == 'inner').First.ArgumentExpressions.First.StringValue) &
                    print(cb.Calls.Where(item.MemberName == 'run').First.ValueType.Name) &
                    print(cb.Calls.Where(item.MemberName == 'run').First.ReferencedCallable.Signature)
                """);
            var executable = Path.Combine(TestContext.CurrentContext.TestDirectory,
                OperatingSystem.IsWindows() ? "cop.exe" : "cop");
            var start = new System.Diagnostics.ProcessStartInfo(executable)
            {
                WorkingDirectory = root, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var argument in new[] { script, "-t", target }) start.ArgumentList.Add(argument);
            var preloadArgument = option == "--import" ? new Uri(preload).AbsoluteUri : preload;
            start.Environment["NODE_OPTIONS"] = option + " " + System.Text.Json.JsonSerializer.Serialize(preloadArgument);
            start.Environment["NODE_PATH"] = root;
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }
            var output = await stdout;
            var errors = await stderr;
            Assert.That(process.ExitCode, Is.EqualTo(0), errors);
            Assert.That(output.Replace("\r\n", "\n").Trim(), Is.EqualTo("1\n2\n5\ndecoded\nnumber\n(): number"));
            Assert.That(errors, Does.Not.Contain("simpler syntax analysis"));
            Assert.That(errors, Does.Not.Contain("Auto-restoring"));
            Assert.That(File.Exists(marker), Is.False, "Inherited Node options must not execute a runtime preload.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void Parse_CheckerBinding_DistinguishesReceiverValueAndSelectedOverload()
    {
        var file = new JavaScriptSourceParser().Parse("binding.ts", """
            class First {
                convert(value: string): number;
                convert(value: number): string;
                convert(value: string | number): string | number { return value; }
            }
            class Second { convert(value: string): boolean { return true; } }
            function factory(): First { return new First(); }
            const first = factory();
            first.convert('value');
            first.convert(42);
            new Second().convert('value');
            declare const dynamic: any;
            dynamic.convert('unknown');
            missing('unknown');
            """)!;
        var calls = file.Statements.Where(s => s.Kind == "call").ToList();
        var factory = calls.Single(s => s.MemberName == "factory");
        Assert.That(factory.ReceiverType, Is.Null);
        Assert.That(factory.ValueType!.Name, Is.EqualTo("First"));
        Assert.That(factory.ReferencedCallable!.Name, Is.EqualTo("factory"));
        Assert.That(factory.ReferencedCallable.ReturnType!.Name, Is.EqualTo("First"));
        var converts = calls.Where(s => s.MemberName == "convert").ToList();
        Assert.That(converts.Select(s => s.ReceiverType?.Name), Is.EqualTo(new string?[] { "First", "First", "Second", null }));
        Assert.That(converts.Select(s => s.ValueType?.Name), Is.EqualTo(new string?[] { "number", "string", "boolean", null }));
        Assert.That(converts.Take(3).Select(s => s.ReferencedCallable!.DeclaringType),
            Is.EqualTo(new[] { "First", "First", "Second" }));
        Assert.That(converts.Take(3).Select(s => s.ReferencedCallable!.Signature),
            Is.EqualTo(new[] { "(value: string): number", "(value: number): string", "(value: string): boolean" }));
        Assert.That(converts.Take(3).Select(s => s.ReferencedCallable!.Parameters.Single().Name),
            Is.EqualTo(new[] { "string", "number", "string" }));
        Assert.That(converts.Take(3).Select(s => s.ReferencedCallable!.Line), Is.EqualTo(new int?[] { 2, 3, 6 }));
        Assert.That(converts.Last().ReferencedCallable, Is.Null);
        Assert.That(calls.Single(s => s.MemberName == "missing").ReferencedCallable, Is.Null);
        Assert.That(converts.Take(3).All(s => s.ReferencedCallable!.SourceFile!.EndsWith("binding.ts")), Is.True);
    }

    [Test]
    public void Parse_ModuleFunctionsArrowsAndSemicolonFreeDeclarations_RetainsExactFacts()
    {
        var file = new JavaScriptSourceParser().Parse("module.ts", """
            const value = 1
            export class AfterVariable { run(): number { return value } }
            export async function load(url: string): Promise<string> { return await fetch(url) }
            export const arrow = (n: number): number => outer(inner(n))
            topLevel('ready')
            """)!;

        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "AfterVariable" }));
        Assert.That(file.Methods.Select(m => m.Name), Is.EqualTo(new[] { "load", "arrow" }));
        Assert.That(file.Methods[0].ReturnType!.ToString(), Is.EqualTo("Promise<string>"));
        Assert.That(file.Methods[0].Parameters[0].Line, Is.EqualTo(3));
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "fetch", "outer", "inner", "topLevel" }));
        Assert.That(file.Statements.Single(s => s.MemberName == "inner").Method, Is.SameAs(file.Methods[1]));
        Assert.That(file.Statements.Single(s => s.MemberName == "topLevel").IsInMethod, Is.False);
    }

    [Test]
    public void Parse_NestingAndDecodedArguments_PreservesExpressionStructure()
    {
        var file = new JavaScriptSourceParser().Parse("calls.ts", """
            function run(flag: boolean) {
                if (flag) { sink(wrap('a\u0062\n'), obj.value); }
                await 42;
            }
            """)!;
        var calls = file.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new[] { "sink", "wrap" }));
        Assert.That(calls[0].CallTarget!.Kind, Is.EqualTo("identifier"));
        Assert.That(calls[0].CallTarget!.Name, Is.EqualTo("sink"));
        Assert.That(calls[1].ArgumentExpressions.Single().StringValue, Is.EqualTo("ab\n"));
        Assert.That(calls[0].ArgumentExpressions[1].Kind, Is.EqualTo("memberAccess"));
        Assert.That(calls[0].Parent!.Kind, Is.EqualTo("if"));
        Assert.That(calls[0].Parent!.Condition, Is.EqualTo("flag"));
        Assert.That(calls[1].Parent, Is.SameAs(calls[0]));
        Assert.That(calls[0].Children, Does.Contain(calls[1]));
        Assert.That(file.Statements.Single(s => s.Kind == "await").Expression, Is.EqualTo("42"));
        Assert.That(calls.All(s => ReferenceEquals(s.Method, file.Methods.Single())), Is.True);
    }

    [Test]
    public void Parse_TypeScriptMembers_ReportsVisibilitySignaturesDocsAndInterfaces()
    {
        var file = new JavaScriptSourceParser().Parse("members.ts", """
            interface Contract<T> extends Parent<T> { execute(value?: T): Promise<T>; }
            enum Mode { First = 4, Second = 'next' }
            /** A service. */
            @service()
            export abstract class Service<T> implements Contract<T> {
                private readonly name: string = 'secret';
                protected count: number = 0;
                /** Executes work. */
                @logged
                public async execute({ value }: { value: T }, ...rest: T[]): Promise<T> { return value; }
                private hidden(): void {}
                get label(): string { return this.name; }
            }
            """)!;
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "Contract", "Mode", "Service" }));
        Assert.That(file.Types[0].BaseTypes, Is.EqualTo(new[] { "Parent<T>" }));
        Assert.That(file.Types[0].Methods.Single().ReturnType!.ToString(), Is.EqualTo("Promise<T>"));
        Assert.That(file.Types[1].EnumValues, Is.EqualTo(new[] { "First", "Second" }));
        var service = file.Types[2];
        Assert.That(service.IsAbstract, Is.True);
        Assert.That(service.HasDocComment, Is.True);
        Assert.That(service.Decorators, Is.EqualTo(new[] { "service" }));
        Assert.That(service.Fields.Select(f => f.Name), Is.EqualTo(new[] { "name", "count" }));
        Assert.That(service.Fields[0].IsPrivate, Is.True);
        Assert.That(service.Fields[0].IsReadonly, Is.True);
        Assert.That(service.Fields[1].IsProtected, Is.True);
        var execute = service.Methods.Single(m => m.Name == "execute");
        Assert.That(execute.ReturnType!.GenericArguments.Single().Name, Is.EqualTo("T"));
        Assert.That(execute.Parameters.Select(p => p.Name), Is.EqualTo(new[] { "{ value }", "rest" }));
        Assert.That(execute.Parameters[1].IsVariadic, Is.True);
        Assert.That(execute.HasDocComment, Is.True);
        Assert.That(execute.Decorators, Is.EqualTo(new[] { "logged" }));
        Assert.That(service.Methods.Single(m => m.Name == "hidden").IsPrivate, Is.True);
        Assert.That(service.Properties.Single().Name, Is.EqualTo("label"));
        Assert.That(service.Properties.Single().HasGetter, Is.True);
    }

    [TestCase(".js")]
    [TestCase(".jsx")]
    [TestCase(".ts")]
    [TestCase(".tsx")]
    [TestCase(".mjs")]
    [TestCase(".cjs")]
    [TestCase(".mts")]
    [TestCase(".cts")]
    public void Parse_AllSupportedExtensions_RetainsModuleCalls(string extension)
    {
        var parser = new JavaScriptSourceParser();
        Assert.That(parser.Extensions, Does.Contain(extension));
        var file = parser.Parse("module" + extension, "export const work = () => log('ok'); work();")!;
        Assert.That(file.Methods.Select(m => m.Name), Is.EqualTo(new[] { "work" }));
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "log", "work" }));
    }

    [Test]
    public void Parse_AliasOptionalGenericImportsAndSpans_PreservesNeutralFacts()
    {
        var file = new JavaScriptSourceParser().Parse("facts.ts", """
            import Default, { thing as renamed } from 'module';
            import * as namespace from 'other';
            export type Result<T> = { value: T; read(input?: T): T };
            export function identity<T>(value?: T): T { return value!; }
            """)!;
        Assert.That(file.Imports.Select(i => (i.Module, i.Name, i.Alias, i.IsWildcard)),
            Is.EqualTo(new[]
            {
                ("module", "default", "Default", false), ("module", "thing", "renamed", false),
                ("other", (string?)null, "namespace", true)
            }));
        var alias = file.Types.Single();
        Assert.That(alias.Kind, Is.EqualTo(TypeKind.Alias));
        Assert.That(alias.AliasTarget!.OriginalText, Is.EqualTo("{ value: T; read(input?: T): T }"));
        Assert.That(alias.TypeParameters, Is.EqualTo(new[] { "T" }));
        Assert.That(alias.Properties.Single().Name, Is.EqualTo("value"));
        Assert.That(alias.Methods.Single().Parameters.Single().IsOptional, Is.True);
        var method = file.Methods.Single();
        Assert.That(method.TypeParameters, Is.EqualTo(new[] { "T" }));
        Assert.That(method.Parameters.Single().IsOptional, Is.True);
        Assert.That((method.Line, method.Column, method.EndLine, method.EndColumn), Is.EqualTo((4, 1, 4, 61)));
    }

    [Test]
    public void Parse_ResolvedReceiverAndUnknownReceiver_DoesNotInventDynamicTypes()
    {
        var file = new JavaScriptSourceParser().Parse("semantic.ts", """
            class Actual { execute() {} }
            function run(client: Actual, dynamic: any) {
                client.execute();
                dynamic.execute();
                console.log('output');
            }
            """)!;
        var calls = file.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.TypeName), Is.EqualTo(new string?[] { "Actual", null, "Console" }));
        Assert.That(calls.Select(s => s.CallTarget!.Target!.Name), Is.EqualTo(new[] { "client", "dynamic", "console" }));
        Assert.That(calls[0].Method!.Name, Is.EqualTo("run"));
    }

    [TestCase(".jsx")]
    [TestCase(".tsx")]
    public void Parse_JSXAndTSX_KeepsEmbeddedCallsWithoutTreatingTagsAsCalls(string extension)
    {
        var file = new JavaScriptSourceParser().Parse("view" + extension, """
            export const View = () => <section title={format('title')}><Child value={compute(2)} /></section>;
            """)!;
        Assert.That(file.ParseErrors, Is.Empty);
        Assert.That(file.Methods.Select(m => m.Name), Is.EqualTo(new[] { "View" }));
        Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
            Is.EqualTo(new[] { "format", "compute" }));
    }

    [TestCase("tsconfig.json", ".ts")]
    [TestCase("jsconfig.json", ".js")]
    public void Parse_PathAliasAndEditedImportedDeclaration_UsesRealCurrentSemanticTypes(string configName, string extension)
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-js-semantic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, configName), """
                { "compilerOptions": { "baseUrl": ".", "paths": { "@client": ["client.ts"] } } }
                """);
            var imported = Path.Combine(root, "client.ts");
            File.WriteAllText(imported, "class First { send(value: string): number { return 1; } } export const client = new First();");
            var main = Path.Combine(root, "main" + extension);
            const string source = "import { client } from '@client'; client.send('value');";
            var parser = new JavaScriptSourceParser();
            using var scope = new Cop.Core.ToolchainExecutionScope();
            var first = parser.Parse(main, source)!;
            Assert.That(first.Statements.Single(s => s.Kind == "call").TypeName, Is.EqualTo("First"));
            var firstCall = first.Statements.Single(s => s.Kind == "call");
            Assert.That(firstCall.ReceiverType!.Name, Is.EqualTo("First"));
            Assert.That(firstCall.ValueType!.Name, Is.EqualTo("number"));
            Assert.That(firstCall.ReferencedCallable!.Signature, Is.EqualTo("(value: string): number"));
            Assert.That(firstCall.ReferencedCallable.DeclaringType, Is.EqualTo("First"));
            Assert.That(Path.GetFullPath(firstCall.ReferencedCallable.SourceFile!), Is.EqualTo(imported));
            File.WriteAllText(imported, "class Second { send(value: string): boolean { return true; } } export const client = new Second();");
            File.SetLastWriteTimeUtc(imported, DateTime.UtcNow.AddSeconds(2));
            var second = parser.Parse(main, source)!;
            Assert.That(second.Statements.Single(s => s.Kind == "call").TypeName, Is.EqualTo("Second"));
            var secondCall = second.Statements.Single(s => s.Kind == "call");
            Assert.That(secondCall.ReceiverType!.Name, Is.EqualTo("Second"));
            Assert.That(secondCall.ValueType!.Name, Is.EqualTo("boolean"));
            Assert.That(secondCall.ReferencedCallable!.Signature, Is.EqualTo("(value: string): boolean"));
            Assert.That(secondCall.ReferencedCallable.DeclaringType, Is.EqualTo("Second"));
            Assert.That(parser.CanCache(main), Is.False, "Semantic dependencies are not source-only persistent cache keys.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void Parse_FiftyFilesInOneExecution_ProducesIdenticalExactFactsWithAmortizedCompilerStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), "cop-js-scale-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "tsconfig.json"), """{ "include": ["*.ts"] }""");
            var sources = Enumerable.Range(0, 50).Select(i =>
                $"export function work{i}() {{ return outer(inner('value{i}')); }}").ToArray();
            for (int i = 0; i < sources.Length; i++)
                File.WriteAllText(Path.Combine(root, $"scale-{i:D2}.ts"), sources[i]);
            var parser = new JavaScriptSourceParser();
            using var scope = new Cop.Core.ToolchainExecutionScope();
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var files = new List<SourceFile>();
            long firstMilliseconds = 0;
            for (int i = 0; i < sources.Length; i++)
            {
                files.Add(parser.Parse(Path.Combine(root, $"scale-{i:D2}.ts"), sources[i])!);
                if (i == 0) firstMilliseconds = timer.ElapsedMilliseconds;
            }
            timer.Stop();
            Assert.That(files.Sum(f => f.Methods.Count), Is.EqualTo(50));
            Assert.That(files.Sum(f => f.Statements.Count(s => s.Kind == "call")), Is.EqualTo(100));
            Assert.That(files.SelectMany(f => f.Statements.Where(s => s.MemberName == "inner"))
                .Select(s => s.ArgumentExpressions.Single().StringValue),
                Is.EqualTo(Enumerable.Range(0, 50).Select(i => "value" + i)));
            TestContext.Out.WriteLine($"Native 50 files with tsconfig: total={timer.ElapsedMilliseconds}ms; first={firstMilliseconds}ms; " +
                $"remaining49={timer.ElapsedMilliseconds - firstMilliseconds}ms. No persistent semantic-model cache.");
        }
        finally { Directory.Delete(root, true); }
    }

    [Test]
    public void Parse_UnsupportedLocalCompiler_DoesNotDisableAWorkingCompilerInAnotherPackage()
    {
        var installed = JavaScriptToolchain.Discover("module.ts")!;
        var root = Path.Combine(Path.GetTempPath(), "cop-js-compilers-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var package in new[] { "broken", "working" })
            {
                var library = Path.Combine(root, package, "node_modules", "typescript", "lib");
                Directory.CreateDirectory(library);
                File.WriteAllText(Path.Combine(library, "typescript.js"), package == "broken"
                    ? "module.exports = {};"
                    : "module.exports = require(" + System.Text.Json.JsonSerializer.Serialize(installed.CompilerPath) + ");");
            }
            using var scope = new Cop.Core.ToolchainExecutionScope();
            var parser = new JavaScriptSourceParser(Path.GetDirectoryName(installed.NodePath));
            _ = parser.Parse(Path.Combine(root, "broken", "test.ts"), "function run() {}");
            var working = parser.Parse(Path.Combine(root, "working", "test.ts"),
                "export interface Resolved { execute(): string; }")!;
            Assert.That(working.Types.Single().Methods.Single().Name, Is.EqualTo("execute"));
            Assert.That(working.Types.Single().Methods.Single().ReturnType!.Name, Is.EqualTo("string"));
        }
        finally { Directory.Delete(root, true); }
    }
}

[TestFixture]
public class JavaScriptFallbackParityTests
{
    [Test]
    public void Parse_MissingToolchain_ReportsWarningAndKeepsModuleFunctionsAndNestedCalls()
    {
        Cop.Core.ProviderWarnings.Clear();
        var parser = new JavaScriptSourceParser(toolchainSearchPath: "");
        var file = parser.Parse("fallback.ts", """
            const value = 1
            export class AfterVariable { private run(): number { return value } }
            export function load(url: string): string { return outer(inner('ab')); }
            export const arrow = (n: number): number => outer(inner(n))
            topLevel('ready')
            """)!;
        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "AfterVariable" }));
        Assert.That(file.Methods.Select(m => m.Name), Is.EqualTo(new[] { "load", "arrow" }));
        Assert.That(file.Methods[0].ReturnType!.Name, Is.EqualTo("string"));
        Assert.That(file.Methods[0].Parameters.Single().Line, Is.EqualTo(3));
        Assert.That(file.Types.Single().Methods.Single().IsPrivate, Is.True);
        var calls = file.Statements.Where(s => s.Kind == "call").ToList();
        Assert.That(calls.Select(s => s.MemberName), Is.EqualTo(new[] { "outer", "inner", "outer", "inner", "topLevel" }));
        Assert.That(calls[1].Parent, Is.SameAs(calls[0]));
        Assert.That(calls[1].ArgumentExpressions.Single().StringValue, Is.EqualTo("ab"));
        Assert.That(calls[2].Method, Is.SameAs(file.Methods[1]));
        Assert.That(calls[4].IsInMethod, Is.False);
        Assert.That(calls.Select(s => s.ReceiverType), Is.All.Null);
        Assert.That(calls.Select(s => s.ValueType), Is.All.Null);
        Assert.That(calls.Select(s => s.ReferencedCallable), Is.All.Null);
        Assert.That(Cop.Core.ProviderWarnings.Drain().Single(), Does.Contain("using simpler syntax analysis"));
    }

    [Test]
    public void Discovery_NodeWithoutCompiler_IsNotMistakenForTypeScript()
    {
        var node = Cop.Core.ToolchainProcess.FindExecutable("node");
        if (node is null) Assert.Ignore("Node.js is not installed.");
        var path = Path.Combine(Path.GetTempPath(), "cop-no-compiler-" + Guid.NewGuid().ToString("N"), "test.ts");
        Assert.That(JavaScriptToolchain.Discover(path, Path.GetDirectoryName(node!)), Is.Null);
        Assert.That(JavaScriptToolchain.Discover(path, ""), Is.Null);
    }

    [Test]
    public void Parse_FallbackCleanControl_DoesNotInventCallsInsideStringsOrComments()
    {
        var file = new JavaScriptSourceParser("").Parse("clean.js", """
            // outer(inner('not a call'))
            const text = 'console.log("not a call")'
            function clean() { return text; }
            """)!;
        Assert.That(file.Methods.Select(m => m.Name), Is.EqualTo(new[] { "clean" }));
        Assert.That(file.Statements.Count(s => s.Kind == "call"), Is.EqualTo(0));
    }

    [Test]
    public void Parse_FallbackSemicolonFreeAliasAndNestedGeneric_DoesNotSwallowTheFollowingClass()
    {
        var file = new JavaScriptSourceParser("").Parse("aliases.ts", """
            export type Alias<T> = Map<string, Array<T>>
            export class After { execute(): string { return 'ok' } }
            """)!;
        Assert.That(file.Types.Select(t => t.Name), Is.EqualTo(new[] { "Alias", "After" }));
        Assert.That(file.Types[0].Kind, Is.EqualTo(TypeKind.Alias));
        Assert.That(file.Types[0].AliasTarget!.OriginalText, Is.EqualTo("Map<string, Array<T>>"));
        Assert.That(file.Types[0].TypeParameters, Is.EqualTo(new[] { "T" }));
        Assert.That(file.Types[1].Methods.Single().Name, Is.EqualTo("execute"));
    }

    [Test]
    public void Parse_InstalledButUnsupportedCompiler_WarnsAndUsesFallback()
    {
        var node = Cop.Core.ToolchainProcess.FindExecutable("node");
        if (node is null) Assert.Ignore("Node.js is not installed.");
        var root = Path.Combine(Path.GetTempPath(), "cop-js-unsupported-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "node_modules", "typescript", "lib");
        Directory.CreateDirectory(library);
        try
        {
            File.WriteAllText(Path.Combine(library, "typescript.js"), "module.exports = {};");
            Cop.Core.ProviderWarnings.Clear();
            var parser = new JavaScriptSourceParser(Path.GetDirectoryName(node!));
            var file = parser.Parse(Path.Combine(root, "source.ts"), "function run() { outer(inner('ok')); }")!;
            Assert.That(file.Methods.Single().Name, Is.EqualTo("run"));
            Assert.That(file.Statements.Where(s => s.Kind == "call").Select(s => s.MemberName),
                Is.EqualTo(new[] { "outer", "inner" }));
            Assert.That(Cop.Core.ProviderWarnings.Drain(), Has.Some.Contains("lacks required API"));
        }
        finally { Directory.Delete(root, true); }
    }
}
