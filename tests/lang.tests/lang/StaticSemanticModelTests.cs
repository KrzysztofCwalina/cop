using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;
using NUnit.Framework;

namespace Lang.Tests.Lang;

[TestFixture]
public class StaticSemanticModelTests
{
    private static (StaticSemanticModel Model, CallExpr Call) Analyze(string source)
    {
        var module = CopParser.Parse(source, "source.cop");
        return (new([module]), (CallExpr)module.Declarations.OfType<LetDecl>().Last().Value);
    }

    [TestCase("'hello'", "string", 1)]
    [TestCase("123", "int", 2)]
    public void SelectsExactTypedOverload(string argument, string expected, int line)
    {
        var (model, call) = Analyze($"""
            function choose(value : string) : string => value
            function choose(value : int) : int => value
            let result = choose({argument})
            """);
        var selected = model.ResolveCall(call);
        Assert.That(selected.Declaration?.Line, Is.EqualTo(line));
        Assert.That(selected.ParameterTypes.Select(t => t?.Display), Is.EqualTo(new[] { expected }));
        Assert.That(selected.ResultType?.Display, Is.EqualTo(expected));
        Assert.That(selected.Diagnostics, Is.Empty);
    }

    [TestCase("'hello'", "string")]
    [TestCase("123", "int")]
    public void InstantiatesGenericParametersAndResultWithoutRuntimeValues(string argument, string expected)
    {
        var (model, call) = Analyze($"""
            function identity(value : T) : T => value
            let result = identity({argument})
            """);
        var resolved = model.ResolveCall(call);
        Assert.That(resolved.ParameterTypes.Select(t => t?.Display), Is.EqualTo(new[] { expected }));
        Assert.That(resolved.ResultType?.Display, Is.EqualTo(expected));
        Assert.That(model.InferType(call)?.Display, Is.EqualTo(expected));
    }

    [Test]
    public void GenericCollectionParameterPreservesElementBinding()
    {
        var (model, call) = Analyze("""
            function first(values : [T]) : T => values
            let result = first([1, 2])
            """);
        var resolved = model.ResolveCall(call);
        Assert.That(resolved.ParameterTypes.Single()?.Display, Is.EqualTo("[int]"));
        Assert.That(resolved.ResultType?.Display, Is.EqualTo("int"));
    }

    [Test]
    public void UnknownParameterDoesNotEraseIndependentlyKnownResult()
    {
        var (model, call) = Analyze("""
            function opaque(value) : string => 'known'
            let result = opaque(missing)
            """);
        var resolved = model.ResolveCall(call);
        Assert.That(resolved.Declaration?.Name, Is.EqualTo("opaque"));
        Assert.That(resolved.ParameterTypes, Is.EqualTo(new TypeInfo?[] { null }));
        Assert.That(resolved.ResultType?.Display, Is.EqualTo("string"));
    }

    [Test]
    public void InfersSourceBodyAndTerminatesRecursiveFunctions()
    {
        var (model, call) = Analyze("""
            function identity(value : string) => value
            function recursive(value : string) => recursive(value)
            let first = identity('hello')
            let last = recursive('hello')
            """);
        Assert.That(model.InferType(new CallExpr(new IdentifierExpr("identity"), [new LiteralExpr("value")]))?.Display,
            Is.EqualTo("string"));
        Assert.That(model.InferType(call), Is.Null);
    }

    [Test]
    public void UnknownOverloadSubjectIsNotAssignedTheFirstDeclaration()
    {
        var (model, call) = Analyze("""
            function choose(value : string) : string => value
            function choose(value : int) : int => value
            let result = choose(missing)
            """);
        Assert.That(model.ResolveCall(call).Declaration, Is.Null);
        Assert.That(model.ResolveCall(call).Diagnostics, Is.EqualTo(new[]
        {
            "'choose' overload selection requires a known scalar subject."
        }));
    }

    [Test]
    public void DynamicCalleeShadowingDoesNotBindGlobalDeclaration()
    {
        var (model, call) = Analyze("""
            function choose(value : string) : string => value
            let result = choose('hello')
            """);
        var resolved = model.ResolveCall(call, new Dictionary<string, TypeInfo?> { ["choose"] = null });
        Assert.That(resolved.Declaration, Is.Null);
        Assert.That(resolved.ResultType, Is.Null);
    }

    [Test]
    public void HeterogeneousCollectionAndConflictingGenericsStayUnknown()
    {
        var (model, call) = Analyze("""
            function pair(first : T, second : T) : T => first
            let result = pair(1, 'text')
            """);
        Assert.That(model.ResolveCall(call).Declaration, Is.Null);
        Assert.That(model.InferType(new ListExpr([new LiteralExpr(1), new LiteralExpr("text")])), Is.Null);
    }

    [Test]
    public void SameSubjectOverloadsUseArityWithoutInventingSecondParameterDispatch()
    {
        var (model, call) = Analyze("""
            function choose(value : string) : string => value
            function choose(value : string, suffix : int) : int => suffix
            let result = choose('hello', 7)
            """);
        Assert.That(model.ResolveCall(call).Declaration?.Line, Is.EqualTo(2));
        Assert.That(model.ResolveCall(call).ResultType?.Display, Is.EqualTo("int"));
    }

    [Test]
    public void InfersGenericExpressionBodyFromInstantiatedParameters()
    {
        var (model, call) = Analyze("""
            function identity(value : T) => value
            let result = identity('hello')
            """);
        Assert.That(model.ResolveCall(call).ResultType?.Display, Is.EqualTo("string"));
        Assert.That(model.ResolveCall(call).ParameterTypes.Single()?.Display, Is.EqualTo("string"));
    }

    [Test]
    public void UnknownNominalAnnotationDoesNotInventAParameterType()
    {
        var (model, call) = Analyze("""
            function describe(value : Unavailable) : string => 'hello'
            let result = describe(missing)
            """);
        var selected = model.ResolveCall(call);
        Assert.That(selected.ParameterTypes, Is.EqualTo(new TypeInfo?[] { null }));
        Assert.That(selected.ResultType?.Display, Is.EqualTo("string"));
    }
}
