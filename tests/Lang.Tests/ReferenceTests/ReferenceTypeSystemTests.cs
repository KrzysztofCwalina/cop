using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

/// <summary>
/// Reference test runner for the Cop type system.
/// Each test is a .cop file in Programs/ with @expect directives.
/// Tests use Engine.Run() — the same path as real user code — no pre-registration.
///
/// Directives:
///   # @expect output              — at least one output produced
///   # @expect no-output           — zero outputs produced
///   # @expect output-contains: X  — some output message contains X
///   # @expect no-output-contains: X — no output message contains X
///   # @expect parse-error         — the program fails to parse
///   # @expect error               — engine reports fatal errors
///   # @expect error-contains: X   — some fatal error contains X
/// </summary>
[TestFixture]
public class ReferenceTypeSystemTests
{
    private static string ProgramsDir =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "ReferenceTests", "Programs");

    private static string SamplesDir =>
        Path.Combine(TestContext.CurrentContext.TestDirectory, "Samples");

    private static string PackagesDir
    {
        get
        {
            // Walk up from test output dir to find the repo root's packages/ folder
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir != null)
            {
                var candidate = Path.Combine(dir, "packages");
                if (Directory.Exists(candidate) && Directory.Exists(Path.Combine(candidate, "code")))
                    return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            // Fallback: relative from repo structure
            return Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "..", "packages"));
        }
    }

    private static IEnumerable<string> GetTestPrograms()
    {
        var dir = ProgramsDir;
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.GetFiles(dir, "*.cop").OrderBy(f => f))
            yield return Path.GetFileName(file);
    }

    [TestCaseSource(nameof(GetTestPrograms))]
    public void RunProgram(string fileName)
    {
        var filePath = Path.Combine(ProgramsDir, fileName);
        var source = File.ReadAllText(filePath);
        var directives = ParseDirectives(source);

        // Handle parse-error: copy to temp dir and run isolated
        if (directives.Any(d => d.Kind == "parse-error"))
        {
            var errDir = Path.Combine(Path.GetTempPath(), "cop-ref-tests", Path.GetFileNameWithoutExtension(fileName));
            if (Directory.Exists(errDir)) Directory.Delete(errDir, true);
            Directory.CreateDirectory(errDir);
            try
            {
                File.Copy(filePath, Path.Combine(errDir, fileName));
                var parseResult = Engine.Run(
                    errDir, SamplesDir,
                    additionalFeedPaths: [PackagesDir]);
                Assert.That(parseResult.HasParseErrors || parseResult.HasFatalErrors, Is.True,
                    $"{fileName}: Expected a parse error but got none. Outputs: {string.Join(", ", parseResult.Outputs.Select(o => o.Message))}");
            }
            finally
            {
                try { Directory.Delete(errDir, true); } catch { }
            }
            return;
        }

        // Copy the single .cop file to a temp directory (Engine discovers all .cop in dir)
        var tempDir = Path.Combine(Path.GetTempPath(), "cop-ref-tests", Path.GetFileNameWithoutExtension(fileName));
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);
        try
        {
            File.Copy(filePath, Path.Combine(tempDir, fileName));

            var diagMessages = new List<string>();
            var result = Engine.Run(
                tempDir, SamplesDir,
                diagLog: msg => diagMessages.Add(msg),
                additionalFeedPaths: [PackagesDir]);

            var diagInfo = string.Join("; ", diagMessages);
            var warningInfo = result.Warnings is not null ? string.Join("; ", result.Warnings) : "";

            // Validate directives
            foreach (var directive in directives)
            {
                switch (directive.Kind)
                {
                    case "output":
                        Assert.That(result.Outputs, Is.Not.Empty,
                            $"{fileName}: Expected output but got none. Errors: {FormatErrors(result)}. Warnings: {warningInfo}. Diag: {diagInfo}");
                        break;

                    case "no-output":
                        Assert.That(result.Outputs, Is.Empty,
                            $"{fileName}: Expected no output but got: {FormatOutputs(result)}");
                        break;

                    case "output-contains":
                        Assert.That(result.Outputs.Any(o => o.Message.Contains(directive.Arg!)), Is.True,
                            $"{fileName}: Expected output containing '{directive.Arg}' but got: {FormatOutputs(result)}. Errors: {FormatErrors(result)}. Warnings: {warningInfo}. Diag: {diagInfo}");
                        break;

                    case "no-output-contains":
                        Assert.That(result.Outputs.All(o => !o.Message.Contains(directive.Arg!)), Is.True,
                            $"{fileName}: Expected no output containing '{directive.Arg}' but found one in: {FormatOutputs(result)}");
                        break;

                    case "error":
                        Assert.That(result.HasFatalErrors || result.HasParseErrors, Is.True,
                            $"{fileName}: Expected an error but got none. Outputs: {FormatOutputs(result)}");
                        break;

                    case "error-contains":
                        var allErrors = result.Errors.Concat(result.ParseErrors).ToList();
                        Assert.That(allErrors.Any(e => e.Contains(directive.Arg!)), Is.True,
                            $"{fileName}: Expected error containing '{directive.Arg}' but errors were: {string.Join("; ", allErrors)}");
                        break;
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string FormatOutputs(EngineResult result) =>
        result.Outputs.Count == 0 ? "(none)" : string.Join(", ", result.Outputs.Select(o => o.Message));

    private static string FormatErrors(EngineResult result)
    {
        var all = result.Errors.Concat(result.ParseErrors).ToList();
        return all.Count == 0 ? "(none)" : string.Join("; ", all);
    }

    private static List<Directive> ParseDirectives(string source)
    {
        var directives = new List<Directive>();
        foreach (var line in source.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("# @expect")) continue;

            var rest = trimmed["# @expect".Length..].Trim();

            if (rest == "output")
                directives.Add(new("output", null));
            else if (rest == "no-output")
                directives.Add(new("no-output", null));
            else if (rest == "parse-error")
                directives.Add(new("parse-error", null));
            else if (rest == "error")
                directives.Add(new("error", null));
            else if (rest.StartsWith("output-contains:"))
                directives.Add(new("output-contains", rest["output-contains:".Length..].Trim()));
            else if (rest.StartsWith("no-output-contains:"))
                directives.Add(new("no-output-contains", rest["no-output-contains:".Length..].Trim()));
            else if (rest.StartsWith("error-contains:"))
                directives.Add(new("error-contains", rest["error-contains:".Length..].Trim()));
        }
        return directives;
    }

    private record Directive(string Kind, string? Arg);
}
