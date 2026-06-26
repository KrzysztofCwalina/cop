using System.Diagnostics;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// End-to-end NEGATIVE tests: what a user SEES when a cop program is malformed or fails at runtime.
/// These guard the quality of error reporting — every error must surface (never silently produce an
/// empty result), carry a real line number (never "line 0"), name the offending symbol, and use a
/// user-facing type name (e.g. "int", not the internal "CopInt").
///
/// Engine-level errors run in-process via <see cref="Engine.Run"/>; a few CLI-level cases run the
/// published cop.exe to assert exit codes (0 clean, 1 usage/IO, 2 fatal/parse).
/// </summary>
[TestFixture]
public class ErrorReportingTests
{
    private static string RepoRoot => FindRepoRoot();
    private static string PackagesDir => Path.Combine(RepoRoot, "packages");
    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private sealed record RunResult(
        bool HasParseErrors, IReadOnlyList<string> ParseErrors,
        bool HasFatalErrors, IReadOnlyList<string> Errors,
        IReadOnlyList<string> Outputs)
    {
        public string AllErrors => string.Join(" | ", ParseErrors.Concat(Errors));
    }

    private static RunResult Run(string program)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "error-reporting", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(scripts, "program.cop"), program);
            var r = Engine.Run(scripts, target, additionalFeedPaths: [PackagesDir]);
            return new RunResult(
                r.HasParseErrors, r.ParseErrors.ToArray(),
                r.HasFatalErrors, r.Errors.ToArray(),
                r.Outputs.Select(o => o.Message).ToArray());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(string verb, string program)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");

        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "error-reporting-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "program.cop");
        try
        {
            File.WriteAllText(script, program);
            var psi = new ProcessStartInfo
            {
                FileName = CopExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RepoRoot,
            };
            if (verb == "verify") psi.ArgumentList.Add("verify");
            psi.ArgumentList.Add(script);
            if (verb == "run") { psi.ArgumentList.Add("-t"); psi.ArgumentList.Add(root); }

            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(60_000)) { try { p.Kill(entireProcessTree: true); } catch { } Assert.Fail("cop.exe timed out."); }
            return (p.ExitCode, outTask.GetAwaiter().GetResult().Trim(), errTask.GetAwaiter().GetResult().Trim());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---- Parse / syntax errors ------------------------------------------------------------------

    [Test]
    public void ParseError_UnterminatedString_IsReportedWithLine()
    {
        var r = Run("command main = print('hello");
        Assert.That(r.HasParseErrors, Is.True);
        Assert.That(r.AllErrors, Does.Contain("Unterminated string"));
        Assert.That(r.AllErrors, Does.Contain("(1)"), "the error must cite line 1");
    }

    [Test]
    public void ParseError_UnexpectedToken_IsReportedWithLine()
    {
        var r = Run("command main = print(1 +)");
        Assert.That(r.HasParseErrors, Is.True);
        Assert.That(r.AllErrors, Does.Contain("Unexpected token"));
        Assert.That(r.AllErrors, Does.Contain("(1)"));
    }

    [Test]
    public void ParseError_CommandMissingEquals_IsReported()
    {
        // `command main print('x')` — the '=' is missing; previously this silently "verified".
        var r = Run("command main print('x')");
        Assert.That(r.HasParseErrors, Is.True, "a command without '=' must be a parse error");
        Assert.That(r.AllErrors, Does.Contain("'='"));
        Assert.That(r.AllErrors, Does.Contain("(1)"));
    }

    [Test]
    public void ParseError_EmptyFilter_IsReported()
    {
        // `[1 2]:` with no predicate leaves a dangling ':' that previously verified silently.
        var r = Run("command main = foreach [1 2]: => '{item}'");
        Assert.That(r.HasParseErrors, Is.True, "an empty filter must be a parse error");
        Assert.That(r.AllErrors, Does.Contain("Unexpected token ':'"));
    }

    [Test]
    public void ParseError_StrayClosingBrace_IsReported()
    {
        // A stray '}' previously verified silently.
        var r = Run("command main = print('x') }");
        Assert.That(r.HasParseErrors, Is.True, "a stray '}' must be a parse error");
        Assert.That(r.AllErrors, Does.Contain("Unexpected token '}'"));
    }

    // ---- Binding / name errors ------------------------------------------------------------------

    [Test]
    public void BindError_UndefinedVariable_IsFatalAndNamesTheSymbol()
    {
        var r = Run("command main = print(undefinedThing)");
        Assert.That(r.HasFatalErrors, Is.True);
        Assert.That(r.Outputs, Is.Empty);
        Assert.That(r.AllErrors, Does.Contain("Undefined variable 'undefinedThing'"));
        Assert.That(r.AllErrors, Does.Contain("(1): error:"), "runtime errors should use the file(line): error: form");
        Assert.That(r.AllErrors, Does.Contain("print(undefinedThing)"), "the offending source line should be shown");
    }

    [Test]
    public void UnknownPredicate_IsFatal_NotSilentlyEmpty()
    {
        // A typo'd predicate must FAIL LOUDLY, not silently filter everything out (false-green).
        var r = Run("command main = foreach [1 2 3]:notARealPredicate => '{item}'");
        Assert.That(r.HasFatalErrors, Is.True,
            "an unknown predicate must be a fatal error, not a silent empty result");
        Assert.That(r.Outputs, Is.Empty);
        Assert.That(r.AllErrors, Does.Contain("Unknown predicate 'notARealPredicate'"));
        Assert.That(r.AllErrors, Does.Contain("(1)"));
    }

    [Test]
    public void UnknownNegatedPredicate_IsFatal_NotSilentlyPassThrough()
    {
        // `:!typo` must also error rather than silently treating every item as "not false" → keep-all.
        var r = Run("command main = foreach [1 2 3]:!alsoNotReal => '{item}'");
        Assert.That(r.HasFatalErrors, Is.True);
        Assert.That(r.AllErrors, Does.Contain("Unknown predicate 'alsoNotReal'"));
    }

    [Test]
    public void KnownPredicate_StillFiltersCorrectly_NoFalsePositiveError()
    {
        // Positive control: the unknown-predicate guard must NOT break a legitimate predicate.
        var r = Run("""
            predicate isBig(int) => item > 1
            command main = foreach [1 2 3]:isBig => '{item}'
            """);
        Assert.That(r.HasFatalErrors, Is.False, r.AllErrors);
        Assert.That(r.Outputs, Is.EqualTo(new[] { "2", "3" }));
    }

    // ---- Runtime errors -------------------------------------------------------------------------

    [Test]
    public void Runtime_CallingNonCallable_UsesFriendlyTypeName_NotInternalClassName()
    {
        var r = Run("""
            let x = 5
            command main = print(x(1))
            """);
        Assert.That(r.HasFatalErrors, Is.True);
        Assert.That(r.AllErrors, Does.Contain("Value of type int is not callable"));
        Assert.That(r.AllErrors, Does.Not.Contain("CopInt"),
            "internal CLR type names must not leak into user-facing errors");
        Assert.That(r.AllErrors, Does.Contain("(2)"));
    }

    [Test]
    public void Runtime_UnknownStringMember_IsFatalWithName()
    {
        var r = Run("command main = print('text'.NotAMember)");
        Assert.That(r.HasFatalErrors, Is.True);
        Assert.That(r.AllErrors, Does.Contain("Unknown string member 'NotAMember'"));
    }

    [Test]
    public void Runtime_Error_IsRenderedLikeAParseError_WithFileLineAndSourceSnippet()
    {
        // A runtime failure should read as nicely as a syntax error: "file(line): error: message"
        // followed by the offending source line.
        var r = Run("""
            command main =
                print(missingThing)
            """);
        Assert.That(r.HasFatalErrors, Is.True);
        var err = r.AllErrors;
        Assert.That(err, Does.Contain("(2): error:"), "expected file(line): error: form on line 2");
        Assert.That(err, Does.Contain("Undefined variable 'missingThing'"));
        Assert.That(err, Does.Contain("2 | "), "expected a numbered source-line snippet");
        Assert.That(err, Does.Contain("print(missingThing)"), "the snippet must show the source line");
    }

    [Test]
    public void Runtime_ReadMissingFile_ReportsRealLine_NotLineZero()
    {
        var r = Run("command main = print(read('definitely-missing-file.txt'))");
        Assert.That(r.HasFatalErrors, Is.True);
        Assert.That(r.AllErrors, Does.Contain("read("));
        Assert.That(r.AllErrors, Does.Contain("(1)"));
        Assert.That(r.AllErrors, Does.Not.Contain("line 0"),
            "FFI runtime errors must carry the real call-site line, not line 0");
    }

    [Test]
    public void Runtime_Fail_ReportsMessageAndRealLine_NotLineZero()
    {
        var r = Run("""
            command main =
                fail('something broke')
            """);
        Assert.That(r.HasFatalErrors, Is.True);
        Assert.That(r.AllErrors, Does.Contain("something broke"));
        Assert.That(r.AllErrors, Does.Contain("(2)"));
        Assert.That(r.AllErrors, Does.Not.Contain("line 0"));
    }

    // ---- CLI-level exit codes -------------------------------------------------------------------

    [Test]
    public void Cli_MissingFile_Exits1_WithNotFoundMessage()
    {
        if (!File.Exists(CopExe))
            Assert.Ignore("Published cop.exe not found");

        var psi = new ProcessStartInfo
        {
            FileName = CopExe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = RepoRoot,
        };
        psi.ArgumentList.Add("this-file-does-not-exist.cop");
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        _ = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30_000);

        Assert.That(p.ExitCode, Is.EqualTo(1));
        Assert.That(err, Does.Contain("not found"));
    }

    [Test]
    public void Cli_ParseError_Exits2()
    {
        var (exit, _, stderr) = RunCli("run", "command main = print(1 +)");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(stderr, Does.Contain("Unexpected token"));
    }

    [Test]
    public void Cli_RuntimeFatal_Exits2()
    {
        var (exit, _, stderr) = RunCli("run", "command main = fail('boom')");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(stderr, Does.Contain("boom"));
    }

    [Test]
    public void Cli_UnknownPredicate_Exits2_NotCleanZero()
    {
        // The silent-failure bug previously exited 0 with no output; it must now be a fatal (exit 2).
        var (exit, stdout, stderr) = RunCli("run", "command main = foreach [1 2]:typoPredicate => '{item}'");
        Assert.That(exit, Is.EqualTo(2));
        Assert.That(stdout, Is.Empty);
        Assert.That(stderr, Does.Contain("Unknown predicate 'typoPredicate'"));
    }

    // ---- Verify: loads siblings and flags undefined references (false-green fix) -----------------

    [Test]
    public void Cli_Verify_UndefinedReference_Exits1_NotSilentlyClean()
    {
        // The false-green: `cop verify` previously reported success for a program that fatals at
        // run with "Undefined variable". It must now flag the undefined reference (exit 1).
        var (exit, stdout, stderr) = RunCli("verify", "command main = print(does-not-exist)");
        Assert.That(exit, Is.EqualTo(1), $"stdout={stdout} stderr={stderr}");
        Assert.That(stderr + stdout, Does.Contain("Undefined variable 'does-not-exist'"));
    }

    [Test]
    public void Cli_Verify_KnownProgram_StillVerifiesClean_NoFalsePositive()
    {
        // Positive control: a valid program (filter predicate + implicit item) must still pass.
        var (exit, stdout, stderr) = RunCli("verify",
            "predicate isBig(int) => item > 1\ncommand main = foreach [1 2 3]:isBig => '{item}'");
        Assert.That(exit, Is.EqualTo(0), $"stdout={stdout} stderr={stderr}");
    }

    [Test]
    public void Cli_Verify_SingleFile_LoadsSiblingFiles()
    {
        // `cop verify main.cop` must verify the whole program (every .cop file in its directory),
        // matching `cop run`, so a reference in main.cop to a let declared in a sibling resolves
        // and the reported file count covers the whole program.
        if (!File.Exists(CopExe)) Assert.Ignore("Published cop.exe not found");
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "verify-siblings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "lib.cop"), "let helper-list = [1 2 3]");
            File.WriteAllText(Path.Combine(root, "main.cop"), "command main = print(helper-list)");

            var psi = new ProcessStartInfo
            {
                FileName = CopExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RepoRoot,
            };
            psi.ArgumentList.Add("verify");
            psi.ArgumentList.Add(Path.Combine(root, "main.cop"));
            using var p = Process.Start(psi)!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(30_000)) { try { p.Kill(entireProcessTree: true); } catch { } Assert.Fail("cop.exe timed out."); }
            var outStr = outTask.GetAwaiter().GetResult();
            var errStr = errTask.GetAwaiter().GetResult();

            Assert.That(p.ExitCode, Is.EqualTo(0),
                $"verify of main.cop should load sibling lib.cop and pass. stdout={outStr} stderr={errStr}");
            Assert.That(outStr, Does.Contain("2 file(s)"),
                "single-file verify should report every sibling file in the program");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---- Binder: command bodies are validated (arity) -------------------------------------------

    [Test]
    public void Binder_TooManyArgumentsInCommandBody_IsFatal()
    {
        // Command bodies were not Pass-3 validated, so a too-many-args call silently passed.
        var r = Run("""
            function f(a) => a
            command main = print(f(1, 2, 3))
            """);
        Assert.That(r.HasFatalErrors, Is.True, "an arity error in a command body must be reported");
        Assert.That(r.AllErrors, Does.Contain("expects 1 argument(s) but got 3"));
    }

    // ---- Loader: a broken imported package is fatal, not silently ignored -----------------------

    [Test]
    public void Loader_BrokenImportedPackage_IsFatalWithParseError()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "loader-error", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts");
        var target = Path.Combine(root, "target");
        var feed = Path.Combine(root, "feed");
        var pkgSrc = Path.Combine(feed, "brokenpkg", "src");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(pkgSrc);
        try
        {
            File.WriteAllText(Path.Combine(feed, "brokenpkg", "cop.json"),
                """{"name":"brokenpkg","version":"1.0.0","title":"Broken","description":"x","authors":"t"}""");
            File.WriteAllText(Path.Combine(pkgSrc, "broken.cop"), "export let x = (1 +");
            // The program imports the broken package but does NOT use its exports — it must still fail.
            File.WriteAllText(Path.Combine(scripts, "program.cop"), "import brokenpkg\ncommand main = print('ok')");

            var r = Engine.Run(scripts, target, additionalFeedPaths: [feed]);

            Assert.That(r.HasFatalErrors, Is.True, "importing a package that fails to parse must be fatal");
            Assert.That(string.Join(" | ", r.Errors), Does.Contain("Unexpected token"));
            Assert.That(r.Outputs, Is.Empty, "the program must not run when a dependency is broken");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---- Providers: malformed source files surface, never silently hidden -----------------------

    [Test]
    public void Provider_MalformedCSharp_SurfacesParseErrorsAsWarnings_NotSilentlyHidden()
    {
        // A provider that "hides errors" would drop the broken file (or emit a partial model) and
        // report nothing. The user must SEE the failure: a prominent warning naming the file and the
        // Roslyn diagnostic id. It is a WARNING (not fatal) because a repo legitimately contains .cs
        // files that aren't complete compilable units — but the failure must never be invisible.
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "provider-error", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, "Good.cs"),
                "namespace N { public class Alpha { public void M() { } } }");
            File.WriteAllText(Path.Combine(target, "Broken.cs"),
                "namespace N { public class Beta { public void X( { } }");

            File.WriteAllText(Path.Combine(scripts, "program.cop"), """
                import code
                import csharp
                let cb = codebase(csharp.parse())
                command main = foreach cb.Types => '{item.Name}'
                """);

            var r = Engine.Run(scripts, target, additionalFeedPaths: [PackagesDir]);

            Assert.That(r.HasFatalErrors, Is.False,
                "a malformed source file must surface but not abort the whole analysis: "
                + string.Join(" | ", r.Errors));

            var warnings = r.Warnings ?? new List<string>();
            var allWarnings = string.Join(" | ", warnings);
            Assert.That(warnings, Is.Not.Empty,
                "the malformed C# file must surface as a warning, not be silently hidden");
            Assert.That(allWarnings, Does.Contain("parse error"),
                "a header naming the parse failure must be shown");
            Assert.That(allWarnings, Does.Contain("Broken.cs"),
                "the warning must name the offending file");
            Assert.That(allWarnings, Does.Contain("CS"),
                "the Roslyn diagnostic id (CS####) must be surfaced");

            // The valid type must STILL be analyzed — a partial model is acceptable; hiding is not.
            Assert.That(r.Outputs.Select(o => o.Message), Does.Contain("Alpha"),
                "the valid type must still be analyzed even though a sibling file failed to parse");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Test]
    public void Provider_ValidCSharp_ProducesNoSpuriousParseWarnings()
    {
        // Positive control: the error-surfacing guard must not cry wolf on well-formed code.
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "provider-ok", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, "Good.cs"),
                "namespace N { public class Alpha { public void M() { } } }");

            File.WriteAllText(Path.Combine(scripts, "program.cop"), """
                import code
                import csharp
                let cb = codebase(csharp.parse())
                command main = foreach cb.Types => '{item.Name}'
                """);

            var r = Engine.Run(scripts, target, additionalFeedPaths: [PackagesDir]);

            Assert.That(r.HasFatalErrors, Is.False, string.Join(" | ", r.Errors));
            var warnings = (r.Warnings ?? new List<string>()).Where(w => w.Contains("parse error")).ToArray();
            Assert.That(warnings, Is.Empty,
                "valid C# must NOT produce spurious parse-error warnings: " + string.Join(" | ", warnings));
            Assert.That(r.Outputs.Select(o => o.Message), Does.Contain("Alpha"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
