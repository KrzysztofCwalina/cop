using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class NameResolutionTests
{
    private static ScriptInterpreter CreateInterpreter()
    {
        var registry = new TypeRegistry();
        registry.RegisterProgramType();
        return new ScriptInterpreter(registry);
    }

    // ── Duplicate let binding detection ──

    [Test]
    public void DuplicateLocalLet_ProducesWarning()
    {
        var file1 = ScriptParser.Parse("let x = 42", "a.cop");
        var file2 = ScriptParser.Parse("let x = 99", "b.cop");

        var interpreter = CreateInterpreter();
        var result = interpreter.Run([file1, file2], []);

        Assert.That(result.Warnings.Any(w => w.Contains("Duplicate let binding 'x'")),
            Is.True, $"Expected duplicate let warning but got: {string.Join("; ", result.Warnings)}");
    }

    [Test]
    public void DuplicateLocalPredicate_SameType_ProducesWarning()
    {
        var file1 = ScriptParser.Parse("predicate isBig(Type) => Type.Name:sw('Big')", "a.cop");
        var file2 = ScriptParser.Parse("predicate isBig(Type) => Type.Name:sw('Huge')", "b.cop");

        var interpreter = CreateInterpreter();
        var result = interpreter.Run([file1, file2], []);

        Assert.That(result.Warnings.Any(w => w.Contains("Duplicate predicate 'isBig(Type)'")),
            Is.True, $"Expected duplicate predicate warning but got: {string.Join("; ", result.Warnings)}");
    }

    [Test]
    public void DuplicateLocalFunction_SameType_ProducesWarning()
    {
        var file1 = ScriptParser.Parse("function fmt(Type) => '{Type.Name}!'", "a.cop");
        var file2 = ScriptParser.Parse("function fmt(Type) => '{Type.Name}?'", "b.cop");

        var interpreter = CreateInterpreter();
        var result = interpreter.Run([file1, file2], []);

        Assert.That(result.Warnings.Any(w => w.Contains("Duplicate function 'fmt(Type)'")),
            Is.True, $"Expected duplicate function warning but got: {string.Join("; ", result.Warnings)}");
    }

    // ── Different input types are valid overloads (no warning) ──

    [Test]
    public void SameName_DifferentType_NoWarning()
    {
        var file1 = ScriptParser.Parse("predicate isBig(Type) => Type.Name:sw('Big')", "a.cop");
        var file2 = ScriptParser.Parse("predicate isBig(Method) => Method.Name:sw('Big')", "b.cop");

        var interpreter = CreateInterpreter();
        var result = interpreter.Run([file1, file2], []);

        Assert.That(result.Warnings.Where(w => w.Contains("isBig")).ToList(), Is.Empty,
            "Same name, different type overloads should not produce warnings");
    }

    // ── Cross-package conflict detection ──

    [Test]
    public void ImportImport_SameNameSameType_ProducesAmbiguityWarning()
    {
        var file1 = ScriptParser.Parse("predicate isBig(Type) => Type.Name:sw('Big')", "a.cop");
        var file2 = ScriptParser.Parse("predicate isBig(Type) => Type.Name:sw('Huge')", "b.cop");

        var stamped1 = StampPackage(file1, "pkg1");
        var stamped2 = StampPackage(file2, "pkg2");

        var interpreter = CreateInterpreter();
        var result = interpreter.Run([stamped1, stamped2], []);

        Assert.That(result.Warnings.Any(w => w.Contains("Ambiguous") && w.Contains("isBig") && w.Contains("pkg1") && w.Contains("pkg2")),
            Is.True, $"Expected ambiguity warning but got: {string.Join("; ", result.Warnings)}");
    }

    [Test]
    public void ImportImport_LetConflict_ProducesAmbiguityWarning()
    {
        var file1 = ScriptParser.Parse("let threshold = 10", "a.cop");
        var file2 = ScriptParser.Parse("let threshold = 20", "b.cop");

        var stamped1 = StampPackage(file1, "pkg1");
        var stamped2 = StampPackage(file2, "pkg2");

        var interpreter = CreateInterpreter();
        var result = interpreter.Run([stamped1, stamped2], []);

        Assert.That(result.Warnings.Any(w => w.Contains("Ambiguous") && w.Contains("threshold")),
            Is.True, $"Expected ambiguity warning but got: {string.Join("; ", result.Warnings)}");
    }

    // ── Deterministic file ordering ──

    [Test]
    public void FileOrderDoesNotAffectResult()
    {
        var fileA = ScriptParser.Parse("predicate isSmall(Type) => Type.Name:sw('Small')", "a.cop");
        var fileB = ScriptParser.Parse("predicate isBig(Type) => Type.Name:sw('Big')", "b.cop");

        var interpreter1 = CreateInterpreter();
        var result1 = interpreter1.Run([fileA, fileB], []);

        var interpreter2 = CreateInterpreter();
        var result2 = interpreter2.Run([fileB, fileA], []);

        Assert.That(result1.Warnings, Is.EqualTo(result2.Warnings),
            "Results should be identical regardless of file order");
    }

    // ── Helpers ──

    private static ScriptFile StampPackage(ScriptFile sf, string packageName)
    {
        var preds = sf.Predicates.Select(p => p with { PackageName = packageName }).ToList();
        var funcs = sf.Functions.Select(f => f with { PackageName = packageName }).ToList();
        var lets = sf.LetDeclarations.Select(l => l with { PackageName = packageName }).ToList();

        return sf with
        {
            Predicates = preds,
            Functions = funcs,
            LetDeclarations = lets,
        };
    }
}
