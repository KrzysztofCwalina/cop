using Cop.Cli.Lsp;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Completion is served entirely by the real compiler (SemanticModel + LanguageMetadata). These
/// tests lock in the main contexts — dot (properties, with inheritance), colon (predicates),
/// general (keywords/lets/types/callables), `=>` (actions) — and crucially that completion still
/// works while the buffer is mid-edit (an incomplete line that doesn't parse).
/// </summary>
[TestFixture]
public class CompletionTests
{
    private const string Header = """
        type Animal = { Name : string, Legs : int }
        type Dog : Animal = { Breed : string }
        let greeting = 'hello'
        predicate isBig(Dog) => Dog.Legs > 3
        function describe(Animal) : string => Animal.Name
        """;

    private static IReadOnlyList<CompletionEntry> Complete(string text)
    {
        // The cursor is at the end of the buffer (the typical completion position).
        var dir = Path.Combine(Path.GetTempPath(), "cop-cmp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "doc.cop");
            File.WriteAllText(file, text);
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int line = lines.Length - 1;
            int ch = lines[line].Length;
            return CopLanguageService.Complete(file, text, line, ch);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static IEnumerable<string> Labels(IReadOnlyList<CompletionEntry> e) => e.Select(x => x.Label);

    [Test]
    public void Dot_OnTypedParameter_OffersProperties_IncludingInherited()
    {
        // Mid-edit: the last line does not parse (trailing dot). Completion must still work.
        var text = Header + "\npredicate p(Dog) => Dog.";
        var labels = Labels(Complete(text)).ToHashSet();
        Assert.That(labels, Does.Contain("Breed"));
        Assert.That(labels, Does.Contain("Name"), "inherited from Animal");
        Assert.That(labels, Does.Contain("Legs"), "inherited from Animal");
    }

    [Test]
    public void Colon_OffersUniversalAndDeclaredPredicates()
    {
        var text = Header + "\npredicate p(Dog) => something:";
        var labels = Labels(Complete(text)).ToHashSet();
        // A declared predicate applicable to the element + universal builtins.
        Assert.That(labels, Does.Contain("isBig"), "declared predicate offered after colon");
    }

    [Test]
    public void General_OffersKeywordsLetsTypesAndCallables()
    {
        var text = Header + "\n";
        var labels = Labels(Complete(text)).ToHashSet();
        Assert.That(labels, Does.Contain("predicate"), "keyword");
        Assert.That(labels, Does.Contain("greeting"), "top-level let");
        Assert.That(labels, Does.Contain("Dog"), "type");
        Assert.That(labels, Does.Contain("isBig"), "declared predicate");
        Assert.That(labels, Does.Contain("describe"), "declared function");
    }

    [Test]
    public void Export_OffersDeclarationKeywords()
    {
        var text = Header + "\nexport ";
        var labels = Labels(Complete(text)).ToHashSet();
        Assert.That(labels, Does.Contain("predicate"));
        Assert.That(labels, Does.Contain("function"));
        Assert.That(labels, Does.Contain("type"));
        Assert.That(labels, Does.Not.Contain("greeting"), "only declaration keywords after export");
    }

    [Test]
    public void Dot_OnCollection_OffersCollectionMembers()
    {
        // `greeting` is a string; a string property like Length should be offered.
        var text = Header + "\nlet x = greeting.";
        var labels = Labels(Complete(text)).ToHashSet();
        Assert.That(labels, Does.Contain("Length"), "string property");
    }

    [Test]
    public void Completion_IsNonEmpty_EvenWithUnparseableTail()
    {
        // The whole point: a half-typed line must not blank the model.
        var text = Header + "\npredicate p(Animal) => Animal.";
        var result = Complete(text);
        Assert.That(result, Is.Not.Empty);
        Assert.That(Labels(result), Does.Contain("Name"));
    }
}
