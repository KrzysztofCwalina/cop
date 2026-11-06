using NUnit.Framework;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

[TestFixture]
public class SignatureValidationTests
{
    private async Task<CopValue> EvalAsync(string source, ForeignFunctionRegistry? ffi = null, CancellationToken cancellationToken = default)
    {
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        await evaluator.EvalModuleAsync(module, cancellationToken: cancellationToken);
        return (await evaluator.RunCommandAsync("main", cancellationToken: cancellationToken));
    }

    // ========================================================================
    // Return Type Validation
    // ========================================================================

    [Test]
    public async Task FunctionReturningCorrectType_Passes()
    {
        // String function returning a string
        var result = (await EvalAsync(@"
function greet(name : string) : string => 'hello ' + name
command main = greet('world')", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("hello world"));
    }

    [Test]
    public void FunctionReturningWrongType_Throws()
    {
        // Declares : string but body returns an int
        var ex = Assert.ThrowsAsync<CopEvaluationException>(async () => await EvalAsync(@"
function bad() : string => 42
command main = bad()", cancellationToken: CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("declares return type string"));
        Assert.That(ex.Message, Does.Contain("returned int"));
    }

    [Test]
    public async Task FunctionReturningBool_Passes()
    {
        var result = (await EvalAsync(@"
function isPositive(n : int) : bool => n > 0
command main = isPositive(5)", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).IsTrue, Is.True);
    }

    [Test]
    public void FunctionReturningWrongBool_Throws()
    {
        var ex = Assert.ThrowsAsync<CopEvaluationException>(async () => await EvalAsync(@"
function bad() : bool => 'not a bool'
command main = bad()", cancellationToken: CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("declares return type bool"));
        Assert.That(ex.Message, Does.Contain("returned string"));
    }

    [Test]
    public async Task FunctionReturningInt_Passes()
    {
        var result = (await EvalAsync(@"
function double(n : int) : int => n * 2
command main = double(21)", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(42));
    }

    [Test]
    public async Task FunctionReturningCollection_Passes()
    {
        var result = (await EvalAsync(@"
function items() : [object] => [1, 2, 3]
command main = items()", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
    }

    [Test]
    public void FunctionReturningNullWhenTypeDeclared_Throws()
    {
        // Declares : string but the body evaluates to null (undefined variable)
        var ex = Assert.ThrowsAsync<CopEvaluationException>(async () => await EvalAsync(@"
function bad() : string => nic
command main = bad()", cancellationToken: CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("declares return type string"));
        Assert.That(ex.Message, Does.Contain("returned null"));
    }

    [Test]
    public async Task FunctionWithNoReturnType_AcceptsAnything()
    {
        // No return type declared — should accept any value including null
        var result = (await EvalAsync(@"
function anything() => 42
command main = anything()", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
    }

    [Test]
    public async Task FunctionReturningObject_AcceptsAnything()
    {
        // 'object' is the top type — anything passes
        var result = (await EvalAsync(@"
function flex() : object => 42
command main = flex()", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
    }

    // ========================================================================
    // Parameter Type Validation
    // ========================================================================

    [Test]
    public async Task CorrectParameterTypes_Passes()
    {
        var result = (await EvalAsync(@"
function add(a : int, b : int) : int => a + b
command main = add(3, 4)", cancellationToken: CancellationToken.None));
        Assert.That(((CopInt)result).Value, Is.EqualTo(7));
    }

    [Test]
    public void WrongParameterType_Throws()
    {
        var ex = Assert.ThrowsAsync<CopEvaluationException>(async () => await EvalAsync(@"
function double(n : int) : int => n * 2
command main = double('not an int')", cancellationToken: CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("parameter 'n'"));
        Assert.That(ex.Message, Does.Contain("expects int"));
        Assert.That(ex.Message, Does.Contain("got string"));
    }

    [Test]
    public void NullForTypedParameter_Throws()
    {
        var ex = Assert.ThrowsAsync<CopEvaluationException>(async () => await EvalAsync(@"
function greet(name : string) : string => 'hi ' + name
command main = greet(nic)", cancellationToken: CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("parameter 'name'"));
        Assert.That(ex.Message, Does.Contain("expects string"));
        Assert.That(ex.Message, Does.Contain("got null"));
    }

    [Test]
    public async Task ObjectParameter_AcceptsAnything()
    {
        // 'object' typed parameter accepts anything including null
        var result = (await EvalAsync(@"
function wrap(value : object) : object => value
command main = wrap(42)", cancellationToken: CancellationToken.None));
        Assert.That(((CopInt)result).Value, Is.EqualTo(42));
    }

    [Test]
    public async Task UntypedParameter_AcceptsAnything()
    {
        // No type on parameter — no validation
        var result = (await EvalAsync(@"
function echo(x) => x
command main = echo('hello')", cancellationToken: CancellationToken.None));
        Assert.That(((CopString)result).Value, Is.EqualTo("hello"));
    }

    // ========================================================================
    // Arity Validation
    // ========================================================================

    [Test]
    public void TooManyArguments_Throws()
    {
        var ex = Assert.ThrowsAsync<CopEvaluationException>(async () => await EvalAsync(@"
function single(x : int) : int => x
command main = single(1, 2, 3)", cancellationToken: CancellationToken.None));
        Assert.That(ex!.Message, Does.Contain("expects 1 argument(s)"));
        Assert.That(ex.Message, Does.Contain("got 3"));
    }

    [Test]
    public async Task CorrectArity_Passes()
    {
        var result = (await EvalAsync(@"
function pair(a : int, b : int) : int => a + b
command main = pair(10, 20)", cancellationToken: CancellationToken.None));
        Assert.That(((CopInt)result).Value, Is.EqualTo(30));
    }

    // ========================================================================
    // Intrinsic Function Validation
    // ========================================================================

    [Test]
    public async Task IntrinsicPrint_AcceptsAnyType()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi, msg => outputs.Add(msg));

        var module = CopParser.Parse(@"
export function print(message : object) => intrinsic
command main = print('hello')", "test.cop");

        var evaluator = new Evaluator(ffi, "test.cop");
        await evaluator.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        await evaluator.RunCommandAsync("main", cancellationToken: CancellationToken.None);

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Is.EqualTo("hello"));
    }

    [Test]
    public async Task IntrinsicPrint_AcceptsInt()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi, msg => outputs.Add(msg));

        var module = CopParser.Parse(@"
export function print(message : object) => intrinsic
command main = print(42)", "test.cop");

        var evaluator = new Evaluator(ffi, "test.cop");
        await evaluator.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        await evaluator.RunCommandAsync("main", cancellationToken: CancellationToken.None);

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Is.EqualTo("42"));
    }

    [Test]
    public void IntrinsicRead_ReturnsString()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        // read() with wrong type should throw parameter error
        var ex = Assert.ThrowsAsync<CopEvaluationException>(async () =>
        {
            var module = CopParser.Parse(@"
export function read(path : string) : string => intrinsic
command main = read(42)", "test.cop");
            var evaluator = new Evaluator(ffi, "test.cop");
            await evaluator.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
            await evaluator.RunCommandAsync("main", cancellationToken: CancellationToken.None);
        });
        Assert.That(ex!.Message, Does.Contain("parameter 'path'"));
        Assert.That(ex.Message, Does.Contain("expects string"));
    }

    [Test]
    public async Task IntrinsicPathMatches_ReturnsBool()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        var module = CopParser.Parse(@"
export function pathMatches(path : string, pattern : string) : bool => intrinsic
command main = pathMatches('foo.cs', '*.cs')", "test.cop");

        var evaluator = new Evaluator(ffi, "test.cop");
        await evaluator.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var result = (await evaluator.RunCommandAsync("main", cancellationToken: CancellationToken.None));

        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).IsTrue, Is.True);
    }

    [Test]
    public async Task IntrinsicText_ReturnsString()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        var module = CopParser.Parse(@"
export function text(value : object) : string => intrinsic
command main = text(42)", "test.cop");

        var evaluator = new Evaluator(ffi, "test.cop");
        await evaluator.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var result = (await evaluator.RunCommandAsync("main", cancellationToken: CancellationToken.None));

        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("42"));
    }

    // ========================================================================
    // TypeValidator.IsCompatible unit tests
    // ========================================================================

    [Test]
    public void IsCompatible_StringType()
    {
        Assert.That(TypeValidator.IsCompatible(new CopString("hi"), new TypeRef("string")), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopInt(42), new TypeRef("string")), Is.False);
        Assert.That(TypeValidator.IsCompatible(CopNull.Instance, new TypeRef("string")), Is.False);
    }

    [Test]
    public void IsCompatible_IntType()
    {
        Assert.That(TypeValidator.IsCompatible(new CopInt(1), new TypeRef("int")), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopString("1"), new TypeRef("int")), Is.False);
        Assert.That(TypeValidator.IsCompatible(CopNull.Instance, new TypeRef("int")), Is.False);
    }

    [Test]
    public void IsCompatible_NumberType_AcceptsIntAndNumber()
    {
        Assert.That(TypeValidator.IsCompatible(new CopInt(1), new TypeRef("float")), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopNumber(1.5), new TypeRef("float")), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopString("1"), new TypeRef("float")), Is.False);
    }

    [Test]
    public void IsCompatible_BoolType()
    {
        Assert.That(TypeValidator.IsCompatible(CopBool.True, new TypeRef("bool")), Is.True);
        Assert.That(TypeValidator.IsCompatible(CopBool.False, new TypeRef("bool")), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopInt(1), new TypeRef("bool")), Is.False);
    }

    [Test]
    public void IsCompatible_ObjectType_AcceptsEverything()
    {
        Assert.That(TypeValidator.IsCompatible(new CopString("x"), new TypeRef("object")), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopInt(1), new TypeRef("object")), Is.True);
        Assert.That(TypeValidator.IsCompatible(CopNull.Instance, new TypeRef("object")), Is.True);
        Assert.That(TypeValidator.IsCompatible(CopBool.True, new TypeRef("object")), Is.True);
    }

    [Test]
    public void IsCompatible_CollectionType()
    {
        var list = new CopList([new CopInt(1)]);
        var lazy = new CopLazyCollection(() => [new CopInt(1)]);
        Assert.That(TypeValidator.IsCompatible(list, new TypeRef("object", IsCollection: true)), Is.True);
        Assert.That(TypeValidator.IsCompatible(lazy, new TypeRef("object", IsCollection: true)), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopString("x"), new TypeRef("object", IsCollection: true)), Is.False);
    }

    [Test]
    public void IsCompatible_NamedType()
    {
        var error = new CopObject(new Dictionary<string, CopValue>
        {
            ["Message"] = new CopString("oops")
        }) { TypeName = "Error" };

        Assert.That(TypeValidator.IsCompatible(error, new TypeRef("Error")), Is.True);
        Assert.That(TypeValidator.IsCompatible(error, new TypeRef("Other")), Is.False);
        Assert.That(TypeValidator.IsCompatible(CopNull.Instance, new TypeRef("Error")), Is.False);
    }

    [Test]
    public void IsCompatible_LambdaType()
    {
        var callable = new CopExternalFunction("dummy", (args, env) => CopNull.Instance);
        Assert.That(TypeValidator.IsCompatible(callable, new TypeRef("lambda")), Is.True);
        Assert.That(TypeValidator.IsCompatible(callable, new TypeRef("function")), Is.True);
        Assert.That(TypeValidator.IsCompatible(new CopInt(1), new TypeRef("lambda")), Is.False);
    }

    // ========================================================================
    // GetActualTypeName
    // ========================================================================

    [Test]
    public void GetActualTypeName_ReturnsCorrectNames()
    {
        Assert.That(TypeValidator.GetActualTypeName(CopNull.Instance), Is.EqualTo("null"));
        Assert.That(TypeValidator.GetActualTypeName(new CopString("x")), Is.EqualTo("string"));
        Assert.That(TypeValidator.GetActualTypeName(new CopInt(1)), Is.EqualTo("int"));
        Assert.That(TypeValidator.GetActualTypeName(new CopNumber(1.5)), Is.EqualTo("float"));
        Assert.That(TypeValidator.GetActualTypeName(CopBool.True), Is.EqualTo("bool"));
        Assert.That(TypeValidator.GetActualTypeName(new CopList([])), Is.EqualTo("collection"));
    }
}
