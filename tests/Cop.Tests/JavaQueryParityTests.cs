using System.Reflection;
using Cop.Core;
using Cop.Providers;
using Cop.Providers.SourceModel;
using Cop.Providers.SourceParsers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class JavaQueryParityTests
{
    private string _root = "";

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
        _root = Path.Combine(RepoRoot, ".java-query-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task InterfaceAndEnvironmentHelpers_AreJavaConstrained_ColdAndWarm()
    {
        File.WriteAllText(Path.Combine(_root, "Example.java"), """
            interface Api {}
            @interface Marker {}
            class Example {
                void run() {
                    System.getenv("HOME");
                    java.lang.System.getenv("PATH");
                    System.getenv();
                    System.getProperty("HOME");
                    System.getProperties();
                    System.setProperty("HOME", "value");
                    getenv("not-system");
                    Custom.getenv("not-system");
                }
                String getenv(String name) { return name; }
            }
            class Custom { static String getenv(String name) { return name; } }
            """);
        File.WriteAllText(Path.Combine(_root, "Other.cs"), """
            interface Dotnet {}
            class Other {
                void Run() {
                    System.Environment.GetEnvironmentVariable("HOME");
                    System.getenv("not-java");
                }
            }
            """);
        await AssertEngineOutput("""
            import java
            import csharp
            import code
            let cb = codebase(java.parse(), csharp.parse())
            command MAIN = print(cb.Types.Where(java.isInterface(item)).Count) &
                print(cb.Calls.Where(java.isEnvironmentVariableAccess(item)).Count) &
                print(cb.Calls.Where(java.isEnvironmentVariableRead(item)).Count)
            """, ["2", "3", "2"]);
    }

    [Test]
    public async Task ChainedCallsWithTheSameStart_KeepDistinctSyntaxIdentities_ColdAndWarm()
    {
        RequireJdk();
        File.WriteAllText(Path.Combine(_root, "Chain.java"), """
            class Chain {
                void run() { Thread.currentThread().interrupt(); }
            }
            """);
        await AssertEngineOutput("""
            import java
            let cb = java.parse()
            command MAIN = print(cb.Calls.Count) &
                print(cb.Calls.Where(item.MemberName == 'currentThread').First.Parent.MemberName) &
                print(cb.Calls.Where(item.MemberName == 'interrupt').Count)
            """, ["2", "interrupt", "1"]);
    }

    [Test]
    public async Task ResourceAndRethrowProofs_RejectNestedConditionalAndReplacementFlows_ColdAndWarm()
    {
        RequireJdk();
        File.WriteAllText(Path.Combine(_root, "Cases.java"), ResourceSource);
        await AssertEngineOutput("""
            import java
            let cb = java.parse()
            command MAIN = print(cb.Statements:asJava:alwaysRethrowsOriginalException.Count) &
                print(cb.Calls:asJava:isUsingResource.Count) &
                print(cb.Calls:asJava:transfersResourceOwnership.Count) &
                foreach cb.Calls:asJava:isUsingResource => '{item.MemberName}'
            """, ["2", "2", "3", "wrap", "<init>"]);
    }

    [Test]
    public async Task InterruptedExceptionProofs_RequireOriginalPropagationOrBoundCurrentThreadRestoration_ColdAndWarm()
    {
        RequireJdk();
        File.WriteAllText(Path.Combine(_root, "Interrupts.java"), InterruptSource);
        await AssertEngineOutput("""
            import java
            let cb = java.parse()
            command MAIN = print(cb.Statements:asJava:isCancellationHandler.Count) &
                foreach cb.Statements:asJava:isCallerCancellationPreserved => '{item.Method.Name}'
            """, ["14", "propagate", "restore", "restoreReturn", "both", "derived"]);
    }

    [Test]
    public async Task Fallback_ProvesOnlySupportedSyntax_AndCacheRoundTripsKeepFlags()
    {
        const string source = """
            class Fallback {
                static class Resource implements AutoCloseable { public void close() {} }
                void work() throws Exception {}
                Resource wrap(Resource resource) { return resource; }
                Resource returned() { return new Resource(); }
                Resource local() { Resource resource = new Resource(); return resource; }
                void resource() throws Exception {
                    try (Resource resource = wrap(new Resource())) { work(); }
                    try (Resource resource = new Resource()) { work(); }
                }
                void original() throws Exception {
                    try { work(); } catch (Exception caught) { throw caught; }
                }
                void branches(boolean flag) throws Exception {
                    try { work(); } catch (Exception caught) {
                        if (flag) { throw caught; } else { throw (caught); }
                    }
                }
                void conditional(boolean flag) throws Exception {
                    try { work(); } catch (Exception caught) { if (flag) { throw caught; } }
                }
                void reassigned() throws Exception {
                    try { work(); } catch (Exception caught) { caught = new Exception(); throw caught; }
                }
                void replacement() throws Exception {
                    try { work(); } catch (Exception caught) { throw new Exception(); }
                }
                void nested() {
                    try { work(); } catch (Exception caught) {
                        Runnable nested = () -> { throw new RuntimeException(); };
                    }
                }
            }
            """;
        ISourceParser parser = CreateParser(_root);
        SourceFile file = (await parser.ParseAsync(Path.Combine(_root, "Fallback.java"), source))!;
        for (int round = 0; round < 3; round++)
        {
            StatementInfo[] statements = file.Statements.ToArray();
            Assert.Multiple(() =>
            {
                Assert.That(statements.Where(s => Flag(s, "IsOriginalExceptionAlwaysRethrown")).Select(s => s.Method?.Name),
                    Is.EqualTo(new[] { "original", "branches" }));
                Assert.That(statements.Where(s => Flag(s, "IsUsingResource")).Select(s => s.MemberName),
                    Is.EqualTo(new[] { "wrap", "<init>" }));
                Assert.That(statements.Where(s => Flag(s, "IsOwnershipTransferred")).Select(s => s.Method?.Name),
                    Is.EqualTo(new[] { "returned" }));
                Assert.That(statements.Count(s => Flag(s, "IsCancellationHandler")), Is.EqualTo(0));
                Assert.That(statements.Count(s => Flag(s, "IsCallerCancellationPreserved")), Is.EqualTo(0));
            });
            file = SourceGraphIdentityTests.RoundTrip(file);
            SourceGraphIdentityTests.Link(file);
        }

        SourceFile cleanup = (await parser.ParseAsync(Path.Combine(_root, "Cleanup.java"), """
            class Cleanup {
                void work() throws Exception {}
                void run() throws Exception {
                    try { work(); } catch (Exception caught) { throw caught; } finally { return; }
                }
            }
            """))!;
        Assert.That(cleanup.Statements.Count(s => Flag(s, "IsOriginalExceptionAlwaysRethrown")),
            Is.EqualTo(0));
    }

    [Test]
    public async Task AttributedQueryFlags_SurviveMaterializedSourceCacheRoundTrip()
    {
        RequireJdk();
        string path = Path.Combine(_root, "Interrupts.java");
        File.WriteAllText(path, InterruptSource);
        SourceFile file = (await CreateParser().ParseAsync(path, InterruptSource))!;
        foreach (StatementInfo statement in file.Statements)
        {
            await statement.EnsureSemanticAnalysisAsync();
        }

        file = SourceGraphIdentityTests.RoundTrip(file);
        SourceGraphIdentityTests.Link(file);
        Assert.Multiple(() =>
        {
            Assert.That(file.Statements.Count(s => Flag(s, "IsCancellationHandler")), Is.EqualTo(14));
            Assert.That(file.Statements
                .Where(s => Flag(s, "IsCallerCancellationPreserved")).Select(s => s.Method?.Name),
                Is.EqualTo(new[] { "propagate", "restore", "restoreReturn", "both", "derived" }));
        });
    }

    private static ISourceParser CreateParser(string? searchPath = null)
    {
        Assembly assembly = Assembly.LoadFrom(Path.Combine(RepoRoot,
            "packages", "languages", "java", "lib", "java-provider.dll"));
        foreach (string name in new[] { "JavaStatementInfo", "JavaTypeDeclaration", "JavaMethodDeclaration" })
        {
            Type model = assembly.GetType("Cop.Providers.SourceModel." + name, throwOnError: true)
                ?? throw new InvalidOperationException("Java model type is unavailable: " + name);
            MethodInfo register = model.GetMethod("RegisterCacheFactory", BindingFlags.Public | BindingFlags.Static)
                ?? throw new InvalidOperationException("Java cache registration is unavailable: " + name);
            register.Invoke(null, null);
        }

        Type parser = assembly.GetType("Cop.Providers.SourceParsers.JavaSourceParser", throwOnError: true)
            ?? throw new InvalidOperationException("Java parser type is unavailable.");
        return (ISourceParser)(Activator.CreateInstance(parser, [searchPath, null])
            ?? throw new InvalidOperationException("Could not instantiate the Java parser."));
    }

    private static bool Flag(StatementInfo statement, string name) =>
        (bool)(statement.GetType().GetProperty(name)?.GetValue(statement)
            ?? throw new InvalidOperationException("Java query flag is unavailable: " + name));

    private static void RequireJdk()
    {
        if (ToolchainProcess.FindExecutable("javac") is null)
        {
            Assert.Ignore("Compiler-bound query tests require a JDK; the fallback/cache test remains mandatory.");
        }
    }

    private async Task AssertEngineOutput(string script, string[] expected)
    {
        string path = Path.Combine(_root, "query.cop");
        File.WriteAllText(path, script);
        for (int run = 0; run < 2; run++)
        {
            EngineResult result = await Engine.RunProjectAsync(
                [Path.Combine(RepoRoot, "packages")], ["java"], _root, ["MAIN"],
                new EngineRunOptions { ScriptFiles = [path] });
            Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
            Assert.That(result.Outputs.Select(o => o.Message), Is.EqualTo(expected), $"Run {run + 1}");
        }
    }

    private const string ResourceSource = """
        class Cases {
            static class Resource implements AutoCloseable { public void close() {} }
            Resource field = new Resource();
            Resource wrap(Resource value) { return value; }
            void accept(Resource value) {}
            void work() throws Exception {}
            Resource returned() { return new Resource(); }
            Resource wrapped() { return wrap(new Resource()); }
            void owned() { this.field = new Resource(); }
            Resource local() { Resource value = new Resource(); return value; }
            void argument() { accept(new Resource()); }
            void array() { Resource[] values = { new Resource() }; }
            void resources() throws Exception {
                try (Resource value = wrap(new Resource())) { new Resource().close(); }
                try (Resource value = new Resource()) { work(); }
            }
            void original() throws Exception {
                try { work(); } catch (Exception caught) { throw caught; }
            }
            void branches(boolean flag) throws Exception {
                try { work(); } catch (Exception caught) {
                    if (flag) { throw caught; } else { throw (caught); }
                }
            }
            void conditional(boolean flag) throws Exception {
                try { work(); } catch (Exception caught) { if (flag) { throw caught; } }
            }
            void replacement() throws Exception {
                try { work(); } catch (Exception caught) { throw new Exception(); }
            }
            void reassigned() throws Exception {
                try { work(); } catch (Exception caught) { caught = new Exception(); throw caught; }
            }
            void nested() {
                try { work(); } catch (Exception caught) {
                    Runnable nested = () -> { throw new RuntimeException(); };
                }
            }
            void cleanup(boolean flag) throws Exception {
                try { work(); } catch (Exception caught) { throw caught; } finally { if (flag) return; }
            }
            void nestedCleanup() throws Exception {
                try { work(); } catch (Exception caught) {
                    try { throw caught; } finally { work(); }
                }
            }
        }
        """;

    private const string InterruptSource = """
        class Interrupts {
            static class Derived extends InterruptedException {}
            static class FakeThread {
                static FakeThread currentThread() { return new FakeThread(); }
                void interrupt() {}
            }
            Thread worker = new Thread();
            void block() throws InterruptedException {}
            void derivedBlock() throws Derived {}
            void propagate() throws InterruptedException {
                try { block(); } catch (InterruptedException caught) { throw caught; }
            }
            void restore() {
                try { block(); } catch (InterruptedException caught) { Thread.currentThread().interrupt(); }
            }
            void restoreReturn() {
                try { block(); } catch (InterruptedException caught) { Thread.currentThread().interrupt(); return; }
            }
            void both(boolean flag) throws InterruptedException {
                try { block(); } catch (InterruptedException caught) {
                    if (flag) { throw caught; } else { Thread.currentThread().interrupt(); }
                }
            }
            void lost() {
                try { block(); } catch (InterruptedException caught) {}
            }
            void wrongThread() {
                try { block(); } catch (InterruptedException caught) { worker.interrupt(); }
            }
            void replacement() {
                try { block(); } catch (InterruptedException caught) { throw new RuntimeException(caught); }
            }
            void cleared() {
                try { block(); } catch (InterruptedException caught) { Thread.currentThread().interrupt(); Thread.interrupted(); }
            }
            void reassigned() throws InterruptedException {
                try { block(); } catch (InterruptedException caught) { caught = new InterruptedException(); throw caught; }
            }
            void conditional(boolean flag) {
                try { block(); } catch (InterruptedException caught) { if (flag) { Thread.currentThread().interrupt(); } }
            }
            void nested() {
                try { block(); } catch (InterruptedException caught) {
                    Runnable nested = () -> Thread.currentThread().interrupt();
                }
            }
            void cleanup() {
                try { block(); } catch (InterruptedException caught) { Thread.currentThread().interrupt(); }
                finally { Thread.interrupted(); }
            }
            void broad() {
                try { block(); } catch (Exception caught) { Thread.currentThread().interrupt(); }
            }
            void future() {
                try { throw new java.util.concurrent.CancellationException(); }
                catch (java.util.concurrent.CancellationException caught) { Thread.currentThread().interrupt(); }
            }
            void derived() throws Derived {
                try { derivedBlock(); } catch (Derived caught) { throw caught; }
            }
            void fake() {
                try { block(); } catch (InterruptedException caught) { FakeThread.currentThread().interrupt(); }
            }
        }
        """;
}
