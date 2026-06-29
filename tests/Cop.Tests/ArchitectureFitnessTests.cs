using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Architecture "fitness function" tests for the language core. They encode invariants the compiler
/// cleanup established so they cannot silently regress: the interpreter core (evaluator, binder,
/// type-checker, parser glue) must carry zero domain knowledge, and the implicit iteration variable
/// must be referenced through a single constant rather than scattered string literals.
/// </summary>
[TestFixture]
public class ArchitectureFitnessTests
{
    private static string RepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static string InterpreterDir => Path.Combine(RepoRoot(), "cop", "shared", "interpreter");

    [Test]
    public void InterpreterCore_HasNoHardcodedDomainTypeNames()
    {
        // Domain concepts belong to .cop packages (Violation/Codebase/...) and, for exit-code
        // semantics, the runtime orchestration layer — never the evaluator/binder/type-checker. This
        // guards the Phase 0 removals (the isPublic trace and the "Codebase" special-case) and stops
        // new domain special-cases from creeping back into the core and breaking generality.
        string[] forbidden = ["Codebase", "Violation", "Filesystem", "MarkdownContent"];

        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(InterpreterDir, "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                foreach (var name in forbidden)
                    if (lines[i].Contains($"\"{name}\""))
                        offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.That(offenders, Is.Empty,
            "domain-type names are hardcoded in the language interpreter core:\n" + string.Join("\n", offenders));
    }

    [Test]
    public void ImplicitItemVariable_IsReferencedThroughTheConstant_NotScatteredLiterals()
    {
        // The implicit `item` binding must go through Evaluator.ImplicitItemVariable, not a literal.
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(InterpreterDir, "*.cs"))
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
                if (lines[i].Contains(".Define(\"item\"") || lines[i].Contains(".TryLookup(\"item\""))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.That(offenders, Is.Empty,
            "use Evaluator.ImplicitItemVariable instead of the literal \"item\":\n" + string.Join("\n", offenders));
    }
}
