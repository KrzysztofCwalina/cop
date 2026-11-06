using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class LanguageQueryReuseTests
{
    private string _root = null!;

    private static string RepoRoot
    {
        get
        {
            var directory = TestContext.CurrentContext.TestDirectory;
            while (directory is not null &&
                !File.Exists(Path.Combine(directory, "cop.sln")))
                directory = Path.GetDirectoryName(directory);
            return directory ??
                throw new InvalidOperationException("Repository root not found.");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(RepoRoot, ".artifacts", "language-query-reuse",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task JavaChecks_PreserveNestedReceiversSuppressionAndDiagnostics()
    {
        Write("Sample.java", """
            class Sample {
                void run() {
                    System.out.println("out");
                    System.err.printf("err");
                    System.exit(1);
                    error.printStackTrace();
                    Custom.out.println("other");
                    other.exit(1);
                    println("bare");
                    // cop-ignore: console-output
                    System.out.println("suppressed");
                }
            }
            """);

        await AssertChecks("java-checks",
        [
            "Sample.java(3): warning: Avoid System.out.println() — " +
                "use a logging framework instead [console-output]",
            "Sample.java(4): warning: Avoid System.err.printf() — " +
                "use a logging framework instead [console-output]",
            "Sample.java(6): warning: Do not call printStackTrace() — " +
                "log the exception via a logger [print-stack-trace]",
            "Sample.java(5): error: Avoid System.exit() — " +
                "throw an exception or return an error instead [system-exit]"
        ]);
    }

    [Test]
    public async Task JavaScriptChecks_PreserveCallKindsReceiversHandlersAndSuppression()
    {
        Write("sample.js", """
            function run() {
                console.log("out");
                console.warn("warning");
                alert("hello");
                eval("1");
                debugger;
                var value = 1;
                other.console.log("other");
                other.log("other");
                // cop-ignore: console-calls
                console.log("suppressed");
                try { run(); } catch (error) {}
                try { run(); } catch (error) { throw error; }
            }
            """);

        await AssertChecks("javascript-checks",
        [
            "sample.js(2): warning: Avoid console.log in production code [console-calls]",
            "sample.js(3): warning: Avoid console.warn in production code [console-calls]",
            "sample.js(4): error: Do not use alert() [alert-calls]",
            "sample.js(5): error: Do not use eval() — it is a security risk [eval-calls]",
            "sample.js(6): error: Remove debugger statement [debugger-statements]",
            "sample.js(7): warning: Use const or let instead of var [var-declarations]",
            "sample.js(12): warning: Use const or let instead of var [var-declarations]",
            "sample.js(13): warning: Use const or let instead of var [var-declarations]",
            "sample.js(12): warning: Do not swallow errors — " +
                "rethrow or handle explicitly [swallowed-exceptions]"
        ]);
    }

    [Test]
    public async Task PythonChecks_PreserveHandlerRethrowsTypeNamesAndSuppression()
    {
        Write("sample.py", """"
            class Sample:
                """Sample fixture."""
                def run(self):
                    """Run fixture."""
                    print('out')
                    eval('1')
                    other('safe')
                    # cop-ignore: print-calls
                    print('suppressed')
                    try:
                        other()
                    except Exception:
                        pass
                    try:
                        other()
                    except Exception:
                        raise
                    raise Exception('bad')
                    raise ValueError('specific')
            """");

        await AssertChecks("python-checks",
        [
            "sample.py(5): warning: Avoid print() — use logging instead [print-calls]",
            "sample.py(12): warning: Do not silence Exception — " +
                "reraise or catch a specific type [silenced-exceptions]",
            "sample.py(6): error: Do not use eval() — it is a security risk [no-eval]",
            "sample.py(12): warning: Empty except block — handle or log the exception " +
                "instead of silencing it [empty-except-body]",
            "sample.py(18): warning: Raise a specific exception type " +
                "instead of generic Exception [broad-exception-raise]"
        ]);
    }

    [Test]
    public async Task GoChecks_PreserveThrowKindTypeNamesSuppressionAndProductionScope()
    {
        Write("sample.go", """
            package lib
            func run() {
                panic("bad")
                os.Exit(1)
                time.Sleep(1)
                fmt.Print("one")
                fmt.Printf("two")
                fmt.Println("three")
                other.Print("safe")
                other.Exit(1)
                // cop-ignore: console-output
                fmt.Print("suppressed")
            }
            """);
        const string excluded = "package lib\nfunc run() { panic(\"ignored\") }\n";
        Write("ignored_test.go", excluded);
        Write(Path.Combine("vendor", "ignored.go"), excluded);

        await AssertChecks("go-checks",
        [
            "sample.go(3): warning: Avoid panic() in library code — return an error instead",
            "sample.go(4): warning: Avoid os.Exit() outside main — " +
                "it skips deferred cleanup; return an error instead",
            "sample.go(5): info: time.Sleep() is often a concurrency smell — " +
                "prefer channels, contexts, or timers",
            "sample.go(6): warning: Avoid fmt.Print in library code — " +
                "use a logging framework (log/slog)",
            "sample.go(7): warning: Avoid fmt.Printf in library code — " +
                "use a logging framework (log/slog)",
            "sample.go(8): warning: Avoid fmt.Println in library code — " +
                "use a logging framework (log/slog)"
        ]);
    }

    [Test]
    public async Task RustChecks_PreserveMemberSetsMacroKindsSuppressionAndProductionScope()
    {
        Write("sample.rs", """
            fn run() {
                value.unwrap();
                value.unwrap_err();
                value.unwrap_unchecked();
                value.expect("bad");
                value.expect_err("bad");
                panic!("bad");
                unreachable!();
                todo!();
                unimplemented!();
                mem::forget(value);
                other::forget(value);
                transmute(value);
                println!("out");
                print!("out");
                eprintln!("err");
                eprint!("err");
                dbg!(value);
                value.clone();
                value.safe();
                // cop-ignore: unwrap-calls
                value.unwrap();
            }
            """);
        const string excluded = "fn run() { value.unwrap(); panic!(\"ignored\"); }\n";
        foreach (var directory in new[] { "tests", "benches", "examples" })
            Write(Path.Combine(directory, "ignored.rs"), excluded);
        Write("build.rs", excluded);

        var expected = new List<string>();
        foreach (var line in new[] { 2, 3, 4 })
            expected.Add($"sample.rs({line}): warning: Avoid .unwrap() — " +
                "handle the Option/Result or propagate with ? [unwrap-calls]");
        foreach (var line in new[] { 5, 6 })
            expected.Add($"sample.rs({line}): info: .expect() panics on None/Err — " +
                "prefer propagating errors with ? [expect-calls]");
        foreach (var line in new[] { 7, 8 })
            expected.Add($"sample.rs({line}): warning: Avoid panic!/unreachable! " +
                "in library code — return a Result instead [panic-macros]");
        foreach (var line in new[] { 9, 10 })
            expected.Add($"sample.rs({line}): warning: Unfinished code — " +
                "todo!/unimplemented! will panic at runtime [unfinished-code]");
        expected.Add("sample.rs(11): warning: mem::forget leaks resources and skips Drop " +
            "— drop() deliberately or use a scope [mem-forget]");
        expected.Add("sample.rs(13): warning: transmute is a common source " +
            "of undefined behavior — prefer a safe conversion [transmute-calls]");
        var macros = new[] { "println!", "print!", "eprintln!", "eprint!", "dbg!" };
        for (var index = 0; index < macros.Length; index++)
            expected.Add($"sample.rs({index + 14}): warning: Avoid {macros[index]} — " +
                "use a logging framework (log/tracing) [console-output]");
        expected.Add("sample.rs(19): info: Review .clone() — it allocates; " +
            "borrow or move if the clone is unnecessary [needless-clone]");
        await AssertChecks("rust-checks", expected.ToArray());
    }

    [TestCase("go")]
    [TestCase("rust")]
    public async Task ComplexityChecks_PreserveExclusiveParameterAndStatementLimits(string language)
    {
        var parameters = Enumerable.Range(0, 8).Select(index =>
            language == "go" ? $"p{index} int" : $"p{index}: i32").ToArray();
        var prefix = language == "go" ? "package lib\n" : "";
        var declaration = language == "go" ? "func" : "fn";
        var below = $"{declaration} below({string.Join(", ", parameters.Take(7))}) {{\n" +
            string.Concat(Enumerable.Repeat("    work();\n", 50)) + "}\n";
        var above = $"{declaration} above({string.Join(", ", parameters)}) {{\n" +
            string.Concat(Enumerable.Repeat("    work();\n", 51)) + "}\n";
        var file = language == "go" ? "sample.go" : "sample.rs";
        Write(file, prefix + below + above);
        var line = language == "go" ? 54 : 53;
        var checkSuffix = language == "go" ? "" : " [too-many-arguments]";
        var largeSuffix = language == "go" ? "" : " [large-function]";

        await AssertChecks(language + "-checks",
        [
            $"{file}({line}): warning: above has more than 7 parameters — " +
                "group them into a struct" + checkSuffix,
            $"{file}({line}): info: above is large (> 50 statements) — " +
                "consider splitting it" + largeSuffix
        ], "too-many-arguments + large-function");
    }

    [TestCase("python-checks", "too-many-params",
        "LimitClient has methods with more than 5 parameters [too-many-params]")]
    [TestCase("python-library-checks", "too-many-positional-params",
        "LimitClient has a method with more than 5 positional parameters — " +
        "use keyword arguments")]
    public async Task PythonParameterChecks_PreserveExclusiveLimitAndSelectedType(
        string package, string check, string message)
    {
        Write("sample.py", """
            class LimitClient:
                def below(self, a, b, c, d):
                    pass
                def above(self, a, b, c, d, e):
                    pass
            class BoundaryClient:
                def below(self, a, b, c, d):
                    pass
            """);

        await AssertChecks(package, [$"sample.py(1): warning: {message}"], check);
    }

    [TestCase("python-checks", "sample.py", "no-six-import",
        "import six\n# cop-ignore: no-six-import\nimport six\n",
        "sample.py(1): error: Do not import six — " +
        "Python 2 is no longer supported [no-six-import]")]
    [TestCase("go-checks", "sample.go", "use-any",
        "package lib\ntype First interface{}\n" +
        "// cop-ignore: use-any\ntype Second interface{}\n",
        "sample.go(2): info: Use any instead of interface{} (Go 1.18+)")]
    [TestCase("rust-checks", "sample.rs", "eq-to-none",
        "fn run() {\n    value == None;\n" +
        "    // cop-ignore: eq-to-none\n    value == None;\n}\n",
        "sample.rs(2): warning: Use .is_none()/.is_some() " +
        "instead of == None / != None [eq-to-none]")]
    public async Task LineSuppression_PreservesExistingRuleSelection(
        string package, string file, string check, string source, string expected)
    {
        Write(file, source);
        await AssertChecks(package, [expected], check);
    }

    [TestCase("java-checks", "Sample.java",
        "class Sample { void run() { system.out.println(\"safe\"); " +
        "System.Out.println(\"safe\"); System.out.Println(\"safe\"); " +
        "system.exit(1); error.PrintStackTrace(); } }")]
    [TestCase("javascript-checks", "sample.js",
        "function run() { Console.log('safe'); Alert('safe'); Eval('1'); }")]
    [TestCase("python-checks", "sample.py", "Print('safe')\nEval('1')\n")]
    [TestCase("go-checks", "sample.go",
        "package lib\nfunc run() { Os.Exit(1); os.exit(1); " +
        "Fmt.Println(\"safe\"); fmt.println(\"safe\"); time.sleep(1) }\n")]
    [TestCase("rust-checks", "sample.rs",
        "fn run() { value.Unwrap(); value.Expect(\"safe\"); " +
        "value.Clone(); Transmute(value); }")]
    public async Task MemberAndReceiverQueries_PreserveCaseSensitivePolicy(
        string package, string file, string source)
    {
        Write(file, source);
        await AssertChecks(package, []);
    }

    private void Write(string path, string content)
    {
        var fullPath = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    private async Task AssertChecks(string package, string[] expected, string? checks = null)
    {
        List<string> commands = [];
        string[] scripts = [];
        if (checks is not null)
        {
            var script = Path.Combine(_root, "check.cop");
            File.WriteAllText(script,
                $"import {package}\ncommand TEST = CHECK({checks})\n");
            commands = ["TEST"];
            scripts = [script];
        }

        var result = (await Engine.RunProjectAsync([Path.Combine(RepoRoot, "packages")],
            [package], _root, commands, new EngineRunOptions { ScriptFiles = scripts }));
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected));
        Assert.That(result.ExitCode, Is.EqualTo(expected.Length));
    }
}
