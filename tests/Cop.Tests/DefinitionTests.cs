using Cop.Cli.Lsp;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Go-to-definition is served by the compiler's view of the program (a cop directory is one
/// program), so it resolves a name to its declaration even across files. Guards the LSP
/// definition provider.
/// </summary>
[TestFixture]
public class DefinitionTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-def-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public void Definition_ResolvesCrossFileTopLevelLet()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "defs.cop"), "let shared = 5\n");
            var main = Path.Combine(dir, "main.cop");
            var mainText = "let x = shared\n";
            File.WriteAllText(main, mainText);

            // Cursor on the `shared` use in main.cop (line 0, inside the word).
            var loc = CopLanguageService.Definition(main, mainText, 0, 9);

            Assert.That(loc, Is.Not.Null);
            Assert.That(Path.GetFileName(loc!.FilePath), Is.EqualTo("defs.cop"), "jumps to the other file");
            Assert.That(loc.Line, Is.EqualTo(0), "shared is declared on the first line (0-based 0)");
            Assert.That(loc.Column, Is.EqualTo(4), "'shared' starts at column 4 in 'let shared = 5'");
            Assert.That(loc.Length, Is.EqualTo("shared".Length));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Definition_ResolvesSameFilePredicate()
    {
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "doc.cop");
            var text = "predicate isBig(Type) => true\nlet x = isBig\n";
            File.WriteAllText(file, text);

            var loc = CopLanguageService.Definition(file, text, 1, 9); // `isBig` use on line 1

            Assert.That(loc, Is.Not.Null);
            Assert.That(loc!.Line, Is.EqualTo(0), "isBig declared on line 0");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Test]
    public void Definition_BuiltinOrUnknown_ReturnsNull()
    {
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "doc.cop");
            var text = "let x = startsWith\n"; // a builtin — no user declaration to jump to
            File.WriteAllText(file, text);

            Assert.That(CopLanguageService.Definition(file, text, 0, 10), Is.Null);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
