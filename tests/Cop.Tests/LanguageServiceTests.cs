using Cop.Cli.Lsp;
using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// The language service is what the editor calls instead of reimplementing the compiler. These
/// tests lock in that it (a) runs the real verify pipeline, (b) honors the in-memory buffer over
/// the file on disk, (c) resolves cross-file references in a directory, and (d) only returns
/// diagnostics for the open document. This is the behavior the VS Code extension kept getting wrong.
/// </summary>
[TestFixture]
public class LanguageServiceTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-langsvc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void Analyze_CleanFile_ReturnsNoDiagnostics()
    {
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "ok.cop");
            var text = "let greeting = 'hello'\n";
            File.WriteAllText(file, text);

            var diags = CopLanguageService.Analyze(file, text);

            Assert.That(diags, Is.Empty, "a valid program must produce zero diagnostics");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Analyze_UndefinedVariable_ReportsExactError()
    {
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "bad.cop");
            var text = "let x = undefinedThing\n";
            File.WriteAllText(file, text);

            var diags = CopLanguageService.Analyze(file, text);

            Assert.That(diags.Count, Is.EqualTo(1), "expected exactly one diagnostic");
            var d = diags[0];
            Assert.That(d.Severity, Is.EqualTo(CopDiagnosticSeverity.Error));
            Assert.That(d.Message, Is.EqualTo("Undefined variable 'undefinedThing'"));
            Assert.That(d.Line, Is.EqualTo(1));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Analyze_BufferOverridesDisk_BrokenBufferOverCleanDisk()
    {
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "doc.cop");
            File.WriteAllText(file, "let greeting = 'hello'\n"); // clean on disk

            // The editor buffer has an unsaved error; analysis must reflect the BUFFER, not disk.
            var diags = CopLanguageService.Analyze(file, "let x = undefinedThing\n");

            Assert.That(diags.Count, Is.EqualTo(1));
            Assert.That(diags[0].Message, Is.EqualTo("Undefined variable 'undefinedThing'"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Analyze_BufferOverridesDisk_CleanBufferOverBrokenDisk()
    {
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "doc.cop");
            File.WriteAllText(file, "let x = undefinedThing\n"); // broken on disk

            // The unsaved buffer fixed the error; analysis must report it clean.
            var diags = CopLanguageService.Analyze(file, "let greeting = 'hello'\n");

            Assert.That(diags, Is.Empty);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Analyze_MultiFile_SiblingReferenceResolves_NoFalseUndefined()
    {
        var dir = NewDir();
        try
        {
            // A cop "program" is the whole directory; b.cop references a `let` from a.cop.
            File.WriteAllText(Path.Combine(dir, "a.cop"), "let shared = 5\n");
            var b = Path.Combine(dir, "b.cop");
            var bText = "let y = shared\n";
            File.WriteAllText(b, bText);

            var diags = CopLanguageService.Analyze(b, bText);

            Assert.That(diags, Is.Empty,
                "a reference to a sibling file's let must resolve, not report 'undefined'");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Analyze_OnlyReturnsDiagnosticsForTheOpenDocument()
    {
        var dir = NewDir();
        try
        {
            // a.cop has an error on disk; we open the (clean) b.cop.
            File.WriteAllText(Path.Combine(dir, "a.cop"), "let x = undefinedThing\n");
            var b = Path.Combine(dir, "b.cop");
            var bText = "let y = 1\n";
            File.WriteAllText(b, bText);

            var diags = CopLanguageService.Analyze(b, bText);

            Assert.That(diags, Is.Empty,
                "a sibling file's error must not be attributed to the open document");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
