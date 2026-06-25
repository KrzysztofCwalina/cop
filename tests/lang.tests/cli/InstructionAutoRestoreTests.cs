using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests.Lang.Cli;

/// <summary>
/// End-to-end test that the common auto-restore path (<c>cop &lt;file&gt;.cop</c>) places an
/// imported package's instructions into the repository's
/// <c>.github/instructions/{name}.instructions.md</c> with the correct applyTo front-matter, and
/// does so idempotently.
/// </summary>
[TestFixture]
[NonParallelizable]
public class InstructionAutoRestoreTests
{
    private static string NewTempRepo()
    {
        var dir = Path.Combine(TestContext.CurrentContext.WorkDirectory, "instr-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        // A .git marker makes this temp dir a self-contained repo, guarding against any future
        // regression where placement might walk up into the real cop repository and pollute it.
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        return dir;
    }

    private static void WriteLocalInstructionPackage(string repo, string name, string applyTo, string instructionBody)
    {
        var pkg = Path.Combine(repo, "packages", name);
        Directory.CreateDirectory(Path.Combine(pkg, "src"));
        Directory.CreateDirectory(Path.Combine(pkg, "instructions"));
        File.WriteAllText(Path.Combine(pkg, "cop.json"),
            $"{{ \"name\": \"{name}\", \"version\": \"1.0.0\", \"title\": \"Demo\", \"description\": \"d\", \"authors\": \"a\", \"applyTo\": \"{applyTo}\" }}");
        File.WriteAllText(Path.Combine(pkg, "src", $"{name}.cop"), "let demoValue = 42\n");
        File.WriteAllText(Path.Combine(pkg, "instructions", "guide.md"), instructionBody);
    }

    private static int RunInRepo(string repo, string copFileName)
    {
        var previousCwd = Directory.GetCurrentDirectory();
        var previousOut = Console.Out;
        var previousErr = Console.Error;
        try
        {
            Directory.SetCurrentDirectory(repo);
            using var so = new StringWriter();
            using var se = new StringWriter();
            Console.SetOut(so);
            Console.SetError(se);
            return RunCommand.Execute(Path.Combine(repo, copFileName), target: repo);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousErr);
            Directory.SetCurrentDirectory(previousCwd);
        }
    }

    [Test]
    public void AutoRestore_PlacesImportedPackageInstructions_Idempotently()
    {
        var repo = NewTempRepo();
        try
        {
            WriteLocalInstructionPackage(repo, "demo-instr-checks", "**/*.cs", "# Demo Guide\n\nUse PascalCase.\n");
            File.WriteAllText(Path.Combine(repo, "program.cop"),
                "import demo-instr-checks\ncommand MAIN = foreach [1 2] => 'n={item}'\n");

            var exit = RunInRepo(repo, "program.cop");
            Assert.That(exit, Is.EqualTo(0), "program should run cleanly");

            var placed = Path.Combine(repo, ".github", "instructions", "demo-instr-checks.instructions.md");
            Assert.That(File.Exists(placed), Is.True, "auto-restore should place package instructions");
            Assert.That(File.ReadAllText(placed).Replace("\r\n", "\n"),
                Is.EqualTo("---\napplyTo: '**/*.cs'\n---\n\n# Demo Guide\n\nUse PascalCase.\n"));

            // A second run must not change the placed file (no working-tree churn).
            var before = File.ReadAllBytes(placed);
            var exit2 = RunInRepo(repo, "program.cop");
            Assert.That(exit2, Is.EqualTo(0));
            Assert.That(File.ReadAllBytes(placed), Is.EqualTo(before), "second run must not rewrite the file");
        }
        finally { TryDelete(repo); }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }
}
