// Executes Cop test declarations and reports assertion results.

using System.CommandLine;
using Cop.Lang;
using Cop.Providers;

namespace Cop.Cli.Commands;

/// <summary>Runs Cop test declarations and reports their assertion outcomes.</summary>
public static class TestCommand
{
    /// <summary>
    /// Creates the test command with source selection and diagnostic options.
    /// </summary>
    public static Command Create()
    {
        Argument<string> fileArg = new Argument<string>("file")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = ".cop file or directory containing test files"
        };
        Option<bool> diagOption = new Option<bool>("-d") { Description = "Print diagnostic timing to stderr" };
        Command command = new Command("test", "Run ASSERT commands in .cop files and report results")
        {
            fileArg,
            diagOption
        };
        command.SetAction(parseResult => Execute(
            parseResult.GetValue(fileArg),
            parseResult.GetValue(diagOption)));
        return command;
    }

    /// <summary>
    /// Executes tests in the selected source and returns their overall status.
    /// </summary>
    public static int Execute(string? fileOrDir, bool diag = false)
    {
        string scriptsDir;
        string rootPath;

        if (fileOrDir != null && fileOrDir.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            FileInfo spec = new FileInfo(fileOrDir);
            if (!spec.Exists) { Console.Error.WriteLine($"Error: File '{spec.FullName}' not found"); return 2; }
            scriptsDir = spec.DirectoryName ?? Directory.GetCurrentDirectory();
            rootPath = scriptsDir;
        }
        else
        {
            scriptsDir = fileOrDir ?? Directory.GetCurrentDirectory();
            rootPath = scriptsDir;
        }

        Action<string>? diagLog = diag ? msg => Console.Error.WriteLine(RunCommand.ColorDiagLine(msg)) : null;
        EngineResult result = Engine.Run(scriptsDir, rootPath, new EngineRunOptions
        {
            DiagnosticLog = diagLog,
            IsAssertMode = true,
        });

        foreach (string error in result.ParseErrors)
        {
            Console.Error.WriteLine(error);
        }

        if (result.HasFatalErrors)
        {
            foreach (string error in result.Errors)
            {
                Console.Error.WriteLine(error);
            }

            return 2;
        }

        List<AssertResult> asserts = result.Asserts ?? [];
        if (asserts.Count == 0)
        {
            Console.Error.WriteLine("No ASSERT commands found.");
            return 2;
        }

        int passed = 0, failed = 0;
        foreach (AssertResult assert in asserts)
        {
            if (assert.Passed)
            {
                Console.WriteLine($"  \u2713 {assert.Name}");
                passed++;
            }
            else
            {
                string detail = string.Equals(assert.Name, assert.Message, StringComparison.OrdinalIgnoreCase)
                    ? ""
                    : $": {assert.Message}";
                string countInfo = assert.Count > 0 ? $" (found {assert.Count} items)" : " (empty)";
                Console.WriteLine($"  \u2717 {assert.Name}{detail}{countInfo}");
                failed++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"  {asserts.Count} tests, {passed} passed, {failed} failed");

        return failed > 0 ? 1 : 0;
    }
}
