using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class NamedTemplateTests
{
    private static async Task<CopValue> RunAsync(string source, ForeignFunctionRegistry? ffi = null, CancellationToken cancellationToken = default)
    {
        var evaluator = new Evaluator(ffi, "templates.cop");
        var module = CopParser.Parse(source, "templates.cop");
        evaluator.RegisterDeclarations(module);
        await evaluator.EvalLetBindingsAsync(module, cancellationToken: cancellationToken);
        return (await Evaluator.ForceValueAsync((await evaluator.RunCommandAsync("MAIN", cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
    }

    [TestCase("message")]
    [TestCase("alias")]
    [TestCase("second-alias")]
    public async Task TemplateRendersEachItemThroughTypedArguments(string binding)
    {
        var result = (await RunAsync($$"""
            let prefix = 'Type'
            let message = '{prefix} {item.Name@dim}'
            let alias = message
            let second-alias = alias
            function label(value, text: string) : string => text
            let values = [{ Name = 'Alpha' }, { Name = 'Beta' }]
            command MAIN = values.Select(label(item, {{binding}}))
            """, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[Type {Alpha@dim}, Type {Beta@dim}]"));
    }

    [Test]
    public async Task NestedTemplatesRenderEachItem()
    {
        var result = (await RunAsync("""
            let name = '{item.Name}'
            let message = 'Type {name}'
            command MAIN = [{ Name = 'Alpha' }, { Name = 'Beta' }].Select(message)
            """, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[Type Alpha, Type Beta]"));
    }

    [TestCase("item.Name == first-name ? label : item.Name", "[constant, Beta]")]
    [TestCase("item.Name == first-name || item.Flag", "[true, false]")]
    public async Task BranchesDependingOnItemAreNotMemoized(string expression, string expected)
    {
        var result = (await RunAsync($$"""
            let first-name = 'Alpha'
            let label = 'constant'
            let message = '{ {{expression}} }'
            command MAIN = [{ Name = 'Alpha', Flag = false }, { Name = 'Beta', Flag = false }].Select(message)
            """, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo(expected));
    }

    [Test]
    public async Task MatchInTemplateUsesTheActualItem()
    {
        var result = (await RunAsync("""
            let yes = 'yes'
            let no = 'no'
            let message = '{item ? true => yes | false => no}'
            command MAIN = [true, false].Select(message)
            """, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[yes, no]"));
    }

    [Test]
    public async Task TemplateUsesLexicalBindingsInsteadOfCallerBindings()
    {
        var result = (await RunAsync("""
            let prefix = 'Lexical'
            let message = '{prefix} {item}'
            function label(prefix: string) => ['Alpha', 'Beta'].Select(message)
            command MAIN = label('Caller')
            """, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[Lexical Alpha, Lexical Beta]"));
    }

    [Test]
    public async Task ContextIndependentTemplateIsMemoized()
    {
        var calls = 0;
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("read", (args, env) => new CopInt(++calls));

        var result = (await RunAsync("""
            let message = 'Value {read()}'
            command MAIN = ['Alpha', 'Beta'].Select(message)
            """, ffi, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[Value 1, Value 1]"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task NestedItemScopeDoesNotMakeTemplateContextDependent()
    {
        var calls = 0;
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("read", (args, env) => new CopInt(++calls));

        var result = (await RunAsync("""
            let inner = ['Inner']
            let message = 'Values {inner.Select(item)} {read()}'
            command MAIN = ['Alpha', 'Beta'].Select(message)
            """, ffi, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[Values [Inner] 1, Values [Inner] 1]"));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public async Task UnusedTemplateAndOrdinaryAliasStayLazy()
    {
        var calls = 0;
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("read", (args, env) => new CopInt(++calls));

        var result = (await RunAsync("""
            let value = read()
            let alias = value
            let message = '{read()} {item}'
            function ignore(value, unused) => true
            command MAIN = ['Alpha', 'Beta'].Select(ignore(item, alias))
            """, ffi, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[true, true]"));
        Assert.That(calls, Is.EqualTo(0));
    }

    [Test]
    public void OrdinaryBindingsDoNotAcquireDynamicScope()
    {
        var error = Assert.ThrowsAsync<CopEvaluationException>(async () => await RunAsync("""
            let value = item.Name
            command MAIN = [{ Name = 'Alpha' }].Select(value)
            """, cancellationToken: CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("Undefined variable 'item'"));
    }

    [TestCase("let message = '{message} {item}'")]
    [TestCase("let message = alias\nlet alias = message")]
    public void RecursiveTemplateOrAliasReportsAnError(string declarations)
    {
        var error = Assert.ThrowsAsync<CopEvaluationException>(async () => await RunAsync($$"""
            {{declarations}}
            command MAIN = ['Alpha'].Select(message)
            """, cancellationToken: CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("Recursive thunk forcing detected"));
    }

    [Test]
    public void TemplateWithoutItemContextReportsAnError()
    {
        var error = Assert.ThrowsAsync<CopEvaluationException>(async () => await RunAsync("""
            let message = 'Type {item.Name}'
            command MAIN = message
            """, cancellationToken: CancellationToken.None));

        Assert.That(error!.Message, Does.Contain("Undefined variable 'item'"));
    }

    [Test]
    public async Task BlockLocalTemplateRendersEachItem()
    {
        var result = (await RunAsync("""
            let values = ['Alpha', 'Beta']
            command MAIN = {
                let message = 'Type {item}'
                values.Select(message)
            }
            """, cancellationToken: CancellationToken.None));

        Assert.That((await result.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[Type Alpha, Type Beta]"));
    }

    [Test]
    public async Task DirectModuleEvaluationSupportsNamedTemplates()
    {
        var evaluator = new Evaluator();
        await evaluator.EvalModuleAsync(CopParser.Parse("""
            let message = 'Type {item}'
            command MAIN = ['Alpha', 'Beta'].Select(message)
            """), cancellationToken: CancellationToken.None);

        Assert.That((await (await evaluator.RunCommandAsync("MAIN", cancellationToken: CancellationToken.None)).DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("[Type Alpha, Type Beta]"));
    }

    [Test]
    public async Task LexicallyBoundItemIsNotReplaced()
    {
        var evaluator = new Evaluator();
        evaluator.GlobalEnvironment.Define("item", new CopString("Captured"));
        var module = CopParser.Parse("let message = 'Type {item}'");
        await evaluator.EvalLetBindingsAsync(module, cancellationToken: CancellationToken.None);
        var caller = evaluator.GlobalEnvironment.Extend();
        caller.Define("item", new CopString("Caller"));

        var value = (await evaluator.EvalAsync(new Cop.Lang.Ast.IdentifierExpr("message"), caller, cancellationToken: CancellationToken.None));

        Assert.That((await value.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("Type Captured"));
    }
}
