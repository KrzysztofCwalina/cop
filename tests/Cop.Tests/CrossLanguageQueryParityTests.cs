using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class CrossLanguageQueryParityTests
{
    private string _root = null!;
    private string _repo = null!;

    [SetUp]
    public void SetUp()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "cop.sln")))
        {
            directory = directory.Parent;
        }

        _repo = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        _root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "query-parity", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "target"));
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [TestCase("csharp", "cs", """
        class Fixture {
            void Run(string name) {
                ffi.Register("print", "TOKEN");
                ffi.Register(name, "print");
                other.Register("print", "TOKEN");
            }
        }
        """)]
    [TestCase("java", "java", """
        class Fixture {
            void run(String name) {
                ffi.Register("print", "TOKEN");
                ffi.Register(name, "print");
                other.Register("print", "TOKEN");
            }
        }
        """)]
    [TestCase("javascript", "js", """
        function run(name) {
            ffi.Register("print", "TOKEN");
            ffi.Register(name, "print");
            other.Register("print", "TOKEN");
        }
        """)]
    [TestCase("python", "py", """
        def run(name):
            ffi.Register('print', 'TOKEN')
            ffi.Register(name, 'print')
            other.Register('print', 'TOKEN')
        """)]
    [TestCase("go", "go", """
        package fixture
        func Run(name string) {
            ffi.Register("print", "TOKEN")
            ffi.Register(name, "print")
            other.Register("print", "TOKEN")
        }
        """)]
    [TestCase("rust", "rs", """
        fn run(name: &str) {
            ffi::Register("print", "TOKEN");
            ffi::Register(name, "print");
            other::Register("print", "TOKEN");
        }
        """)]
    [TestCase("cop", "cop", """
        function run(name: string) => [
            ffi.Register('print', 'TOKEN'),
            ffi.Register(name, 'print'),
            other.Register('print', 'TOKEN')
        ]
        """)]
    public async Task CommonCallQueriesExecuteAgainstEverySourceLanguage(
        string language, string extension, string source)
    {
        File.WriteAllText(Path.Combine(_root, "target", "fixture." + extension), source);
        await AssertProgram($$"""
            import code
            import {{language}}
            let cb = {{language}}.parse()
            let registrations = cb.Calls:callsTo('ffi', ['Register'])
            let receivers = registrations:hasIdentifierReceiver('ffi')
            let strings = registrations:hasStringArgument(0, 'print')
            let identifiers = registrations:hasIdentifierArgument(0, 'name')
            let markers = registrations:stringArgumentContainsAny(1, ['TOKEN'])
            let wrong-case = cb.Calls:callsTo('ffi', ['register'])
            command MAIN = {
                print('{registrations.Count} ' +
                    '{receivers.Count} {strings.Count} {identifiers.Count} ' +
                    '{markers.Count} {wrong-case.Count}')
                0
            }
            """, ["2 2 1 1 1 0"]);
    }

    [Test]
    public async Task RustEnvironmentQueriesHandlePathsWithoutMatchingOtherModulesOrMembers()
    {
        File.WriteAllText(Path.Combine(_root, "target", "fixture.rs"), """
            struct Generic<T> { value: T }
            struct Ordinary;
            fn generic<T>(value: T) -> T { value }
            fn run() {
                std::env::var("A");
                env::var_os("B");
                std::env::vars();
                env::vars_os();
                std::env::set_var("C", "value");
                env::remove_var("C");
                std::env::args();
                other::env::var("D");
                std::env::VAR("E");
                ENV::var("F");
                std::ENV::var("G");
            }
            """);
        await AssertProgram("""
            import code
            import rust
            let cb = rust.parse()
            command MAIN = {
                foreach cb.Calls:isEnvironmentVariableRead => 'read:{item.Line}'
                foreach cb.Calls:isEnvironmentVariableAccess => 'access:{item.Line}'
                foreach cb.Types:asRust:isGenericType => 'type:{item.Name}'
                foreach cb.Methods:asRust:isGenericMethod => 'method:{item.Name}'
                0
            }
            """, ["read:5", "read:6", "read:7", "read:8",
                "access:5", "access:6", "access:7", "access:8", "access:9", "access:10",
                "type:Generic", "method:generic"]);
    }

    [Test]
    public async Task GoEnvironmentQueriesDistinguishEnvironmentApisFromGeneralExpansion()
    {
        File.WriteAllText(Path.Combine(_root, "target", "fixture.go"), """
            package fixture
            type Generic[T any] struct { Value T }
            type Ordinary struct {}
            func Run() {
                os.Getenv("A")
                os.LookupEnv("B")
                os.Environ()
                os.ExpandEnv("$A")
                os.Setenv("C", "value")
                os.Unsetenv("C")
                os.Clearenv()
                os.Expand("value", mapper)
                other.Getenv("D")
                os.getenv("E")
            }
            """);
        await AssertProgram("""
            import code
            import go
            let cb = go.parse()
            command MAIN = {
                foreach cb.Calls:isEnvironmentVariableRead => 'read:{item.Line}'
                foreach cb.Calls:isEnvironmentVariableAccess => 'access:{item.Line}'
                foreach cb.Types:asGo:isGenericType => 'type:{item.Name}'
                0
            }
            """, ["read:5", "read:6", "read:7", "read:8",
                "access:5", "access:6", "access:7", "access:8", "access:9", "access:10", "access:11",
                "type:Generic"]);
    }

    [TestCase("import csharp\nimport go\nimport rust")]
    [TestCase("import rust\nimport go\nimport csharp")]
    public async Task EnvironmentOverloadsRemainLanguageScopedInMixedPrograms(string imports)
    {
        File.WriteAllText(Path.Combine(_root, "target", "fixture.cs"), """
            class Fixture {
                void Run() {
                    Environment.GetEnvironmentVariable("C");
                    os.Getenv("not-go");
                }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "target", "fixture.go"), """
            package fixture
            func Run() {
                os.Getenv("G")
                Environment.GetEnvironmentVariable("not-csharp")
            }
            """);
        File.WriteAllText(Path.Combine(_root, "target", "fixture.rs"), """
            fn run() {
                env::var("R");
                os::Getenv("not-go");
            }
            """);
        await AssertProgram($$"""
            import code
            {{imports}}
            let cb = codebase(csharp.parse(), go.parse(), rust.parse())
            command MAIN = {
                foreach cb.Calls:isEnvironmentVariableAccess => '{item.File.Language}:{item.Line}'
                0
            }
            """, ["csharp:3", "go:3", "rust:2"]);
    }

    [TestCase("import csharp\nimport java\nimport javascript\nimport python\nimport go\nimport rust")]
    [TestCase("import rust\nimport go\nimport python\nimport javascript\nimport java\nimport csharp")]
    public async Task InterfaceQueriesComposeAcrossPackagesWithoutImportOrderDependence(string imports)
    {
        File.WriteAllText(Path.Combine(_root, "target", "fixture.cs"),
            "interface CSharpContract {} class CSharpClass {}");
        File.WriteAllText(Path.Combine(_root, "target", "Fixture.java"),
            "interface JavaContract {} class JavaClass {}");
        File.WriteAllText(Path.Combine(_root, "target", "fixture.ts"),
            "export interface TypeScriptContract {} export class TypeScriptClass {}");
        File.WriteAllText(Path.Combine(_root, "target", "fixture.py"),
            "from typing import Protocol\nclass PythonContract(Protocol):\n    pass\nclass PythonClass:\n    pass\n");
        File.WriteAllText(Path.Combine(_root, "target", "fixture.go"),
            "package fixture\ntype GoContract interface { Run() }\ntype GoStruct struct {}\n");
        File.WriteAllText(Path.Combine(_root, "target", "fixture.rs"),
            "trait RustContract { fn run(&self); }\nstruct RustStruct;\n");
        await AssertProgram($$"""
            import code
            {{imports}}
            let cb = codebase(csharp.parse(), java.parse(), javascript.parse(),
                python.parse(), go.parse(), rust.parse())
            command MAIN = {
                foreach cb.Types:isInterface => '{item.Name}'
                foreach cb.Types.Where(csharp.isInterface(item)) => 'csharp:{item.Name}'
                0
            }
            """, ["CSharpContract", "JavaContract", "TypeScriptContract", "PythonContract", "GoContract",
                "RustContract", "csharp:CSharpContract"]);
    }

    [Test]
    public async Task ProviderBackedRethrowQueriesComposeWithoutTreatingConditionalThrowsAsProof()
    {
        File.WriteAllText(Path.Combine(_root, "target", "fixture.cs"), """
            using System;
            class Fixture {
                void Run(bool condition) {
                    try { throw new Exception(); } catch (Exception error) { throw; }
                    try { throw new Exception(); } catch (Exception error) { if (condition) throw; }
                }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "target", "Fixture.java"), """
            class Fixture {
                void run(boolean condition) {
                    try { throw new RuntimeException(); } catch (RuntimeException error) { throw error; }
                    try { throw new RuntimeException(); } catch (RuntimeException error) { if (condition) throw error; }
                }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "target", "fixture.js"), """
            function run(condition) {
                try { work(); } catch (error) { throw error; }
                try { work(); } catch (error) { if (condition) throw error; }
            }
            """);
        File.WriteAllText(Path.Combine(_root, "target", "fixture.py"), """
            def run(condition):
                try:
                    work()
                except Exception:
                    raise
                try:
                    work()
                except Exception:
                    if condition:
                        raise
            """);
        await AssertProgram("""
            import code
            import python
            import javascript
            import java
            import csharp
            let cb = codebase(csharp.parse(), java.parse(), javascript.parse(), python.parse())
            command MAIN = {
                foreach cb.Statements:alwaysRethrowsOriginalException => '{item.File.Language}:{item.Line}'
                0
            }
            """, ["csharp:4", "java:3", "javascript:2", "python:4"]);
    }

    private async Task AssertProgram(string program, string[] expected)
    {
        File.WriteAllText(Path.Combine(_root, "main.cop"), program);
        for (int run = 0; run < 2; run++)
        {
            var result = await Engine.RunAsync(_root, Path.Combine(_root, "target"), new EngineRunOptions
            {
                AdditionalFeedPaths = [Path.Combine(_repo, "packages")]
            });
            Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
            Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected));
            Assert.That(result.ExitCode, Is.Zero);
        }
    }
}
