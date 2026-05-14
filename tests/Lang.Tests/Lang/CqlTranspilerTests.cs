using Cop.Lang;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CqlTranspilerTests
{
    private static ScriptFile ParseCop(string source, string path = "test.cop")
        => ScriptParser.Parse(source, path);

    // --- String escaping ---

    [Test]
    public void SanitizeIdentifier_ReplacesHyphens()
    {
        Assert.That(CqlTranspiler.SanitizeIdentifier("thread-sleep-calls"), Is.EqualTo("thread_sleep_calls"));
    }

    [Test]
    public void EscapeCqlString_EscapesWildcards()
    {
        // CodeQL matches() treats % and _ as wildcards — must be escaped
        Assert.That(CqlTranspiler.EscapeCqlString("100%"), Is.EqualTo("100\\%"));
        Assert.That(CqlTranspiler.EscapeCqlString("some_value"), Is.EqualTo("some\\_value"));
    }

    [Test]
    public void EscapeCqlString_EscapesQuotes()
    {
        Assert.That(CqlTranspiler.EscapeCqlString("say \"hello\""), Is.EqualTo("say \\\"hello\\\""));
    }

    // --- Simple predicate transpilation ---

    [Test]
    public void SimplePredicateWithKindAndMember_TranspilesToCodeQL()
    {
        var source = """
            import code
            predicate isSleepCall(Statement) => Statement.Kind == 'call' && Statement.MemberName == 'Sleep'
            export let sleep-calls = Code.Statements:isCSharp:isSleepCall
                :toError('Do not use Sleep')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Files, Has.Count.EqualTo(1));
        Assert.That(result.Files[0].FileName, Is.EqualTo("sleep_calls.ql"));

        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("import csharp"));
        Assert.That(ql, Does.Contain("MethodAccess"));
        Assert.That(ql, Does.Contain("@problem.severity error"));
    }

    [Test]
    public void ModifierPredicateIsSet_TranspilesToCodeQL()
    {
        var source = """
            import code
            predicate isPublic(Type) => Type.Modifiers:isSet(Public)
            export let public-types = Code.Types:isCSharp:isPublic
                :toWarning('Type is public')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        Assert.That(result.Files, Has.Count.EqualTo(1));

        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("isPublic()"));
        Assert.That(ql, Does.Contain("@problem.severity warning"));
    }

    [Test]
    public void StringStartsWith_TranspilesToMatchesPattern()
    {
        var source = """
            import code
            predicate nameStartsWithI(Type) => Type.Name:startsWith('I')
            export let types-starting-with-i = Code.Types:isCSharp:nameStartsWithI
                :toInfo('Starts with I')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        // Case-insensitive: uses toLowerCase and escaped pattern
        Assert.That(ql, Does.Contain("toLowerCase()"));
        Assert.That(ql, Does.Contain("matches("));
    }

    // --- Error cases ---

    [Test]
    public void NonCodeCollection_ProducesError()
    {
        var source = """
            export let all-files = DiskFiles
                :toError('found a file')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.True);
        Assert.That(result.Errors[0], Does.Contain("cannot be transpiled to CodeQL"));
    }

    [Test]
    public void NoExportedLetBindings_ProducesEmptyResult()
    {
        var source = """
            import code
            predicate isPublic(Type) => Type.Modifiers:isSet(Public)
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False);
        Assert.That(result.Files, Has.Count.EqualTo(0));
    }

    // --- Language filter ---

    [Test]
    public void LanguageFilter_SelectsCorrectImport()
    {
        var source = """
            import code
            export let py-types = Code.Types:isPython
                :toWarning('Python type')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("import python"));
    }

    // --- Negation ---

    [Test]
    public void NegatedPredicate_TranspilesToNot()
    {
        var source = """
            import code
            predicate isSealed(Type) => Type.Modifiers:isSet(Sealed)
            export let unsealed = Code.Types:isCSharp:!isSealed
                :toWarning('Not sealed')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("not"));
        Assert.That(ql, Does.Contain("isSealed()"));
    }

    // --- Compound predicates ---

    [Test]
    public void CompoundAndPredicate_TranspilesToCodeQL()
    {
        var source = """
            import code
            predicate isThreadSleep(Statement) => Statement.Kind == 'call'
                && Statement.TypeName == 'Thread' && Statement.MemberName == 'Sleep'
            export let sleep-calls = Code.Statements:isCSharp:isThreadSleep
                :toError('Use Task.Delay')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        // Kind == 'call' should narrow to MethodAccess, other conditions become where clauses
        Assert.That(ql, Does.Contain("MethodAccess"));
        Assert.That(ql, Does.Contain("and"));
    }

    // --- Metadata ---

    [Test]
    public void GeneratedQuery_HasMetadata()
    {
        var source = """
            import code

            ## Check for public types
            export let public-types = Code.Types:isCSharp
                :toWarning('Public type')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("@name"));
        Assert.That(ql, Does.Contain("@kind problem"));
        Assert.That(ql, Does.Contain("@id cop/public_types"));
        Assert.That(ql, Does.Contain("from"));
        Assert.That(ql, Does.Contain("select"));
    }

    // --- Kind narrowing ---

    [Test]
    public void StatementKindViaPredicateCall_NarrowsToMethodAccess()
    {
        var source = """
            import code
            predicate isACall(Statement) => Statement.Kind == 'call'
            export let all-calls = Code.Statements:isCSharp:isACall
                :toWarning('Call found')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("MethodAccess"));
    }

    // --- Collection contains ---

    [Test]
    public void BaseTypesContains_TranspilesToExists()
    {
        var source = """
            import code
            predicate implementsDisposable(Type) => Type.BaseTypes:contains('IDisposable')
            export let disposable-types = Code.Types:isCSharp:implementsDisposable
                :toWarning('Implements IDisposable')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("exists("));
        Assert.That(ql, Does.Contain("idisposable")); // case-insensitive lowered
    }

    // --- Calls collection ---

    [Test]
    public void CallsCollection_DefaultsToMethodAccess()
    {
        var source = """
            import code
            export let all-calls = Code.Calls:isCSharp
                :toWarning('Call found')
            """;
        var sf = ParseCop(source);
        var transpiler = new CqlTranspiler(sf, []);
        var result = transpiler.Transpile();

        Assert.That(result.HasErrors, Is.False, string.Join("\n", result.Errors));
        var ql = result.Files[0].Content;
        Assert.That(ql, Does.Contain("MethodAccess"));
    }
}
