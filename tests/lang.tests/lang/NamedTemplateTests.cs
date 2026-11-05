using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class NamedTemplateTests
{
    private static CopValue Run(string source, ForeignFunctionRegistry? ffi = null)
    {
        var evaluator = new Evaluator(ffi, "templates.cop");
        var module = CopParser.Parse(source, "templates.cop");
        evaluator.RegisterDeclarations(module);
        evaluator.EvalLetBindings(module);
        return Evaluator.ForceValue(evaluator.RunCommand("MAIN"));
    }

    [TestCase("message")]
    [TestCase("alias")]
    [TestCase("second-alias")]
    public void TemplateRendersEachItemThroughTypedArguments(string binding)
    {
        var result = Run($$"""
            let prefix = 'Type'
            let message = '{prefix} {item.Name@dim}'
            let alias = message
            let second-alias = alias
            function label(value, text: string) : string => text
            let values = [{ Name = 'Alpha' }, { Name = 'Beta' }]
            command MAIN = values.Select(label(item, {{binding}}))
            """);

        Assert.That(result.Display(), Is.EqualTo("[Type {Alpha@dim}, Type {Beta@dim}]"));
    }

    [Test]
    public void NestedTemplatesRenderEachItem()
    {
        var result = Run("""
            let name = '{item.Name}'
            let message = 'Type {name}'
            command MAIN = [{ Name = 'Alpha' }, { Name = 'Beta' }].Select(message)
            """);

        Assert.That(result.Display(), Is.EqualTo("[Type Alpha, Type Beta]"));
    }

    [TestCase("item.Name == first-name ? label : item.Name", "[constant, Beta]")]
    [TestCase("item.Name == first-name || item.Flag", "[true, false]")]
    public void BranchesDependingOnItemAreNotMemoized(string expression, string expected)
    {
        var result = Run($$"""
            let first-name = 'Alpha'
            let label = 'constant'
            let message = '{ {{expression}} }'
            command MAIN = [{ Name = 'Alpha', Flag = false }, { Name = 'Beta', Flag = false }].Select(message)
            """);

        Assert.That(result.Display(), Is.EqualTo(expected));
    }

    [Test]
    public void MatchInTemplateUsesTheActualItem()
    {
        var result = Run("""
            let yes = 'yes'
            let no = 'no'
            let message = '{item ? true => yes | false => no}'
            command MAIN = [true, false].Select(message)
            """);

        Assert.That(result.Display(), Is.EqualTo("[yes, no]"));
    }

    [Test]
    public void TemplateUsesLexicalBindingsInsteadOfCallerBindings()
    {
        var result = Run("""
            let prefix = 'Lexical'
            let message = '{prefix} {item}'
            function label(prefix: string) => ['Alpha', 'Beta'].Select(message)
            command MAIN = label('Caller')
            """);

        Assert.That(result.Display(), Is.EqualTo("[Lexical Alpha, Lexical Beta]"));
    }

    [Test]
    public void ContextIndependentTemplateIsMemoized()
    {
        var calls = 0;
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("read", (args, env) => new CopInt(++calls));

        var result = Run("""
            let message = 'Value {read()}'
            command MAIN = ['Alpha', 'Beta'].Select(message)
            """, ffi);

        Assert.That(result.Display(), Is.EqualTo("[Value 1, Value 1]"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void NestedItemScopeDoesNotMakeTemplateContextDependent()
    {
        var calls = 0;
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("read", (args, env) => new CopInt(++calls));

        var result = Run("""
            let inner = ['Inner']
            let message = 'Values {inner.Select(item)} {read()}'
            command MAIN = ['Alpha', 'Beta'].Select(message)
            """, ffi);

        Assert.That(result.Display(), Is.EqualTo("[Values [Inner] 1, Values [Inner] 1]"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void UnusedTemplateAndOrdinaryAliasStayLazy()
    {
        var calls = 0;
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("read", (args, env) => new CopInt(++calls));

        var result = Run("""
            let value = read()
            let alias = value
            let message = '{read()} {item}'
            function ignore(value, unused) => true
            command MAIN = ['Alpha', 'Beta'].Select(ignore(item, alias))
            """, ffi);

        Assert.That(result.Display(), Is.EqualTo("[true, true]"));
        Assert.That(calls, Is.EqualTo(0));
    }

    [Test]
    public void OrdinaryBindingsDoNotAcquireDynamicScope()
    {
        var error = Assert.Throws<CopEvaluationException>(() => Run("""
            let value = item.Name
            command MAIN = [{ Name = 'Alpha' }].Select(value)
            """));

        Assert.That(error!.Message, Does.Contain("Undefined variable 'item'"));
    }

    [TestCase("let message = '{message} {item}'")]
    [TestCase("let message = alias\nlet alias = message")]
    public void RecursiveTemplateOrAliasReportsAnError(string declarations)
    {
        var error = Assert.Throws<CopEvaluationException>(() => Run($$"""
            {{declarations}}
            command MAIN = ['Alpha'].Select(message)
            """));

        Assert.That(error!.Message, Does.Contain("Recursive thunk forcing detected"));
    }

    [Test]
    public void TemplateWithoutItemContextReportsAnError()
    {
        var error = Assert.Throws<CopEvaluationException>(() => Run("""
            let message = 'Type {item.Name}'
            command MAIN = message
            """));

        Assert.That(error!.Message, Does.Contain("Undefined variable 'item'"));
    }

    [Test]
    public void BlockLocalTemplateRendersEachItem()
    {
        var result = Run("""
            let values = ['Alpha', 'Beta']
            command MAIN = {
                let message = 'Type {item}'
                values.Select(message)
            }
            """);

        Assert.That(result.Display(), Is.EqualTo("[Type Alpha, Type Beta]"));
    }

    [Test]
    public void DirectModuleEvaluationSupportsNamedTemplates()
    {
        var evaluator = new Evaluator();
        evaluator.EvalModule(CopParser.Parse("""
            let message = 'Type {item}'
            command MAIN = ['Alpha', 'Beta'].Select(message)
            """));

        Assert.That(evaluator.RunCommand("MAIN").Display(), Is.EqualTo("[Type Alpha, Type Beta]"));
    }

    [Test]
    public void LexicallyBoundItemIsNotReplaced()
    {
        var evaluator = new Evaluator();
        evaluator.GlobalEnvironment.Define("item", new CopString("Captured"));
        var module = CopParser.Parse("let message = 'Type {item}'");
        evaluator.EvalLetBindings(module);
        var caller = evaluator.GlobalEnvironment.Extend();
        caller.Define("item", new CopString("Caller"));

        var value = evaluator.Eval(new Cop.Lang.Ast.IdentifierExpr("message"), caller);

        Assert.That(value.Display(), Is.EqualTo("Type Captured"));
    }
}
