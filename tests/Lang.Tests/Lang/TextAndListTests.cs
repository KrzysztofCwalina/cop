using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class TextAndListTests
{
    private static TypeDeclaration MakeType(string name = "Foo") =>
        new(name, TypeKind.Class, Modifier.Public, [], [], [], [], [], [], 1);

    private static TypeRegistry CreateTestRegistry()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        return registry;
    }

    [Test]
    public void Text_String_ReturnsIdentity()
    {
        var source = """predicate test(Type) => Text(Type.Name) == 'Foo' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Text_Int_ReturnsStringRepresentation()
    {
        var source = """predicate test(Type) => Text(Type.Line) == '1' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Text_Bool_ReturnsLowercaseString()
    {
        var source = """predicate test(Type) => Text(Type.Documented) == 'true' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var type = new TypeDeclaration("Foo", TypeKind.Class, Modifier.Public, [], [], [], [], [], [], 1) { HasDocComment = true };
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, type, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void ListLiteral_ParsedAndEvaluated()
    {
        var source = """predicate test(Type) => [1 2 3]""";
        var file = ScriptParser.Parse(source, "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.InstanceOf<ListLiteralExpr>());
        var list = (ListLiteralExpr)body;
        Assert.That(list.Elements, Has.Count.EqualTo(3));
    }

    [Test]
    public void ListLiteral_Empty_Parsed()
    {
        var source = """predicate test(Type) => []""";
        var file = ScriptParser.Parse(source, "test.cop");
        var body = file.Predicates[0].Body;
        Assert.That(body, Is.InstanceOf<ListLiteralExpr>());
        Assert.That(((ListLiteralExpr)body).Elements, Has.Count.EqualTo(0));
    }

    [Test]
    public void ListLiteral_EvaluatesToList()
    {
        var source = """predicate test(Type) => ['a' 'b']""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.True);
    }

    // --- let declarations with list literals ---

    [Test]
    public void LetListLiteral_Parsed()
    {
        var source = """
            let Keywords = ['Test' 'Bench' 'Perf']
            predicate test(Type) => Type.Name:ca(Keywords)
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.LetDeclarations, Has.Count.EqualTo(1));
        var letDecl = file.LetDeclarations[0];
        Assert.That(letDecl.Name, Is.EqualTo("Keywords"));
        Assert.That(letDecl.IsValueBinding, Is.True);
        Assert.That(letDecl.ValueExpression, Is.InstanceOf<ListLiteralExpr>());
        Assert.That(((ListLiteralExpr)letDecl.ValueExpression!).Elements, Has.Count.EqualTo(3));
    }

    [Test]
    public void LetListLiteral_EmptyList_Parsed()
    {
        var source = """
            let Empty = []
            predicate test(Type) => Type.Name:ca(Empty)
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        Assert.That(file.LetDeclarations[0].IsValueBinding, Is.True);
        Assert.That(((ListLiteralExpr)file.LetDeclarations[0].ValueExpression!).Elements, Has.Count.EqualTo(0));
    }

    // --- ca predicate ---

    [Test]
    public void ContainsAny_InlineList_Match()
    {
        var source = """predicate test(Type) => Type.Name:ca(['Fo' 'Bar'])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void ContainsAny_InlineList_NoMatch()
    {
        var source = """predicate test(Type) => Type.Name:ca(['Bar' 'Baz'])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void ContainsAny_EmptyList_ReturnsFalse()
    {
        var source = """predicate test(Type) => Type.Name:ca([])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void ContainsAny_NamedList_Match()
    {
        var source = """
            let Keywords = ['Fo' 'Bar']
            predicate test(Type) => Type.Name:ca(Keywords)
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var letDecls = new Dictionary<string, LetDeclaration>
        {
            ["Keywords"] = file.LetDeclarations[0]
        };
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry(), letDeclarations: letDecls);
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void ContainsAny_NamedList_NoMatch()
    {
        var source = """
            let Keywords = ['Bar' 'Baz']
            predicate test(Type) => Type.Name:ca(Keywords)
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var letDecls = new Dictionary<string, LetDeclaration>
        {
            ["Keywords"] = file.LetDeclarations[0]
        };
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry(), letDeclarations: letDecls);
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void ContainsAny_Negated()
    {
        var source = """predicate test(Type) => !Type.Name:ca(['Test' 'Bench'])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());

        // "Foo" doesn't contain "Test" or "Bench" → negated is true
        var (result1, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result1, Is.True);

        // "TestProject" contains "Test" → negated is false
        var (result2, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("TestProject"), "Type");
        Assert.That(result2, Is.False);
    }

    // --- in predicate ---

    [Test]
    public void In_InlineList_Match()
    {
        var source = """predicate test(Type) => Type.Name:in(['Foo' 'Bar'])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void In_InlineList_NoMatch()
    {
        var source = """predicate test(Type) => Type.Name:in(['Bar' 'Baz'])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void In_NamedList_Match()
    {
        var source = """
            let AllowedNames = ['Foo' 'Bar']
            predicate test(Type) => Type.Name:in(AllowedNames)
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var letDecls = new Dictionary<string, LetDeclaration>
        {
            ["AllowedNames"] = file.LetDeclarations[0]
        };
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry(), letDeclarations: letDecls);
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void In_ExactMatch_NotSubstring()
    {
        // "in" does exact match, not substring — "Fo" should NOT match "Foo"
        var source = """predicate test(Type) => Type.Name:in(['Fo' 'Ba'])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void In_EmptyList_ReturnsFalse()
    {
        var source = """predicate test(Type) => Type.Name:in([])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.False);
    }

    // --- Case-insensitive comparisons (default behavior) ---

    [Test]
    public void Equals_CaseInsensitive_ByDefault()
    {
        var source = """predicate test(Type) => Type.Name == 'foo' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True, "== should be case-insensitive by default");
    }

    [Test]
    public void NotEquals_CaseInsensitive_ByDefault()
    {
        var source = """predicate test(Type) => Type.Name != 'foo' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.False, "!= should be case-insensitive — 'Foo' != 'foo' is false");
    }

    [Test]
    public void Contains_CaseInsensitive_ByDefault()
    {
        var source = """predicate test(Type) => Type.Name:ct('fo')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True, "contains should be case-insensitive by default");
    }

    [Test]
    public void StartsWith_CaseInsensitive_ByDefault()
    {
        var source = """predicate test(Type) => Type.Name:sw('fo')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True, "startsWith should be case-insensitive by default");
    }

    [Test]
    public void EndsWith_CaseInsensitive_ByDefault()
    {
        var source = """predicate test(Type) => Type.Name:ew('OO')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True, "endsWith should be case-insensitive by default");
    }

    [Test]
    public void In_CaseInsensitive_ByDefault()
    {
        var source = """predicate test(Type) => Type.Name:in(['foo' 'bar'])""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True, "in should match case-insensitively");
    }

    [Test]
    public void Matches_StillCaseSensitive()
    {
        var source = """predicate test(Type) => Type.Name:rx('^Foo$')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());

        var (matchExact, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(matchExact, Is.True);

        var (matchWrong, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("foo"), "Type");
        Assert.That(matchWrong, Is.False, "matches (regex) should remain case-sensitive");
    }

    // --- words predicate ---

    [Test]
    public void Words_PascalCase()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("TaskCompletionSource");
        Assert.That(words, Is.EqualTo(new List<object> { "task", "completion", "source" }));
    }

    [Test]
    public void Words_CamelCase()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("taskCompletionSource");
        Assert.That(words, Is.EqualTo(new List<object> { "task", "completion", "source" }));
    }

    [Test]
    public void Words_SnakeCase()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("task_completion_source");
        Assert.That(words, Is.EqualTo(new List<object> { "task", "completion", "source" }));
    }

    [Test]
    public void Words_KebabCase()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("task-completion-source");
        Assert.That(words, Is.EqualTo(new List<object> { "task", "completion", "source" }));
    }

    [Test]
    public void Words_UpperSnakeCase()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("TASK_COMPLETION_SOURCE");
        Assert.That(words, Is.EqualTo(new List<object> { "task", "completion", "source" }));
    }

    [Test]
    public void Words_Acronym()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("HTTPClient");
        Assert.That(words, Is.EqualTo(new List<object> { "http", "client" }));
    }

    [Test]
    public void Words_SingleWord()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("sleep");
        Assert.That(words, Is.EqualTo(new List<object> { "sleep" }));
    }

    [Test]
    public void Words_Empty()
    {
        var words = PredicateEvaluator.SplitIdentifierWords("");
        Assert.That(words, Is.Empty);
    }

    [Test]
    public void Words_InPredicate_ContainsWord()
    {
        var source = """predicate test(Type) => Type.Name.Words:contains('completion')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("TaskCompletionSource"), "Type");
        Assert.That(result, Is.True, "words should split PascalCase and allow contains on words");
    }

    // --- Lower and Upper string properties ---

    [Test]
    public void Lower_Property()
    {
        var source = """predicate test(Type) => Type.Name.Lower == 'foo' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Upper_Property()
    {
        var source = """predicate test(Type) => Type.Name.Upper == 'FOO' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    // --- Normalized property (convention-insensitive) ---

    [Test]
    public void NormalizeIdentifier_PascalCase()
    {
        Assert.That(PredicateEvaluator.NormalizeIdentifier("FooBar"), Is.EqualTo("foobar"));
    }

    [Test]
    public void NormalizeIdentifier_SnakeCase()
    {
        Assert.That(PredicateEvaluator.NormalizeIdentifier("foo_bar"), Is.EqualTo("foobar"));
    }

    [Test]
    public void NormalizeIdentifier_CamelCase()
    {
        Assert.That(PredicateEvaluator.NormalizeIdentifier("fooBar"), Is.EqualTo("foobar"));
    }

    [Test]
    public void NormalizeIdentifier_KebabCase()
    {
        Assert.That(PredicateEvaluator.NormalizeIdentifier("foo-bar"), Is.EqualTo("foobar"));
    }

    [Test]
    public void NormalizeIdentifier_UpperSnakeCase()
    {
        Assert.That(PredicateEvaluator.NormalizeIdentifier("FOO_BAR"), Is.EqualTo("foobar"));
    }

    [Test]
    public void NormalizeIdentifier_Acronym()
    {
        Assert.That(PredicateEvaluator.NormalizeIdentifier("HTTPClient"), Is.EqualTo("httpclient"));
    }

    [Test]
    public void Normalized_Property()
    {
        var source = """predicate test(Type) => Type.Name.Normalized == 'foobar' """;
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());

        // PascalCase
        var (r1, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("FooBar"), "Type");
        Assert.That(r1, Is.True, "FooBar.Normalized should equal 'foobar'");

        // Already normalized
        var (r2, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("foobar"), "Type");
        Assert.That(r2, Is.True);
    }

    // --- :sm() predicate (convention-insensitive equality) ---

    [Test]
    public void Same_PascalVsSnake()
    {
        var source = """predicate test(Type) => Type.Name:sm('foo_bar')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("FooBar"), "Type");
        Assert.That(result, Is.True, ":same should treat FooBar and foo_bar as equal");
    }

    [Test]
    public void Same_CamelVsPascal()
    {
        var source = """predicate test(Type) => Type.Name:sm('fooBar')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("FooBar"), "Type");
        Assert.That(result, Is.True, ":same should treat fooBar and FooBar as equal");
    }

    [Test]
    public void Same_NoMatch()
    {
        var source = """predicate test(Type) => Type.Name:sm('baz_qux')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("FooBar"), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void Same_CrossLanguage_ConfigureAwait()
    {
        var source = """predicate test(Type) => Type.Name:sm('configure_await')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());

        // PascalCase (C#)
        var (r1, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("ConfigureAwait"), "Type");
        Assert.That(r1, Is.True);

        // camelCase (JS)
        var (r2, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("configureAwait"), "Type");
        Assert.That(r2, Is.True);

        // UPPER_SNAKE_CASE
        var (r3, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("CONFIGURE_AWAIT"), "Type");
        Assert.That(r3, Is.True);
    }

    // --- String concatenation ---

    [Test]
    public void StringConcat_PropertyPlusLiteral()
    {
        var source = """predicate test(Type) => (Type.Name + 'Async'):equals('FooAsync')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void StringConcat_UsedAsContainsArg()
    {
        // Test that a computed string (Name + 'Async') can be passed to :contains on a list
        var source = """
            let Methods = ['FooAsync' 'BarAsync' 'Baz']
            predicate test(Type) => Methods:ct(Type.Name + 'Async')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var letDecls = new Dictionary<string, LetDeclaration>
        {
            ["Methods"] = file.LetDeclarations[0]
        };
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry(), letDeclarations: letDecls);
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Foo"), "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void StringConcat_NoMatch()
    {
        var source = """
            let Methods = ['FooAsync' 'BarAsync']
            predicate test(Type) => Methods:ct(Type.Name + 'Async')
            """;
        var file = ScriptParser.Parse(source, "test.cop");
        var letDecls = new Dictionary<string, LetDeclaration>
        {
            ["Methods"] = file.LetDeclarations[0]
        };
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry(), letDeclarations: letDecls);
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Qux"), "Type");
        Assert.That(result, Is.False);
    }

    // --- Collection flattening ---

    [Test]
    public void CollectionFlatten_AccessPropertyAcrossListItems()
    {
        // Types.MethodNames should flatten all method names across all types into one list
        var source = """predicate test(Type) => Types.MethodNames:ct(Type.Name + 'Async')""";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };

        var type1 = new TypeDeclaration("Service", TypeKind.Class, Modifier.Public,
            [], [], [], [new MethodDeclaration("ReadAsync", Modifier.Public, [], null, [], 1)], [], [], 1);
        var type2 = new TypeDeclaration("Client", TypeKind.Class, Modifier.Public,
            [], [], [], [new MethodDeclaration("WriteAsync", Modifier.Public, [], null, [], 1)], [], [], 1);

        // Provide the Types list as a resolved collection
        var resolvedCollections = new Dictionary<string, System.Collections.IList>
        {
            ["Types"] = new List<object> { type1, type2 }
        };

        var evaluator = new PredicateEvaluator(predicates, "test.cs", CreateTestRegistry(),
            resolvedCollections: resolvedCollections);

        // "Read" + "Async" = "ReadAsync" which IS in Types.MethodNames → true
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Read"), "Type");
        Assert.That(result, Is.True);

        // "Delete" + "Async" = "DeleteAsync" which is NOT in Types.MethodNames → false
        var (result2, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType("Delete"), "Type");
        Assert.That(result2, Is.False);
    }
}
