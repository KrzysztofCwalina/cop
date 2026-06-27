using System.Diagnostics;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// End-to-end coverage for the expanded rust-checks package and the two violation-exclusion
/// mechanisms documented in docs/languages/rust.md:
///   1. whole-rule exclusion via the `-` (collection difference) operator, and
///   2. single-instance exclusion via `// cop-ignore:` source comments.
///
/// Both mechanisms were broken before this change (the `-` operator threw "Cannot subtract
/// non-numeric values"; `cop-ignore` was parsed but never enforced), so these tests fail
/// against the old build and pass against the new one. They spawn the published
/// install/win-x64/cop.exe — the same faithful path used by IssueRegressionTests.
/// </summary>
[TestFixture]
public class ExclusionRegressionTests
{
    private string _workDir = null!;

    [SetUp]
    public void SetUp()
    {
        _workDir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "exclusion-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_workDir))
        {
            try { Directory.Delete(_workDir, recursive: true); } catch { }
        }
    }

    /// <summary>Each new Clippy-inspired rust-check must fire at its exact line.</summary>
    [Test]
    public void RustChecks_NewClippyChecks_FireAtExpectedLines()
    {
        // Lines are significant — assertions below pin each violation to a line number.
        WriteRust("lib.rs",
            "use std::collections::*;\n" +              // 1: wildcard-imports
            "\n" +
            "/// Reinterprets bits.\n" +
            "pub fn reinterpret(x: f32) -> u32 {\n" +
            "    unsafe { std::mem::transmute(x) }\n" +  // 5: transmute-calls
            "}\n" +
            "\n" +
            "pub unsafe fn danger() {\n" +              // 8: missing-safety-doc
            "    let _ = 1;\n" +
            "}\n" +
            "\n" +
            "/// Checks option.\n" +
            "pub fn check(opt: Option<i32>) -> bool {\n" +
            "    opt == None\n" +                        // 14: eq-to-none
            "}\n" +
            "\n" +
            "/// Many params.\n" +
            "pub fn many(a: i32, b: i32, c: i32, d: i32, e: i32, f: i32, g: i32, h: i32) -> i32 {\n" + // 18: too-many-arguments
            "    a\n" +
            "}\n" +
            "\n" +
            "/// Clones a vec.\n" +
            "pub fn dupe(v: Vec<i32>) -> Vec<i32> {\n" +
            "    v.clone()\n" +                          // 24: needless-clone
            "}\n" +
            "\n" +
            "/// Frees memory.\n" +
            "pub fn freed(b: Box<i32>) {\n" +
            "    std::mem::forget(b);\n" +               // 29: mem-forget
            "}\n");

        var result = RunCop($"run rust-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(1): warning: Avoid wildcard imports"), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(5): warning: transmute is a common source"), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(8): warning: public unsafe fn danger needs a doc comment"), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(14): warning: Use .is_none()/.is_some()"), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(18): warning: many has more than 7 parameters"), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(24): info: Review .clone()"), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(29): warning: mem::forget leaks resources"), Describe(result));
    }

    /// <summary>large-function must fire only above its statement threshold.</summary>
    [Test]
    public void RustChecks_LargeFunction_FiresAboveThreshold()
    {
        var body = string.Concat(Enumerable.Repeat("    work();\n", 51));
        WriteRust("big.rs", "/// Big.\npub fn big() {\n" + body + "}\n");
        WriteRust("small.rs", "/// Small.\npub fn small() {\n    work();\n}\n");

        var result = RunCop($"run rust-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("big.rs(2): info: big is large"), Describe(result));
        Assert.That(output, Does.Not.Contain("small is large"), Describe(result));
    }

    /// <summary>
    /// Whole-rule exclusion: `rust-checks - panic-macros - needless-clone` drops exactly those
    /// rules' violations while keeping the rest. Regression for the broken `-` operator.
    /// </summary>
    [Test]
    public void RuleExclusion_SubtractOperator_DropsOnlyNamedRules()
    {
        WriteRust("lib.rs",
            "/// Loads.\n" +
            "pub fn load() {\n" +
            "    let b = parse().unwrap();\n" +  // 3: unwrap-calls (kept)
            "    panic!(\"boom\");\n" +          // 4: panic-macros (excluded)
            "    let c = b.clone();\n" +          // 5: needless-clone (excluded)
            "}\n");
        WriteFile("my-checks.cop",
            "import rust-checks\n" +
            "import code\n" +
            "let my-checks = rust-checks - panic-macros - needless-clone\n" +
            "command MAIN = CHECK(my-checks)\n");

        var result = RunCop($"\"{Path.Combine(_workDir, "my-checks.cop")}\" -t \"{_workDir}\"", _workDir);
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(CountOccurrences(output, "Avoid .unwrap()"), Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Not.Contain("Avoid panic!"), Describe(result));
        Assert.That(output, Does.Not.Contain("Review .clone()"), Describe(result));
    }

    /// <summary>
    /// Single-instance exclusion: a `// cop-ignore:` comment exempts exactly the next line.
    /// Regression for cop-ignore not being enforced by the built-in package.
    /// </summary>
    [Test]
    public void InstanceExclusion_CopIgnore_SuppressesOnlyAnnotatedLine()
    {
        WriteRust("lib.rs",
            "/// Loads.\n" +
            "pub fn load() {\n" +
            "    // cop-ignore: unwrap-calls\n" +
            "    let a = parse().unwrap();\n" +  // 4: ignored
            "    let b = parse().unwrap();\n" +  // 5: flagged
            "}\n");

        var result = RunCop($"run rust-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(CountOccurrences(output, "Avoid .unwrap()"), Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(5): warning: Avoid .unwrap()"), Describe(result));
        Assert.That(output, Does.Not.Contain("lib.rs(4): warning: Avoid .unwrap()"), Describe(result));
    }

    /// <summary>Positive composition runs only the selected checks (control for exclusion).</summary>
    [Test]
    public void PositiveComposition_RunsOnlySelectedChecks()
    {
        WriteRust("lib.rs",
            "/// Loads.\n" +
            "pub fn load() {\n" +
            "    let b = parse().unwrap();\n" +  // 3: unwrap-calls
            "    panic!(\"boom\");\n" +          // 4: panic-macros (not selected)
            "}\n");
        WriteFile("only.cop",
            "import rust-checks\n" +
            "import code\n" +
            "let my-checks = unwrap-calls\n" +
            "command MAIN = CHECK(my-checks)\n");

        var result = RunCop($"\"{Path.Combine(_workDir, "only.cop")}\" -t \"{_workDir}\"", _workDir);
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("lib.rs(3): warning: Avoid .unwrap()"), Describe(result));
        Assert.That(output, Does.Not.Contain("Avoid panic!"), Describe(result));
    }

    /// <summary>
    /// Each violation is tagged with the NAME of the check that produced it, shown as a trailing
    /// `[check-name]`. This is the discovery mechanism: the bracketed name is exactly the
    /// identifier the user subtracts (proven separately by RuleExclusion_SubtractOperator_*).
    /// Powered by the general `nameof(...)` language feature — no engine special-casing of checks.
    /// </summary>
    [Test]
    public void RuleNames_AppearAsBracketedTags_NextToTheirMessage()
    {
        WriteRust("lib.rs",
            "/// Loads.\n" +
            "pub fn load() {\n" +
            "    let b = parse().unwrap();\n" +  // 3: unwrap-calls
            "    panic!(\"boom\");\n" +          // 4: panic-macros
            "    let c = b.clone();\n" +          // 5: needless-clone
            "}\n");

        var result = RunCop($"run rust-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        // The tag is appended after the message on the same line. Assert adjacency with a regex
        // (avoids hard-coding the em-dash in the message text).
        Assert.That(output, Does.Match(@"Avoid \.unwrap\(\).*\[unwrap-calls\]"), Describe(result));
        Assert.That(output, Does.Match(@"Avoid panic!.*\[panic-macros\]"), Describe(result));
        Assert.That(output, Does.Match(@"Review \.clone\(\).*\[needless-clone\]"), Describe(result));
        // Sanity: no empty brackets — every tag names a real, subtractable check identifier.
        Assert.That(output, Does.Not.Contain("[]"), Describe(result));
    }

    // ====================================================================
    // Other languages — same two exclusion mechanisms must work everywhere.
    // (cop-ignore was unenforced and `-` threw before this change; csharp also
    // hit a same-name-overload mis-dispatch when imported and composed.)
    // ====================================================================

    [Test]
    public void CSharp_InstanceExclusion_CopIgnore_SuppressesOnlyAnnotatedLine()
    {
        WriteFile("S.cs",
            "class C {\n" +
            "    void M() {\n" +
            "        // cop-ignore: var-declarations\n" +
            "        var a = 1;\n" +   // 4: ignored
            "        var b = 2;\n" +   // 5: flagged
            "    }\n" +
            "}\n");

        var result = RunCop($"run csharp-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(CountOccurrences(output, "Do not use 'var'"), Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("S.cs(5): error: Do not use 'var' for b"), Describe(result));
        Assert.That(output, Does.Not.Contain("S.cs(4): error: Do not use 'var'"), Describe(result));
    }

    [Test]
    public void CSharp_RuleExclusion_SubtractOperator_DropsOnlyNamedRule()
    {
        WriteFile("S.cs",
            "class C {\n" +
            "    void M() {\n" +
            "        Console.WriteLine(\"a\");\n" +  // 3: console-calls (kept)
            "        var x = 1;\n" +                // 4: var-declarations (excluded)
            "    }\n" +
            "}\n");
        WriteFile("checks.cop",
            "import csharp-checks\n" +
            "import code\n" +
            "command MAIN = CHECK(csharp-checks - var-declarations)\n");

        var result = RunCop($"\"{Path.Combine(_workDir, "checks.cop")}\" -t \"{_workDir}\"", _workDir);
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Not.Contain("Do not use 'var'"), Describe(result));
        Assert.That(output, Does.Contain("S.cs(3): warning: Don't use Console.WriteLine"), Describe(result));
    }

    [Test]
    public void Python_InstanceExclusion_CopIgnore_SuppressesOnlyAnnotatedLine()
    {
        WriteFile("s.py",
            "def f():\n" +
            "    # cop-ignore: print-calls\n" +
            "    print(\"a\")\n" +  // 3: ignored
            "    print(\"b\")\n");  // 4: flagged

        var result = RunCop($"run python-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(CountOccurrences(output, "Avoid print()"), Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("s.py(4): warning: Avoid print()"), Describe(result));
        Assert.That(output, Does.Not.Contain("s.py(3): warning: Avoid print()"), Describe(result));
    }

    [Test]
    public void Python_RuleExclusion_SubtractOperator_DropsOnlyNamedRule()
    {
        WriteFile("s.py",
            "def f():\n" +
            "    print(\"a\")\n" +   // 2: print-calls (excluded)
            "    eval(\"1+1\")\n");  // 3: no-eval (kept)
        WriteFile("checks.cop",
            "import python-checks\n" +
            "import code\n" +
            "command MAIN = CHECK(python-checks - print-calls)\n");

        var result = RunCop($"\"{Path.Combine(_workDir, "checks.cop")}\" -t \"{_workDir}\"", _workDir);
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Not.Contain("Avoid print()"), Describe(result));
        Assert.That(output, Does.Contain("s.py(3): error: Do not use eval()"), Describe(result));
    }

    [Test]
    public void JavaScript_InstanceExclusion_CopIgnore_SuppressesOnlyAnnotatedLine()
    {
        WriteFile("s.js",
            "function f() {\n" +
            "    // cop-ignore: console-calls\n" +
            "    console.log(\"a\");\n" +  // 3: ignored
            "    console.log(\"b\");\n" +  // 4: flagged
            "}\n");

        var result = RunCop($"run javascript-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(CountOccurrences(output, "Avoid console.log"), Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("s.js(4): warning: Avoid console.log"), Describe(result));
        Assert.That(output, Does.Not.Contain("s.js(3): warning: Avoid console.log"), Describe(result));
    }

    [Test]
    public void JavaScript_RuleExclusion_SubtractOperator_DropsOnlyNamedRule()
    {
        WriteFile("s.js",
            "function f() {\n" +
            "    console.log(\"a\");\n" +  // 2: console-calls (excluded)
            "    eval(\"1+1\");\n" +       // 3: eval-calls (kept)
            "}\n");
        WriteFile("checks.cop",
            "import javascript-checks\n" +
            "import code\n" +
            "command MAIN = CHECK(javascript-checks - console-calls)\n");

        var result = RunCop($"\"{Path.Combine(_workDir, "checks.cop")}\" -t \"{_workDir}\"", _workDir);
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Not.Contain("Avoid console.log"), Describe(result));
        Assert.That(output, Does.Contain("s.js(3): error: Do not use eval()"), Describe(result));
    }

    [Test]
    public void Java_InstanceExclusion_CopIgnore_SuppressesOnlyAnnotatedLine()
    {
        WriteFile("S.java",
            "public class S {\n" +
            "    void m() {\n" +
            "        // cop-ignore: console-output\n" +
            "        System.out.println(\"a\");\n" +  // 4: ignored
            "        System.out.println(\"b\");\n" +  // 5: flagged
            "    }\n" +
            "}\n");

        var result = RunCop($"run java-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(CountOccurrences(output, "System.out.println()"), Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("S.java(5): warning: Avoid System.out.println()"), Describe(result));
        Assert.That(output, Does.Not.Contain("S.java(4): warning: Avoid System.out.println()"), Describe(result));
    }

    [Test]
    public void Java_RuleExclusion_SubtractOperator_DropsOnlyNamedRule()
    {
        WriteFile("S.java",
            "public class S {\n" +
            "    void m() {\n" +
            "        System.out.println(\"a\");\n" +  // 3: console-output (excluded)
            "        System.exit(1);\n" +            // 4: system-exit (kept)
            "    }\n" +
            "}\n");
        WriteFile("checks.cop",
            "import java-checks\n" +
            "import code\n" +
            "command MAIN = CHECK(java-checks - console-output)\n");

        var result = RunCop($"\"{Path.Combine(_workDir, "checks.cop")}\" -t \"{_workDir}\"", _workDir);
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Not.Contain("System.out.println()"), Describe(result));
        Assert.That(output, Does.Contain("S.java(4): error: Avoid System.exit()"), Describe(result));
    }

    [Test]
    public void Go_Checks_FireAtExpectedLines()
    {
        // Lines are significant — assertions below pin each violation to a line number.
        WriteFile("lib.go",
            "package lib\n" +                                                   // 1
            "\n" +                                                              // 2
            "import (\n" +                                                      // 3
            "\t\"fmt\"\n" +                                                     // 4
            "\t\"os\"\n" +                                                      // 5
            "\t\"time\"\n" +                                                    // 6
            ")\n" +                                                             // 7
            "\n" +                                                              // 8
            "// HttpClient does things.\n" +                                    // 9
            "type HttpClient struct {\n" +                                      // 10: initialism-casing
            "\tName string\n" +                                                 // 11
            "}\n" +                                                             // 12
            "\n" +                                                              // 13
            "type user_record struct {\n" +                                     // 14: underscore-naming
            "\tV int\n" +                                                       // 15
            "}\n" +                                                             // 16
            "\n" +                                                              // 17
            "type Widget struct {\n" +                                          // 18: undocumented-types
            "\tName string\n" +                                                 // 19
            "}\n" +                                                             // 20
            "\n" +                                                              // 21
            "func Run(a int, b int, c int, d int, e int, f int, g int, h int) {\n" + // 22: too-many-arguments
            "\tfmt.Println(\"hi\")\n" +                                         // 23: console-output
            "\tos.Exit(1)\n" +                                                  // 24: os-exit
            "\ttime.Sleep(1)\n" +                                               // 25: time-sleep
            "\tpanic(\"boom\")\n" +                                             // 26: panic-calls
            "\tvar x interface{} = 1\n" +                                       // 27: use-any
            "\t_ = x\n" +                                                       // 28
            "}\n");                                                             // 29

        var result = RunCop($"run go-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("lib.go(10): info: HttpClient"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(14): warning: user_record"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(18): warning: Exported type Widget is missing a doc comment"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(22): warning: Run has more than 7 parameters"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(23): warning: Avoid fmt.Println"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(24): warning: Avoid os.Exit()"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(25): info: time.Sleep()"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(26): warning: Avoid panic()"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(27): info: Use any instead of interface{}"), Describe(result));
        Assert.That(output, Does.Contain("exported functions without doc comments"), Describe(result));
    }

    [Test]
    public void Go_LargeFunction_FiresAboveThreshold()
    {
        var body = string.Concat(Enumerable.Repeat("\twork()\n", 51));
        WriteFile("big.go", "package lib\n\n// Big does work.\nfunc Big() {\n" + body + "}\n");
        WriteFile("small.go", "package lib\n\n// Small does work.\nfunc Small() {\n\twork()\n}\n");

        var result = RunCop($"run go-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("big.go(4): info: Big is large"), Describe(result));
        Assert.That(output, Does.Not.Contain("Small is large"), Describe(result));
    }

    [Test]
    public void Go_InstanceExclusion_CopIgnore_SuppressesOnlyAnnotatedLine()
    {
        WriteFile("lib.go",
            "package lib\n" +
            "\n" +
            "// Run runs.\n" +
            "func Run() {\n" +
            "\t// cop-ignore: panic-calls\n" +
            "\tpanic(\"a\")\n" +  // 6: ignored
            "\tpanic(\"b\")\n" +  // 7: flagged
            "}\n");

        var result = RunCop($"run go-checks -t \"{_workDir}\"");
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(CountOccurrences(output, "Avoid panic()"), Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Contain("lib.go(7): warning: Avoid panic()"), Describe(result));
        Assert.That(output, Does.Not.Contain("lib.go(6): warning: Avoid panic()"), Describe(result));
    }

    [Test]
    public void Go_RuleExclusion_SubtractOperator_DropsOnlyNamedRule()
    {
        WriteFile("lib.go",
            "package lib\n" +
            "\n" +
            "// Run runs.\n" +
            "func Run() {\n" +
            "\tfmt.Println(\"a\")\n" +  // 5: console-output (kept)
            "\tpanic(\"b\")\n" +        // 6: panic-calls (excluded)
            "}\n");
        WriteFile("checks.cop",
            "import go-checks\n" +
            "import code\n" +
            "command MAIN = CHECK(go-checks - panic-calls)\n");

        var result = RunCop($"\"{Path.Combine(_workDir, "checks.cop")}\" -t \"{_workDir}\"", _workDir);
        var output = Normalize(result.Stdout);

        Assert.That(result.ExitCode, Is.EqualTo(1), Describe(result));
        Assert.That(output, Does.Not.Contain("Avoid panic()"), Describe(result));
        Assert.That(output, Does.Contain("lib.go(5): warning: Avoid fmt.Println"), Describe(result));
    }

    private void WriteRust(string name, string content) => WriteFile(name, content);
    private void WriteFile(string name, string content) =>
        File.WriteAllText(Path.Combine(_workDir, name), content);

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }

    private static string RepoRoot => FindRepoRoot();

    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCop(string args, string? workingDir = null)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 -Runtimes win-x64 first.");

        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            Arguments = args,
            WorkingDirectory = workingDir ?? RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            Assert.Fail($"cop.exe timed out. Args: {args}");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string Normalize(string text) =>
        Regex.Replace(text.Replace("\r\n", "\n"), @"\u001b\[[0-9;]*m", string.Empty).Trim();

    private static string Describe((int ExitCode, string Stdout, string Stderr) result) =>
        $"ExitCode: {result.ExitCode}\nSTDOUT:\n{result.Stdout}\nSTDERR:\n{result.Stderr}";
}
