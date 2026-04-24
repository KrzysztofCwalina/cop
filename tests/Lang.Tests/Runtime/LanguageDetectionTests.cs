using Cop.Lang;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Runtime;

public class LanguageDetectionTests
{
    [Test]
    public void DetectsOnlyCsharp_WhenScriptsOnlyUseCsharpFilter()
    {
        var source = """
            let types = Code.Types:csharp
            command CHECK = PRINT('{item.Name}', types)
            """;
        var sf = ScriptParser.Parse(source, "test.cop");
        var result = Engine.DetectRequiredLanguages([sf]);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("csharp"));
        Assert.That(result, Has.Count.EqualTo(1));
    }

    [Test]
    public void DetectsMultipleLanguages_WhenScriptsUseMultiple()
    {
        var source = """
            let csTypes = Code.Types:csharp
            let pyTypes = Code.Types:python
            command CHECK = PRINT('{item.Name}', csTypes)
            """;
        var sf = ScriptParser.Parse(source, "test.cop");
        var result = Engine.DetectRequiredLanguages([sf]);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("csharp"));
        Assert.That(result, Does.Contain("python"));
    }

    [Test]
    public void ReturnsNull_WhenNoLanguageFiltersUsed()
    {
        var source = """
            let types = Code.Types
            command CHECK = PRINT('{item.Name}', types)
            """;
        var sf = ScriptParser.Parse(source, "test.cop");
        var result = Engine.DetectRequiredLanguages([sf]);

        Assert.That(result, Is.Null, "Should return null when no language filters found (parse all)");
    }

    [Test]
    public void DetectsLanguage_InPredicateConstraint()
    {
        var source = """
            predicate client(Type:csharp) => Type.Name:ew('Client')
            let clients = Code.Types:client
            command CHECK = PRINT('{item.Name}', clients)
            """;
        var sf = ScriptParser.Parse(source, "test.cop");
        var result = Engine.DetectRequiredLanguages([sf]);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("csharp"));
    }

    [Test]
    public void DetectsLanguage_InCommandFilters()
    {
        var source = """
            command CHECK = PRINT('{item.Name}', Code.Types:javascript)
            """;
        var sf = ScriptParser.Parse(source, "test.cop");
        var result = Engine.DetectRequiredLanguages([sf]);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("javascript"));
    }

    [Test]
    public void DetectsLanguage_AcrossMultipleScriptFiles()
    {
        var source1 = """
            let types = Code.Types:csharp
            command CHECK1 = PRINT('{item.Name}', types)
            """;
        var source2 = """
            let types = Code.Types:python
            command CHECK2 = PRINT('{item.Name}', types)
            """;
        var sf1 = ScriptParser.Parse(source1, "test1.cop");
        var sf2 = ScriptParser.Parse(source2, "test2.cop");
        var result = Engine.DetectRequiredLanguages([sf1, sf2]);

        Assert.That(result, Is.Not.Null);
        Assert.That(result, Does.Contain("csharp"));
        Assert.That(result, Does.Contain("python"));
    }

    [Test]
    public void NeedsSourceParsing_TrueWhenLetReferencesTypes()
    {
        var source = "let types = Code.Types\ncommand CHECK = PRINT('{item.Name}', types)";
        var sf = ScriptParser.Parse(source, "test.cop");
        Assert.That(Engine.NeedsSourceParsing([sf]), Is.True);
    }

    [Test]
    public void NeedsSourceParsing_TrueWhenImportsCode()
    {
        var source = "import code\nlet x = Statements\ncommand CHECK = PRINT('{item.Name}', x)";
        var sf = ScriptParser.Parse(source, "test.cop");
        Assert.That(Engine.NeedsSourceParsing([sf]), Is.True);
    }

    [Test]
    public void NeedsSourceParsing_FalseWhenOnlyFilesystem()
    {
        var source = "let files = DiskFiles\ncommand CHECK = PRINT('{item.Name}', files)";
        var sf = ScriptParser.Parse(source, "test.cop");
        Assert.That(Engine.NeedsSourceParsing([sf]), Is.False);
    }
}
