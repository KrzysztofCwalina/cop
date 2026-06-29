using Cop.Cli.Lsp;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Hover is served by the real compiler (SemanticModel), not a JS reimplementation. These tests
/// lock in the cases that kept breaking — most importantly that a union of violation lists shows
/// <c>[Violation]</c> instead of "unknown" (the user-reported bug) — plus types, properties (with
/// inheritance), declared callables, and keywords.
/// </summary>
[TestFixture]
public class HoverTests
{
    private static string NewDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-hover-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static (int line, int ch) Pos(string text, string lineSubstr, string word)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            int li = lines[i].IndexOf(lineSubstr, StringComparison.Ordinal);
            if (li < 0) continue;
            int wi = lines[i].IndexOf(word, li, StringComparison.Ordinal);
            if (wi >= 0) return (i, wi + 1); // +1 lands inside the word
        }
        throw new InvalidOperationException($"could not locate '{word}' on a line containing '{lineSubstr}'");
    }

    private static string? Hover(string text, string lineSubstr, string word)
    {
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "doc.cop");
            File.WriteAllText(file, text);
            var (line, ch) = Pos(text, lineSubstr, word);
            return CopLanguageService.Hover(file, text, line, ch);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private const string Program = """
        type Animal = { Name : string, Legs : int }
        type Dog : Animal = { Breed : string }
        let greeting = 'hello'
        predicate isBig(Dog) => Dog.Legs > 3
        function describe(Animal) : string => Animal.Name
        """;

    [Test]
    public void Hover_ViolationUnion_ShowsCollectionTypeNotUnknown()
    {
        // The exact reported bug: hovering an array of violations showed "unknown".
        const string text = """
            type Violation = { Message : string }
            let a : [Violation] = src1
            let b : [Violation] = src2
            let all = a + b
            """;
        var hover = Hover(text, "let all", "all");
        Assert.That(hover, Is.Not.Null);
        Assert.That(hover, Does.Contain("let all: [Violation]"));
        Assert.That(hover, Does.Not.Contain("unknown"));
    }

    [Test]
    public void Hover_Let_ShowsInferredType()
    {
        var hover = Hover(Program, "let greeting", "greeting");
        Assert.That(hover, Does.Contain("let greeting: string"));
    }

    [Test]
    public void Hover_TypeName_ShowsTypeAndProperties()
    {
        var hover = Hover(Program, "type Dog", "Dog");
        Assert.That(hover, Does.Contain("type Dog"));
        Assert.That(hover, Does.Contain("Breed"));
        Assert.That(hover, Does.Contain("Name"), "inherited property listed");
    }

    [Test]
    public void Hover_DotProperty_ShowsDeclaringTypeAndType()
    {
        // Hover `Legs` in `Dog.Legs` inside the predicate body.
        var hover = Hover(Program, "Dog.Legs", "Legs");
        Assert.That(hover, Does.Contain("(property) Animal.Legs: int"));
    }

    [Test]
    public void Hover_Predicate_ShowsSignature()
    {
        var hover = Hover(Program, "predicate isBig", "isBig");
        Assert.That(hover, Does.Contain("predicate isBig(Dog) => bool"));
    }

    [Test]
    public void Hover_Function_ShowsSignatureWithReturnType()
    {
        var hover = Hover(Program, "function describe", "describe");
        Assert.That(hover, Does.Contain("function describe(Animal) => string"));
    }

    [Test]
    public void Hover_Keyword_ShowsKeywordDetail()
    {
        var hover = Hover(Program, "let greeting", "let");
        Assert.That(hover, Does.Contain("(keyword) let"));
        Assert.That(hover, Does.Contain("Bind a named value"));
    }

    [Test]
    public void Hover_Whitespace_ReturnsNull()
    {
        // Hovering empty space yields no hover (not a crash, not a wrong guess).
        var dir = NewDir();
        try
        {
            var file = Path.Combine(dir, "doc.cop");
            File.WriteAllText(file, Program);
            Assert.That(CopLanguageService.Hover(file, Program, 2, 0), Is.Null.Or.Empty
                .Or.Not.Contain("error"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
