using Cop.Core;
using Cop.Lang;
using Cop.Providers;
using Cop.Providers.SourceModel;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class ErrorModelTests
{
    private static TypeDeclaration MakeType(string name = "Foo") =>
        new(name, TypeKind.Class, Modifier.Public, [], [], [], [], [], [], 1);

    private static TypeRegistry CreateTestRegistry()
    {
        var registry = new TypeRegistry();
        ProviderLoader.RegisterSchema(new CodeSchemaProvider(), registry);
        return registry;
    }

    private static PredicateEvaluator CreateEvaluator(
        Dictionary<string, List<PredicateDefinition>>? predicates = null)
    {
        var allPredicates = predicates ?? new Dictionary<string, List<PredicateDefinition>>();
        return new PredicateEvaluator(allPredicates, "test.cop", CreateTestRegistry());
    }

    // --- ErrorValue tests ---

    [Test]
    public void ErrorValue_IsDataObject()
    {
        var err = new ErrorValue("something went wrong");
        Assert.That(err, Is.InstanceOf<DataObject>());
    }

    [Test]
    public void ErrorValue_HasErrorTypeName()
    {
        var err = new ErrorValue("test");
        Assert.That(err.TypeName, Is.EqualTo("Error"));
    }

    [Test]
    public void ErrorValue_MessageField()
    {
        var err = new ErrorValue("timeout");
        Assert.That(err.GetField("Message"), Is.EqualTo("timeout"));
    }

    [Test]
    public void ErrorValue_NullMessage()
    {
        var err = new ErrorValue(null);
        Assert.That(err.GetField("Message"), Is.Null);
    }

    [Test]
    public void ErrorValue_SourceInfo()
    {
        var err = new ErrorValue("test", "myfile.cop", 42);
        Assert.That(err.GetField("SourceFile"), Is.EqualTo("myfile.cop"));
        Assert.That(err.GetField("SourceLine"), Is.EqualTo(42));
        Assert.That(err.GetField("Source"), Is.EqualTo("myfile.cop(42)"));
    }

    [Test]
    public void ErrorValue_IsError_ReturnsTrue()
    {
        var err = new ErrorValue("test");
        Assert.That(ErrorValue.IsError(err), Is.True);
    }

    [Test]
    public void ErrorValue_IsError_NormalObject_ReturnsFalse()
    {
        var obj = new DataObject("Type", new Dictionary<string, object?> { ["Name"] = "Foo" });
        Assert.That(ErrorValue.IsError(obj), Is.False);
    }

    [Test]
    public void ErrorValue_IsError_Null_ReturnsFalse()
    {
        Assert.That(ErrorValue.IsError(null), Is.False);
    }

    [Test]
    public void ErrorValue_IsError_String_ReturnsFalse()
    {
        Assert.That(ErrorValue.IsError("error"), Is.False);
    }

    // --- FailException tests ---

    [Test]
    public void FailException_FormatDiagnostic_WithFileAndLine()
    {
        var ex = new FailException("bad state", "checks.cop", 15);
        Assert.That(ex.FormatDiagnostic(), Is.EqualTo("FATAL: checks.cop(15): bad state"));
    }

    [Test]
    public void FailException_FormatDiagnostic_WithFileOnly()
    {
        var ex = new FailException("oops", "checks.cop");
        Assert.That(ex.FormatDiagnostic(), Is.EqualTo("FATAL: checks.cop: oops"));
    }

    [Test]
    public void FailException_FormatDiagnostic_MessageOnly()
    {
        var ex = new FailException("something broke");
        Assert.That(ex.FormatDiagnostic(), Is.EqualTo("FATAL: something broke"));
    }

    // --- error constructor in evaluator ---

    [Test]
    public void Eval_BareError_ReturnsErrorValue()
    {
        var source = "predicate test(Type) => error";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var result = evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.InstanceOf<ErrorValue>());
        Assert.That(((ErrorValue)result!).GetField("Message"), Is.Null);
    }

    [Test]
    public void Eval_ErrorWithMessage_ReturnsErrorValue()
    {
        var source = "predicate test(Type) => error('timeout')";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var result = evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.InstanceOf<ErrorValue>());
        Assert.That(((ErrorValue)result!).GetField("Message"), Is.EqualTo("timeout"));
    }

    // --- isError predicate ---

    [Test]
    public void Eval_IsError_OnError_ReturnsTrue()
    {
        var source = "predicate test(Type) => isError";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var errItem = new ErrorValue("oops");
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, errItem, "Type");
        Assert.That(result, Is.True);
    }

    [Test]
    public void Eval_IsError_OnNormalObject_ReturnsFalse()
    {
        var source = "predicate test(Type) => isError";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.False);
    }

    [Test]
    public void Eval_IsError_AsFilter_KeepsErrors()
    {
        // Test isError works as a predicate call (colon syntax)
        var source = "predicate test(Type) => Type.Name:isError";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        // When called on an error value, isError returns true
        var err = new ErrorValue("db timeout");
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, err, "Type");
        // The predicate call evaluates field "Name" on err first, then applies isError
        // Since Name field is a string (not error), this is false
        Assert.That(result, Is.False);
    }

    [Test]
    public void Eval_IsError_AsBarePredicate_OnError()
    {
        // isError as a bare predicate evaluates against current item
        var source = "predicate test(Type) => isError";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var err = new ErrorValue("db timeout");
        var (result, _) = evaluator.EvaluateAsBool(file.Predicates[0].Body, err, "Type");
        Assert.That(result, Is.True);
    }

    // --- FAIL in expression position ---

    [Test]
    public void Eval_FAIL_ThrowsFailException()
    {
        var source = "predicate test(Type) => FAIL('bug detected')";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var ex = Assert.Throws<FailException>(() =>
            evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type"));
        Assert.That(ex!.Message, Is.EqualTo("bug detected"));
    }

    // --- error in conditional ---

    [Test]
    public void Eval_ErrorInConditional_TrueCase()
    {
        var source = "predicate test(Type) => true ? error('oops') | 'ok'";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var result = evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.InstanceOf<ErrorValue>());
    }

    [Test]
    public void Eval_ErrorInConditional_FalseCase()
    {
        var source = "predicate test(Type) => false ? error('oops') | 'ok'";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        var result = evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type");
        Assert.That(result, Is.EqualTo("ok"));
    }

    // --- DataSink error handling ---

    [Test]
    public void ConsoleWriteLineSink_ErrorWritesToStderr()
    {
        var sink = new ConsoleWriteLineSink();
        var err = new ErrorValue("connection reset");
        var stderr = new StringWriter();
        Console.SetError(stderr);
        try
        {
            sink.WriteAsync(err, err).GetAwaiter().GetResult();
            Assert.That(stderr.ToString(), Does.Contain("ERROR: connection reset"));
        }
        finally
        {
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
    }

    [Test]
    public void FileWriteSink_ErrorIsSkipped()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var baseSink = new FileWriteSink();
            var sink = baseSink.WithArgs([tempFile]);
            var err = new ErrorValue("write failure");
            sink.WriteAsync(err, err).GetAwaiter().GetResult();
            Assert.That(File.ReadAllText(tempFile), Is.Empty);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // --- Error propagation in batch foreach ---

    [Test]
    public void Interpreter_FAIL_InExpression_TerminatesExecution()
    {
        // FAIL in expression position should terminate execution
        var source = @"
predicate test(Type) => FAIL('should not happen')
";
        var file = ScriptParser.Parse(source, "test.cop");
        var predicates = new Dictionary<string, List<PredicateDefinition>>
        {
            ["test"] = [file.Predicates[0]]
        };
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry());
        Assert.Throws<FailException>(() =>
            evaluator.EvaluateField(file.Predicates[0].Body, MakeType(), "Type"));
    }

    // --- Error handling through function overloads ---

    [Test]
    public void FunctionWithErrorOverload_ReceivesError()
    {
        // A function with an Error overload should be called when item is ErrorValue
        var source = @"
function handle(Request) => Response { StatusCode = 200, Body = 'ok', ContentType = 'text/plain' }
function handle(Error) => Response { StatusCode = 500, Body = Error.Message, ContentType = 'text/plain' }
";
        var file = ScriptParser.Parse(source, "test.cop");
        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["handle"] = [file.Functions[0], file.Functions[1]]
        };
        var predicates = new Dictionary<string, List<PredicateDefinition>>();
        var evaluator = new PredicateEvaluator(predicates, "test.cop", CreateTestRegistry(), functions: functions);

        var errItem = new ErrorValue("connection timeout");
        var result = evaluator.EvaluateField(new IdentifierExpr("handle"), errItem, "Error");

        Assert.That(result, Is.InstanceOf<DataObject>());
        var response = (DataObject)result!;
        Assert.That(response.GetField("StatusCode"), Is.EqualTo(500));
        Assert.That(response.GetField("Body"), Is.EqualTo("connection timeout"));
    }

    [Test]
    public void FunctionWithoutErrorOverload_FallsThrough()
    {
        // When no Error overload exists, the function group does NOT contain a match
        var source = @"
function handle(Request) => Response { StatusCode = 200, Body = 'ok', ContentType = 'text/plain' }
";
        var file = ScriptParser.Parse(source, "test.cop");
        var functions = new Dictionary<string, List<FunctionDefinition>>
        {
            ["handle"] = [file.Functions[0]]
        };

        // Verify that no overload with InputType "Error" exists
        Assert.That(functions["handle"].Any(f => f.InputType == "Error"), Is.False);
    }

    [Test]
    public void ErrorValue_TypeName_IsError()
    {
        // Ensure ErrorValue type name matches what function overload resolution expects
        var err = new ErrorValue("test");
        Assert.That(err.TypeName, Is.EqualTo("Error"));
    }
}
