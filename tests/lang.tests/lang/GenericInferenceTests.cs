using NUnit.Framework;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

[TestFixture]
public class GenericInferenceTests
{
    // ========================================================================
    // IsTypeVariable
    // ========================================================================

    [TestCase("T", true)]
    [TestCase("R", true)]
    [TestCase("A", true)]
    [TestCase("Z", true)]
    [TestCase("object", false)]
    [TestCase("int", false)]
    [TestCase("string", false)]
    [TestCase("Type", false)]
    [TestCase("TT", false)]
    [TestCase("", false)]
    [TestCase("a", false)]
    public void IsTypeVariable(string name, bool expected)
    {
        Assert.That(GenericInference.IsTypeVariable(name), Is.EqualTo(expected));
    }

    // ========================================================================
    // HasTypeParameters
    // ========================================================================

    [Test]
    public void HasTypeParameters_GenericFunction_ReturnsTrue()
    {
        var decl = MakeDecl("reduce", [
            new Parameter("items", new TypeRef("T", true)),
            new Parameter("acc", new TypeRef("(R, T) => R")),
            new Parameter("initial", new TypeRef("R"))
        ], new TypeRef("R"));

        Assert.That(GenericInference.HasTypeParameters(decl), Is.True);
    }

    [Test]
    public void HasTypeParameters_NonGenericFunction_ReturnsFalse()
    {
        var decl = MakeDecl("sum", [
            new Parameter("items", new TypeRef("int", true))
        ], new TypeRef("int"));

        Assert.That(GenericInference.HasTypeParameters(decl), Is.False);
    }

    [Test]
    public void HasTypeParameters_ObjectTyped_ReturnsFalse()
    {
        var decl = MakeDecl("where", [
            new Parameter("items", new TypeRef("object", true)),
            new Parameter("condition", new TypeRef("(object) => bool"))
        ], new TypeRef("object", true));

        Assert.That(GenericInference.HasTypeParameters(decl), Is.False);
    }

    // ========================================================================
    // InferBindings
    // ========================================================================

    [Test]
    public void InferBindings_ReduceIntegers()
    {
        var decl = MakeDecl("reduce", [
            new Parameter("items", new TypeRef("T", true)),
            new Parameter("acc", new TypeRef("(R, T) => R")),
            new Parameter("initial", new TypeRef("R"))
        ], new TypeRef("R"));

        var args = new List<CopValue>
        {
            new CopList([new CopInt(1), new CopInt(2), new CopInt(3)]),
            new CopLambda(new LambdaExpr([new Parameter("a"), new Parameter("b")],
                new BinaryExpr(new IdentifierExpr("a"), BinaryOp.Add, new IdentifierExpr("b"))),
                new Cop.Lang.Interpreter.Environment()),
            new CopInt(0)
        };

        var bindings = GenericInference.InferBindings(decl, args);

        Assert.That(bindings["T"], Is.EqualTo("int"));
        Assert.That(bindings["R"], Is.EqualTo("int"));
    }

    [Test]
    public void InferBindings_ReduceStrings()
    {
        var decl = MakeDecl("reduce", [
            new Parameter("items", new TypeRef("T", true)),
            new Parameter("acc", new TypeRef("(R, T) => R")),
            new Parameter("initial", new TypeRef("R"))
        ], new TypeRef("R"));

        var args = new List<CopValue>
        {
            new CopList([new CopString("a"), new CopString("b")]),
            new CopLambda(new LambdaExpr([new Parameter("a"), new Parameter("b")],
                new BinaryExpr(new IdentifierExpr("a"), BinaryOp.Add, new IdentifierExpr("b"))),
                new Cop.Lang.Interpreter.Environment()),
            new CopString("")
        };

        var bindings = GenericInference.InferBindings(decl, args);

        Assert.That(bindings["T"], Is.EqualTo("string"));
        Assert.That(bindings["R"], Is.EqualTo("string"));
    }

    [Test]
    public void InferBindings_MixedTypes_TandR_Different()
    {
        // reduce([int], (string, int) => string, string) → T=int, R=string
        var decl = MakeDecl("reduce", [
            new Parameter("items", new TypeRef("T", true)),
            new Parameter("acc", new TypeRef("(R, T) => R")),
            new Parameter("initial", new TypeRef("R"))
        ], new TypeRef("R"));

        var args = new List<CopValue>
        {
            new CopList([new CopInt(1), new CopInt(2)]),
            new CopLambda(new LambdaExpr([new Parameter("a"), new Parameter("b")],
                new BinaryExpr(new IdentifierExpr("a"), BinaryOp.Add, new IdentifierExpr("b"))),
                new Cop.Lang.Interpreter.Environment()),
            new CopString("start:")
        };

        var bindings = GenericInference.InferBindings(decl, args);

        Assert.That(bindings["T"], Is.EqualTo("int"));
        Assert.That(bindings["R"], Is.EqualTo("string"));
    }

    [Test]
    public void InferBindings_EmptyCollection_FallsBackToObject()
    {
        var decl = MakeDecl("reduce", [
            new Parameter("items", new TypeRef("T", true)),
            new Parameter("acc", new TypeRef("(R, T) => R")),
            new Parameter("initial", new TypeRef("R"))
        ], new TypeRef("R"));

        var args = new List<CopValue>
        {
            new CopList([]),
            new CopLambda(new LambdaExpr([new Parameter("a"), new Parameter("b")],
                new IdentifierExpr("a")),
                new Cop.Lang.Interpreter.Environment()),
            new CopInt(42)
        };

        var bindings = GenericInference.InferBindings(decl, args);

        Assert.That(bindings["T"], Is.EqualTo("object")); // empty list → can't infer
        Assert.That(bindings["R"], Is.EqualTo("int"));
    }

    [Test]
    public void InferBindings_WhereFunction()
    {
        var decl = MakeDecl("where", [
            new Parameter("items", new TypeRef("T", true)),
            new Parameter("condition", new TypeRef("(T) => bool"))
        ], new TypeRef("T", true));

        var args = new List<CopValue>
        {
            new CopList([new CopInt(1), new CopInt(2), new CopInt(3)]),
            new CopLambda(new LambdaExpr([new Parameter("x")],
                new BinaryExpr(new IdentifierExpr("x"), BinaryOp.GreaterThan, new LiteralExpr(1))),
                new Cop.Lang.Interpreter.Environment())
        };

        var bindings = GenericInference.InferBindings(decl, args);

        Assert.That(bindings["T"], Is.EqualTo("int"));
    }

    // ========================================================================
    // SubstituteTypeRef
    // ========================================================================

    [Test]
    public void SubstituteTypeRef_SimpleTypeVar()
    {
        var typeRef = new TypeRef("R");
        var bindings = new Dictionary<string, string> { ["R"] = "int" };

        var result = GenericInference.SubstituteTypeRef(typeRef, bindings);

        Assert.That(result.Name, Is.EqualTo("int"));
        Assert.That(result.IsCollection, Is.False);
    }

    [Test]
    public void SubstituteTypeRef_CollectionTypeVar()
    {
        var typeRef = new TypeRef("T", true);
        var bindings = new Dictionary<string, string> { ["T"] = "string" };

        var result = GenericInference.SubstituteTypeRef(typeRef, bindings);

        Assert.That(result.Name, Is.EqualTo("string"));
        Assert.That(result.IsCollection, Is.True);
    }

    [Test]
    public void SubstituteTypeRef_FunctionType()
    {
        var typeRef = new TypeRef("(R, T) => R");
        var bindings = new Dictionary<string, string> { ["R"] = "int", ["T"] = "string" };

        var result = GenericInference.SubstituteTypeRef(typeRef, bindings);

        Assert.That(result.Name, Is.EqualTo("(int, string) => int"));
    }

    [Test]
    public void SubstituteTypeRef_NoBinding_Unchanged()
    {
        var typeRef = new TypeRef("int");
        var bindings = new Dictionary<string, string> { ["T"] = "string" };

        var result = GenericInference.SubstituteTypeRef(typeRef, bindings);

        Assert.That(result.Name, Is.EqualTo("int"));
    }

    // ========================================================================
    // ResolveReturnType
    // ========================================================================

    [Test]
    public void ResolveReturnType_TypeVar()
    {
        var decl = MakeDecl("reduce", [
            new Parameter("items", new TypeRef("T", true)),
            new Parameter("initial", new TypeRef("R"))
        ], new TypeRef("R"));

        var bindings = new Dictionary<string, string> { ["T"] = "int", ["R"] = "int" };

        var result = GenericInference.ResolveReturnType(decl, bindings);

        Assert.That(result, Is.EqualTo("int"));
    }

    [Test]
    public void ResolveReturnType_CollectionTypeVar()
    {
        var decl = MakeDecl("where", [
            new Parameter("items", new TypeRef("T", true))
        ], new TypeRef("T", true));

        var bindings = new Dictionary<string, string> { ["T"] = "Type" };

        var result = GenericInference.ResolveReturnType(decl, bindings);

        Assert.That(result, Is.EqualTo("[Type]"));
    }

    // ========================================================================
    // End-to-end: reduce via evaluator
    // ========================================================================

    private CopValue EvalExpr(string exprSource)
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = $"command main = {exprSource}";
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        return evaluator.RunCommand("main");
    }

    [Test]
    public void ReduceIntSum_WithGenericDeclaration()
    {
        // Declare reduce with generic types, then use it
        var source = @"
export function reduce(items: [T], accumulator: (R, T) => R, initial: R) : R => intrinsic
command main = [1, 2, 3, 4].reduce((acc, item) => acc + item, 0)
";
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        var result = evaluator.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(10));
    }

    [Test]
    public void ReduceStringConcat_WithGenericDeclaration()
    {
        var source = @"
export function reduce(items: [T], accumulator: (R, T) => R, initial: R) : R => intrinsic
command main = ['a', 'b', 'c'].reduce((acc, item) => acc + item, '')
";
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        var result = evaluator.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("abc"));
    }

    [Test]
    public void WhereWithGenericType_FiltersCorrectly()
    {
        var source = @"
export function reduce(items: [T], accumulator: (R, T) => R, initial: R) : R => intrinsic
export function concat(items: [T], other: [T]) : [T] => intrinsic
export function where(items: [T], condition: (T) => bool) : [T] 
  => reduce(items, (acc, item) => condition(item) ? acc.concat([item]) : acc, [])
command main = [1, 2, 3, 4, 5].where((x) => x > 3)
";
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        var result = evaluator.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopList>());
        var list = (CopList)result;
        Assert.That(list.Items.Count, Is.EqualTo(2));
        Assert.That(((CopInt)list.Items[0]).Value, Is.EqualTo(4));
        Assert.That(((CopInt)list.Items[1]).Value, Is.EqualTo(5));
    }

    [Test]
    public void GenericPush_AppendsItem()
    {
        var source = @"
export function concat(items: [T], other: [T]) : [T] => intrinsic
export function push(items: [T], value: T) : [T] => items.concat([value])
command main = [1, 2, 3].push(4)
";
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        var result = evaluator.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopList>());
        var list = (CopList)result;
        Assert.That(list.Items.Count, Is.EqualTo(4));
        Assert.That(((CopInt)list.Items[3]).Value, Is.EqualTo(4));
    }

    [Test]
    public void GenericReturnType_Propagates()
    {
        // The result of reduce should naturally be typed correctly
        var source = @"
export function reduce(items: [T], accumulator: (R, T) => R, initial: R) : R => intrinsic
let total = [1, 2, 3].reduce((acc, x) => acc + x, 0)
command main = total + 10
";
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        var result = evaluator.RunCommand("main");

        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(16));
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static FunctionDecl MakeDecl(string name, List<Parameter> parms, TypeRef? returnType = null)
    {
        return new FunctionDecl(name, parms, returnType,
            new IntrinsicBody(), IsExported: true);
    }
}
