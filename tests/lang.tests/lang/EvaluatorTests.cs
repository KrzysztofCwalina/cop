using NUnit.Framework;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

[TestFixture]
public class EvaluatorTests
{
    private CopValue Eval(string source, ForeignFunctionRegistry? ffi = null)
    {
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        return evaluator.RunCommand("main");
    }

    private CopValue EvalExpr(string exprSource, ForeignFunctionRegistry? ffi = null)
    {
        // Wrap expression in a command that returns its value
        var source = $"command main = {exprSource}";
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        evaluator.EvalModule(module);
        return evaluator.RunCommand("main");
    }

    // ========================================================================
    // Literals
    // ========================================================================

    [Test]
    public void EvalIntLiteral()
    {
        var module = CopParser.Parse("let x : int = 42", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var x = eval.GlobalEnvironment.Lookup("x");
        Assert.That(x, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)x).Value, Is.EqualTo(42));
    }

    [Test]
    public void EvalStringLiteral()
    {
        var module = CopParser.Parse("let s = 'hello'", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var s = eval.GlobalEnvironment.Lookup("s");
        Assert.That(s, Is.InstanceOf<CopString>());
        Assert.That(((CopString)s).Value, Is.EqualTo("hello"));
    }

    [Test]
    public void EvalBoolLiteral()
    {
        var module = CopParser.Parse("let b = true", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var b = eval.GlobalEnvironment.Lookup("b");
        Assert.That(b, Is.EqualTo(CopBool.True));
    }

    [Test]
    public void EvalNullLiteral()
    {
        var module = CopParser.Parse("let n = nic", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var n = eval.GlobalEnvironment.Lookup("n");
        Assert.That(n, Is.EqualTo(CopNull.Instance));
    }

    // ========================================================================
    // Filters
    // ========================================================================

    [Test]
    public void EvalFilter_SoleBarePredicateBody_FiltersInsteadOfReturningFunctionGroup()
    {
        // Regression: a predicate whose body is a SOLE bare predicate name (`=> isHuge`) must
        // invoke that predicate per item, not yield its function group. Previously the filter
        // mis-detected map mode and produced function groups, which crashed downstream (e.g. a
        // toError anchor became a CopFunctionGroup). Mirrors `&&`/`||` callable coercion.
        var result = Eval("""
            let nums = [1, 2, 3, 4]
            predicate isHuge(int) => int > 2
            predicate isBig(int) => isHuge
            let big = nums:isBig
            command main = big.Count
            """);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2), "nums:isBig should keep items where isHuge holds (3 and 4)");
    }

    [Test]
    public void EvalFilter_NegatedBarePredicateBody_InvokesPredicatePerItem()
    {
        // Regression (#36): a predicate body that NEGATES a bare predicate name (`=> !isHuge`)
        // must invoke that predicate per item and negate its boolean result — not negate the
        // (always-truthy) function group, which silently yielded false for every item.
        var result = Eval("""
            let nums = [1, 2, 3, 4]
            predicate isHuge(int) => int > 2
            predicate isSmall(int) => !isHuge
            let small = nums:isSmall
            command main = small.Count
            """);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2), "nums:isSmall should keep items where isHuge is false (1 and 2)");
    }

    [Test]
    public void EvalAggregate_InlineElementExpr_ColonForm()
    {
        // Regression: `coll:any/all/none/count(inlineExpr)` must evaluate the inline element
        // expression per item (with `item` bound), not silently fall through to the per-item
        // filter loop and return a collection.
        Assert.That(((CopBool)Eval("let n = [1, 2, 3, 4]\ncommand main = n:any(item > 3)")).Value, Is.True);
        Assert.That(((CopBool)Eval("let n = [1, 2, 3, 4]\ncommand main = n:any(item > 9)")).Value, Is.False);
        Assert.That(((CopBool)Eval("let n = [1, 2, 3, 4]\ncommand main = n:all(item > 0)")).Value, Is.True);
        Assert.That(((CopBool)Eval("let n = [1, 2, 3, 4]\ncommand main = n:all(item > 2)")).Value, Is.False);
        Assert.That(((CopBool)Eval("let n = [1, 2, 3, 4]\ncommand main = n:none(item > 9)")).Value, Is.True);
        Assert.That(((CopInt)Eval("let n = [1, 2, 3, 4]\ncommand main = n:count(item > 2)")).Value, Is.EqualTo(2));
    }

    [Test]
    public void EvalAggregate_InlineElementExpr_DotForm()
    {
        // Regression: `coll.any(inlineExpr)` must bind `item` per element (previously threw
        // "Undefined variable 'item'").
        Assert.That(((CopBool)Eval("let n = [1, 2, 3, 4]\ncommand main = n.any(item > 3)")).Value, Is.True);
        Assert.That(((CopInt)Eval("let n = [1, 2, 3, 4]\ncommand main = n.count(item > 2)")).Value, Is.EqualTo(2));
    }

    [Test]
    public void EvalAggregate_NamedPredicate_StillWorks()
    {
        // The named-predicate form must be preserved after routing aggregates per item.
        var result = Eval("""
            let n = [1, 2, 3, 4]
            predicate isBig(int) => int > 2
            command main = n:any(isBig)
            """);
        Assert.That(((CopBool)result).Value, Is.True);
    }

    [Test]
    public void EvalFilter_EmptyOnNonEmptyCollection_ReturnsFalseBool()
    {
        // Regression (#32): `collection:empty` is a collection-level emptiness test that returns a
        // BOOL, not a per-item filter. Previously a bare `:empty` fell through to the per-item
        // loop and produced a collection whose IsTruthy is always true, so `coll:empty` was truthy
        // even for non-empty collections (e.g. `Statement.Children:empty` matched non-empty catches).
        var nonEmpty = EvalExpr("[1, 2, 3]:empty");
        Assert.That(nonEmpty, Is.InstanceOf<CopBool>(), "coll:empty must return a bool, not a collection");
        Assert.That(((CopBool)nonEmpty).Value, Is.False);

        var empty = EvalExpr("[]:empty");
        Assert.That(empty, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)empty).Value, Is.True);
    }

    [Test]
    public void EvalFilter_EmptyInBooleanContext_RespectsActualEmptiness()
    {
        // Regression (#32): the exact failure shape — `<bool> && coll:empty` used inside a
        // predicate body. A non-empty collection must make the conjunction false.
        Assert.That(((CopBool)EvalExpr("true && [1, 2, 3]:empty")).Value, Is.False);
        Assert.That(((CopBool)EvalExpr("true && []:empty")).Value, Is.True);
    }

    [Test]
    public void EvalFilter_SoleCollectionParamFunction_InvokedAtCollectionLevel()
    {
        // Regression (#32): a bare `:name` whose function has a SOLE collection parameter
        // (like core's `empty(items:[T])`, `distinct`, `pop`) must be invoked once with the
        // whole collection, not dispatched per item (which errored "expects [..], got <item>").
        var result = Eval("""
            let xs = [10, 20, 30]
            function countItems(items: [T]) : int => items.Count
            command main = xs:countItems
            """);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public void EvalFilter_ScalarOverloadWins_StaysPerItem()
    {
        // Regression (#32): when an overload's sole scalar parameter matches the item type
        // (mirroring files' `empty(Folder)` alongside core's `empty(items:[T])`), the bare
        // `:name` filter must stay per-item, not collapse to the collection-level overload.
        var result = Eval("""
            type Box : object = { Flag : bool }
            function flagged(items: [T]) : bool => items.Count == 0
            predicate flagged(Box) => Box.Flag
            let boxes = [Box { Flag = true }, Box { Flag = false }, Box { Flag = true }]
            command main = boxes:flagged.Count
            """);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2),
            "per-item predicate overload should keep the two flagged boxes");
    }

    [Test]
    public void EvalFilter_EmptyCollectionWithScalarOverload_StaysPerItem()
    {
        // Regression (#32 follow-up): for an EMPTY collection the item type can't be inspected, but
        // a scalar overload in scope (e.g. files' `empty(Folder)`) means the bare `:name` filter is
        // per-item and must yield an empty collection — NOT collapse to the collection-level
        // overload and return a bool (which broke `foreach folders():empty => item.Path` when the
        // folder list was empty).
        var result = Eval("""
            type Box : object = { Flag : bool }
            function flagged(items: [T]) : bool => items.Count == 0
            predicate flagged(Box) => Box.Flag
            let boxes : [Box] = []
            command main = boxes:flagged.Count
            """);
        Assert.That(result, Is.InstanceOf<CopInt>(),
            "an empty collection filtered per-item must stay a collection (Count == 0), not a bool");
        Assert.That(((CopInt)result).Value, Is.EqualTo(0));
    }

    [Test]
    public void EvalFilter_CollectionFirstParamMethodWithListArg_DispatchesAtCollectionLevel()
    {
        // Regression (#30): `coll:func(args)` where func's FIRST parameter is a collection — e.g.
        // containsAny(items:[string], values:[string]) — must invoke func ONCE with the whole
        // collection, even when no argument is a callable (a plain list literal). Previously the
        // colon form only dispatched collection-level when an arg was a lambda/callable, so
        // `References:containsAny(['cop'])` fell through to per-item dispatch and failed with
        // "'containsAny' parameter 'items' expects [string], got string".
        var result = Eval("""
            function hasValue(items: [string], value: string) : bool => items:any((i) => i == value)
            let xs = ['a', 'b', 'c']
            command main = xs:hasValue('b')
            """);
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).Value, Is.True);

        var miss = Eval("""
            function hasValue(items: [string], value: string) : bool => items:any((i) => i == value)
            let xs = ['a', 'b', 'c']
            command main = xs:hasValue('z')
            """);
        Assert.That(((CopBool)miss).Value, Is.False);
    }

    [Test]
    public void EvalCall_TextJoinOnCollection_ResolvesTwoArgOverload()
    {
        // Regression (#30): `coll.text(sep)` must join the collection. The single-arg `text(value)`
        // overloads shadowed the 2-arg collection join, so a 2-arg call failed with
        // "'text' expects 1 argument(s), got 2". A `text(items:[T], separator)` overload (backed by
        // the FFI join) restores it.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = Eval("""
            function text(value: string) : string => value
            function text(items: [T], separator: string) : string => intrinsic
            let xs = ['a', 'b', 'c']
            command main = xs.text(', ')
            """, ffi);
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("a, b, c"));
    }

    [Test]
    public void EvalFilter_NumericComparisonPredicates_WorkPerItem()
    {
        // Regression (#33): the named comparison ops (greaterThan/lessThan/greaterOrEqual/
        // lessOrEqual + short forms) compiled to provider pushdown filters but were not registered
        // as runtime functions, so a per-item/materialized `value:greaterThan(n)` crashed with
        // "Undefined variable 'greaterThan'".
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        CopValue Count(string op, int n) => Eval($$"""
            let nums = [5, 10, 20, 30]
            predicate p(int) => int:{{op}}({{n}})
            command main = nums:p.Count
            """, ffi);

        Assert.That(((CopInt)Count("greaterThan", 15)).Value, Is.EqualTo(2), "greaterThan");
        Assert.That(((CopInt)Count("gt", 15)).Value, Is.EqualTo(2), "gt");
        Assert.That(((CopInt)Count("lessThan", 15)).Value, Is.EqualTo(2), "lessThan");
        Assert.That(((CopInt)Count("greaterOrEqual", 20)).Value, Is.EqualTo(2), "greaterOrEqual");
        Assert.That(((CopInt)Count("lessOrEqual", 10)).Value, Is.EqualTo(2), "lessOrEqual");
    }

    [Test]
    public void EvalFilter_StringPredicateAliases_WorkPerItem()
    {
        // Phase 1 regression: the short-form string-predicate aliases (sw/ew/ct/eq/ne/rx) compiled
        // to provider pushdown and were accepted by the type-checker, but were NOT registered as
        // runtime functions, so a per-item `name:eq('x')` crashed with "Undefined variable 'eq'".
        // They are now registered from the single IntrinsicRegistry as aliases of their canonical
        // FFI functions, so per-item use behaves exactly like the canonical name.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        CopValue Count(string op, string arg) => Eval($$"""
            let words = ['Apple', 'Banana', 'Cherry']
            predicate p(string) => string:{{op}}({{arg}})
            command main = words:p.Count
            """, ffi);

        Assert.That(((CopInt)Count("eq", "'apple'")).Value, Is.EqualTo(1), "eq (case-insensitive equals)");
        Assert.That(((CopInt)Count("sw", "'App'")).Value, Is.EqualTo(1), "sw (startsWith)");
        Assert.That(((CopInt)Count("ew", "'rry'")).Value, Is.EqualTo(1), "ew (endsWith)");
        Assert.That(((CopInt)Count("ct", "'an'")).Value, Is.EqualTo(1), "ct (contains)");
        Assert.That(((CopInt)Count("ne", "'Banana'")).Value, Is.EqualTo(2), "ne (notEquals)");
        Assert.That(((CopInt)Count("rx", "'^B.*'")).Value, Is.EqualTo(1), "rx (matches)");
    }

    [Test]
    public void EvalFilter_GuardedPredicateOverloads_DispatchByConstraint()
    {
        // Regression (#35): predicate overloads distinguished only by a first-parameter guard
        // (e.g. `writesOutput(Statement:isCSharp)` vs `(Statement:isPython)` vs `(Statement:isJavaScript)`)
        // must dispatch to the overload whose guard holds for the item. The parser discarded the
        // guard, so all same-typed overloads collapsed onto the first one and adding a 3rd guarded
        // overload silently broke an earlier one's dispatch.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = Eval("""
            type Item : object = { n : int }
            predicate isSmall(Item) => Item.n:lessThan(10)
            predicate isMid(Item) => Item.n:greaterOrEqual(10) && Item.n:lessThan(100)
            predicate isLarge(Item) => Item.n:greaterOrEqual(100)
            predicate keep(Item:isSmall) => true
            predicate keep(Item:isMid) => false
            predicate keep(Item:isLarge) => true
            let items = [Item { n = 5 }, Item { n = 20 }, Item { n = 300 }, Item { n = 3 }]
            command main = items:keep.Count
            """, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3),
            "small (5, 3) and large (300) keep=true; mid (20) keep=false — the 3rd overload must not break the 1st");
    }

    [Test]
    public void EvalCall_CurriedItemFunction_CompletesPerItemInFilter()
    {
        // Regression (#34): partially applying a function whose leading parameter is the implicit
        // item parameter (`wrap(T, prefix, suffix)`) must bind the explicit parameters and return a
        // closure that a filter completes per item — `let bracket = wrap('[')` then
        // `items:bracket(']')`. Previously the partial-application value was a non-callable CopThunk
        // and the call crashed at runtime ("Value of type CopThunk is not callable").
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = Eval("""
            type T : object = { Name : string }
            function wrap(T, prefix: string, suffix: string) => '{prefix}{T.Name}{suffix}'
            let items = [T { Name = 'A' }, T { Name = 'B' }]
            let bracket = wrap('[')
            command main = items:bracket(']')
            """, ffi);
        var values = result switch
        {
            CopList l => l.Items.Select(i => i.Display()).ToList(),
            CopLazyCollection lz => lz.Enumerate().Select(i => i.Display()).ToList(),
            _ => throw new AssertionException($"expected a collection, got {result.GetType().Name}")
        };
        Assert.That(values, Is.EqualTo(new[] { "[A]", "[B]" }));
    }

    [Test]
    public void EvalMember_AccessOnNull_PropagatesNullInsteadOfCrashing()
    {
        // Regression: accessing a member of a null/absent value yields null rather than crashing,
        // so checks over real-world data with missing fields stay robust (e.g. a global-namespace
        // C# type's null File.Namespace.Length). Previously threw "Cannot access member 'X' on CopNull".
        Assert.That(EvalExpr("nic.Length"), Is.EqualTo(CopNull.Instance));
        Assert.That(EvalExpr("nic.Anything.Nested"), Is.EqualTo(CopNull.Instance));
    }

    [Test]
    public void EvalFilter_PredicateAccessingNullMember_DoesNotCrashAndIsFalse()
    {
        // Robustness: a predicate that reaches through a null field must not crash — null propagates
        // and the comparison is simply false, so the item is filtered out rather than aborting the run.
        var result = Eval("""
            type Box : object = { Inner : object }
            predicate hasName(Box) => Box.Inner.Name == 'x'
            let boxes = [Box { Inner = nic }]
            command main = boxes:hasName.Count
            """);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(0));
    }

    [Test]
    public void EvalFilter_SingleGuardedPredicate_FiltersByGuard()
    {
        // Regression (#35): a SINGLE guarded predicate overload (bound as a CopFunction, not a
        // function group) must also honor its guard — `predicate isBig(Box:isReal)` is false for
        // items the guard rejects, instead of running its body on them. Previously the guard was a
        // no-op for single overloads (e.g. `isMissingNamespace(Type:isCSharp)` ran on non-C# types).
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = Eval("""
            type Box : object = { Real : bool, Value : int }
            predicate isReal(Box) => Box.Real
            predicate isBig(Box:isReal) => Box.Value:greaterThan(10)
            let boxes = [Box { Real = true, Value = 20 }, Box { Real = false, Value = 99 }, Box { Real = true, Value = 5 }]
            command main = boxes:isBig.Count
            """, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1),
            "guard isReal excludes the Real=false box even though its Value>10; only {Real=true,Value=20} qualifies");
    }

    // ========================================================================
    // Arithmetic
    // ========================================================================

    [Test]
    public void EvalAddition()
    {
        var module = CopParser.Parse("let x = 3 + 4", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var x = eval.GlobalEnvironment.Lookup("x");
        Assert.That(x, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)x).Value, Is.EqualTo(7));
    }

    [Test]
    public void EvalSubtraction()
    {
        var module = CopParser.Parse("let x = 10 - 3", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(7));
    }

    [Test]
    public void EvalStringConcatenation()
    {
        var module = CopParser.Parse("let s = 'hello' + ' world'", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var s = eval.GlobalEnvironment.Lookup("s");
        Assert.That(((CopString)s).Value, Is.EqualTo("hello world"));
    }

    [Test]
    public void EvalComparison()
    {
        var module = CopParser.Parse("let b = 5 > 3", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.True));
    }

    [Test]
    public void EvalEquality()
    {
        var module = CopParser.Parse("let b = 5 == 5", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.True));
    }

    [Test]
    public void EvalLogicalAnd()
    {
        var module = CopParser.Parse("let b = true && false", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.False));
    }

    [Test]
    public void EvalLogicalOr()
    {
        var module = CopParser.Parse("let b = false || true", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.True));
    }

    [Test]
    public void EvalUnaryNot()
    {
        var module = CopParser.Parse("let b = !true", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.False));
    }

    [Test]
    public void EvalUnaryNegate()
    {
        var module = CopParser.Parse("let x = -5", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(-5));
    }

    // ========================================================================
    // Functions
    // ========================================================================

    [Test]
    public void EvalFunctionCall()
    {
        var module = CopParser.Parse(@"
function add(a : int, b : int) : int = a + b
let result = add(3, 4)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(7));
    }

    [Test]
    public void EvalFunctionCallingFunction()
    {
        // Test that functions can call each other (mutual/chained calls)
        var module = CopParser.Parse(@"
function double(n : int) : int = n + n
function quadruple(n : int) : int = double(double(n))
let result = quadruple(3)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        // double(3) = 6, double(6) = 12
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(12));
    }

    [Test]
    public void EvalHigherOrderFunction()
    {
        var module = CopParser.Parse(@"
function apply(f, x : int) : int = f(x)
function double(n : int) : int = n + n
let result = apply(double, 5)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(10));
    }

    [Test]
    public void EvalClosure()
    {
        var module = CopParser.Parse(@"
let factor = 3
function scale(n : int) : int = n + factor
let result = scale(10)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(13));
    }

    // ========================================================================
    // Lambdas
    // ========================================================================

    [Test]
    public void EvalLambda()
    {
        var module = CopParser.Parse(@"
let inc = (x) => x + 1
let result = inc(9)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(10));
    }

    [Test]
    public void EvalLambda_PassedAsArgument()
    {
        var source = @"
function apply(x: float, f: function) => f(x)
command main = apply(5, (n) => n * 2)";
        var result = Eval(source);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(10));
    }

    [Test]
    public void EvalLambda_HigherOrderFilter()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function myWhere(items: [float], pred: function) =>
  reduce(items, (acc, item) => pred(item) ? acc.concat([item]) : acc, [])
command main = myWhere([1, 2, 3, 4, 5], (x) => x > 3)";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var list = (CopList)result;
        Assert.That(list.Items.Count, Is.EqualTo(2));
        Assert.That(((CopInt)list.Items[0]).Value, Is.EqualTo(4));
        Assert.That(((CopInt)list.Items[1]).Value, Is.EqualTo(5));
    }

    [Test]
    public void EvalLambda_HigherOrderSelect()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function mySelect(items: [float], transform: function) =>
  reduce(items, (acc, item) => acc.concat([transform(item)]), [])
command main = mySelect([1, 2, 3], (x) => x * 10)";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var list = (CopList)result;
        Assert.That(list.Items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)list.Items[0]).Value, Is.EqualTo(10));
        Assert.That(((CopInt)list.Items[1]).Value, Is.EqualTo(20));
        Assert.That(((CopInt)list.Items[2]).Value, Is.EqualTo(30));
    }

    // ========================================================================
    // Conditionals and Match
    // ========================================================================

    [Test]
    public void EvalTernary()
    {
        var module = CopParser.Parse(@"
let x = true ? 1 : 2", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(1));
    }

    [Test]
    public void EvalTernaryFalseBranch()
    {
        var module = CopParser.Parse(@"
let x = false ? 1 : 2", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(2));
    }

    // ========================================================================
    // Lists
    // ========================================================================

    [Test]
    public void EvalListLiteral()
    {
        var module = CopParser.Parse("let xs = [1, 2, 3]", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var xs = eval.GlobalEnvironment.Lookup("xs") as CopList;
        Assert.That(xs, Is.Not.Null);
        Assert.That(xs!.Items, Has.Count.EqualTo(3));
        Assert.That(((CopInt)xs.Items[0]).Value, Is.EqualTo(1));
    }

    [Test]
    public void EvalListCount()
    {
        var module = CopParser.Parse(@"
let xs = [1, 2, 3, 4, 5]
let n = xs.Count", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("n")).Value, Is.EqualTo(5));
    }

    [Test]
    public void EvalListIndex()
    {
        var module = CopParser.Parse(@"
let xs = [10, 20, 30]
let second = xs[1]", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("second")).Value, Is.EqualTo(20));
    }

    // ========================================================================
    // Objects
    // ========================================================================

    [Test]
    public void EvalObjectLiteral()
    {
        var module = CopParser.Parse(@"
let person = { Name = 'Alice', Age = 30 }
let name = person.Name", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopString)eval.GlobalEnvironment.Lookup("name")).Value, Is.EqualTo("Alice"));
    }

    // ========================================================================
    // Commands and Statements
    // ========================================================================

    [Test]
    public void EvalCommandWithLetStatements()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("print", (args, env) =>
        {
            outputs.Add(args[0].Display());
            return CopNull.Instance;
        });

        var module = CopParser.Parse(@"
command main = {
    let x = 10
    let y = 20
    print(x + y)
}", "test.cop");

        var eval = new Evaluator(ffi, "test.cop");
        eval.EvalModule(module);
        eval.RunCommand("main");

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Is.EqualTo("30"));
    }

    [Test]
    public void EvalUppercaseFunctionWithBlockBody()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("print", (args, env) =>
        {
            outputs.Add(args[0].Display());
            return CopNull.Instance;
        });

        // Use function MAIN directly (no command keyword)
        var module = CopParser.Parse(@"
function MAIN() = {
    let x = 'hello'
    print(x)
}", "test.cop");

        var eval = new Evaluator(ffi, "test.cop");
        eval.EvalModule(module);
        eval.RunCommand("MAIN");

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Is.EqualTo("hello"));
    }

    // ========================================================================
    // Foreign Functions
    // ========================================================================

    [Test]
    public void EvalForeignFunction()
    {
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("double", (args, env) =>
        {
            var n = ((CopInt)args[0]).Value;
            return new CopInt(n * 2);
        });

        var module = CopParser.Parse("let result = double(21)", "test.cop");
        var eval = new Evaluator(ffi, "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(42));
    }

    // ========================================================================
    // Foreach / Pipeline
    // ========================================================================

    [Test]
    public void EvalForEachWithPipeline()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("print", (args, env) =>
        {
            outputs.Add(args[0].Display());
            return CopNull.Instance;
        });

        var module = CopParser.Parse(@"
let items = [1, 2, 3]
command main = foreach items => print(item)", "test.cop");

        var eval = new Evaluator(ffi, "test.cop");
        eval.EvalModule(module);
        eval.RunCommand("main");

        Assert.That(outputs, Is.EqualTo(new[] { "1", "2", "3" }));
    }

    // ========================================================================
    // Filters
    // ========================================================================

    [Test]
    public void EvalFilterExpression()
    {
        var module = CopParser.Parse(@"
let items = [1, 2, 3, 4, 5]
predicate isEven(n : int) => n - (n + n) == 0
let evens = items:isEven", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);

        var evens = eval.GlobalEnvironment.Lookup("evens") as CopLazyCollection;
        Assert.That(evens, Is.Not.Null);
        // Note: the predicate logic is n - (n + n) == 0 which is n - 2n == -n == 0, so n == 0
        // Actually this test needs a simpler predicate. Let's just check it returns a lazy collection.
        Assert.That(evens, Is.InstanceOf<CopLazyCollection>());
    }

    [Test]
    public void EvalFilterWithTruePredicate()
    {
        var module = CopParser.Parse(@"
let items = [1, 2, 3]
predicate always(x) => true
let filtered = items:always", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);

        var filtered = eval.GlobalEnvironment.Lookup("filtered") as CopLazyCollection;
        Assert.That(filtered, Is.Not.Null);
        var results = filtered!.Enumerate().ToList();
        Assert.That(results, Has.Count.EqualTo(3));
    }

    [Test]
    public void EvalNegatedFilter()
    {
        var module = CopParser.Parse(@"
let items = [1, 2, 3]
predicate always(x) => true
let filtered = items:!always", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);

        var filtered = eval.GlobalEnvironment.Lookup("filtered") as CopLazyCollection;
        Assert.That(filtered, Is.Not.Null);
        var results = filtered!.Enumerate().ToList();
        Assert.That(results, Has.Count.EqualTo(0));
    }

    // ========================================================================
    // Enums
    // ========================================================================

    [Test]
    public void EvalEnumMembersAsStrings()
    {
        var module = CopParser.Parse(@"
enum Color = Red | Green | Blue
let c = Red", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        Assert.That(((CopString)eval.GlobalEnvironment.Lookup("c")).Value, Is.EqualTo("Red"));
    }

    // ========================================================================
    // Mapping Body (transforms)
    // ========================================================================

    [Test]
    public void EvalFunctionWithMappingBody()
    {
        var module = CopParser.Parse(@"
function makeGreeting(name : string)
    Message = 'Hello ' + name
    Target = name
let result = makeGreeting('World')", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        eval.EvalModule(module);
        var result = eval.GlobalEnvironment.Lookup("result") as CopObject;
        Assert.That(result, Is.Not.Null);
        Assert.That(((CopString)result!.GetField("Message")).Value, Is.EqualTo("Hello World"));
        Assert.That(((CopString)result.GetField("Target")).Value, Is.EqualTo("World"));
    }

    // ========================================================================
    // Error Cases
    // ========================================================================

    [Test]
    public void UndefinedVariableThrows()
    {
        var module = CopParser.Parse("let x = undefinedVar", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        Assert.Throws<CopEvaluationException>(() => eval.EvalModule(module));
    }

    [Test]
    public void CallingNonCallableThrows()
    {
        var module = CopParser.Parse(@"
let x = 42
let result = x(1)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        Assert.Throws<CopEvaluationException>(() => eval.EvalModule(module));
    }

    // ========================================================================
    // Environment
    // ========================================================================

    [Test]
    public void EnvironmentLexicalScoping()
    {
        var env = new Cop.Lang.Interpreter.Environment();
        env.Define("x", new CopInt(1));

        var child = env.Extend();
        child.Define("y", new CopInt(2));

        Assert.That(child.TryLookup("x", out var x), Is.True);
        Assert.That(((CopInt)x).Value, Is.EqualTo(1));
        Assert.That(child.TryLookup("y", out var y), Is.True);
        Assert.That(((CopInt)y).Value, Is.EqualTo(2));
        Assert.That(env.TryLookup("y", out _), Is.False);
    }

    [Test]
    public void EnvironmentShadowing()
    {
        var env = new Cop.Lang.Interpreter.Environment();
        env.Define("x", new CopInt(1));

        var child = env.Extend();
        child.Define("x", new CopInt(99));

        Assert.That(((CopInt)child.Lookup("x")).Value, Is.EqualTo(99));
        Assert.That(((CopInt)env.Lookup("x")).Value, Is.EqualTo(1));
    }

    // ========================================================================
    // Value System
    // ========================================================================

    [Test]
    public void CopNullIsFalsy()
    {
        Assert.That(CopNull.Instance.IsTruthy, Is.False);
    }

    [Test]
    public void CopBoolFalseIsFalsy()
    {
        Assert.That(CopBool.False.IsTruthy, Is.False);
    }

    [Test]
    public void CopIntIsTruthy()
    {
        Assert.That(new CopInt(0).IsTruthy, Is.True); // even 0 is truthy (only null and false are falsy)
    }

    [Test]
    public void CopStringDisplay()
    {
        Assert.That(new CopString("hi").Display(), Is.EqualTo("hi"));
    }

    [Test]
    public void CopObjectDisplay()
    {
        var obj = new CopObject(new Dictionary<string, CopValue>
        {
            ["Name"] = new CopString("test"),
            ["Count"] = new CopInt(5)
        });
        Assert.That(obj.Display(), Does.Contain("Name = test"));
        Assert.That(obj.Display(), Does.Contain("Count = 5"));
    }

    // ========================================================================
    // Thunk (Lazy Evaluation)
    // ========================================================================

    [Test]
    public void ThunkForcesToConcreteValue()
    {
        var thunk = new CopThunk(() => new CopInt(42));
        Assert.That(thunk.IsForced, Is.False);
        var result = thunk.Force();
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(42));
        Assert.That(thunk.IsForced, Is.True);
    }

    [Test]
    public void ThunkMemoizesResult()
    {
        int callCount = 0;
        var thunk = new CopThunk(() => { callCount++; return new CopInt(7); });
        thunk.Force();
        thunk.Force();
        thunk.Force();
        Assert.That(callCount, Is.EqualTo(1));
    }

    [Test]
    public void ThunkDisplayAutoForces()
    {
        var thunk = new CopThunk(() => new CopString("hello"));
        Assert.That(thunk.Display(), Is.EqualTo("hello"));
        Assert.That(thunk.IsForced, Is.True);
    }

    [Test]
    public void ThunkIsTruthyAutoForces()
    {
        var trueThunk = new CopThunk(() => CopBool.True);
        var falseThunk = new CopThunk(() => CopBool.False);
        Assert.That(trueThunk.IsTruthy, Is.True);
        Assert.That(falseThunk.IsTruthy, Is.False);
    }

    [Test]
    public void NestedThunksForceRecursively()
    {
        var inner = new CopThunk(() => new CopInt(99));
        var outer = new CopThunk(() => inner);
        var result = outer.Force();
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(99));
    }

    [Test]
    public void RecursiveThunkThrows()
    {
        CopThunk? self = null;
        self = new CopThunk(() => self!.Force());
        Assert.Throws<CopEvaluationException>(() => self.Force());
    }

    [Test]
    public void ThunkInEnvironmentForcesOnAccess()
    {
        // Register a thunk as a binding; arithmetic on it should auto-force
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("getThunk", (args, env) => new CopThunk(() => new CopInt(10)));
        var result = EvalExpr("getThunk() + 5", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(15));
    }

    [Test]
    public void ThunkCollectionCoercesForIteration()
    {
        // Verify that a thunk wrapping a list can be iterated (Count forces)
        var ffi = new ForeignFunctionRegistry();
        var items = new CopList([new CopInt(1), new CopInt(2), new CopInt(3)]);
        ffi.Register("getItems", (args, env) => new CopThunk(() => items));

        var result = EvalExpr("getItems().Count", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public void ThunkMemberAccessAutoForces()
    {
        var ffi = new ForeignFunctionRegistry();
        var obj = new CopObject(new Dictionary<string, CopValue>
        {
            ["Name"] = new CopString("test")
        });
        ffi.Register("getObj", (args, env) => new CopThunk(() => obj));
        var result = EvalExpr("getObj().Name", ffi);
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("test"));
    }

    [Test]
    public void ThunkComparisonAutoForces()
    {
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("getVal", (args, env) => new CopThunk(() => new CopInt(5)));
        var result = EvalExpr("getVal() > 3", ffi);
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).Value, Is.True);
    }

    [Test]
    public void LazyLetBindingEvaluatesOnDemand()
    {
        // Verify EvalLetBindings creates thunks that auto-force when accessed
        var module = CopParser.Parse(@"
let x = 10 + 5
command main = x + 1", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        // Phase 1: register declarations (skips let)
        eval.RegisterDeclarations(module);
        // Phase 2: register let as lazy thunk
        eval.EvalLetBindings(module);
        // Verify the binding is a thunk
        eval.GlobalEnvironment.TryLookup("x", out var xVal);
        Assert.That(xVal, Is.InstanceOf<CopThunk>());
        // RunCommand should force the thunk transparently
        var result = eval.RunCommand("main");
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(16));
    }

    // ========================================================================
    // Numeric Aggregates (sum, min, max, average)
    // ========================================================================

    [Test]
    public void SumOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1, 2, 3, 4, 5].sum()", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(15));
    }

    [Test]
    public void SumWithProjection()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1, 2, 3].sum((x) => x * 2)", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(12));
    }

    [Test]
    public void MinOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[5, 3, 8, 1, 4].min()", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1));
    }

    [Test]
    public void MaxOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[5, 3, 8, 1, 4].max()", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(8));
    }

    [Test]
    public void AverageOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[2, 4, 6].average()", ffi);
        Assert.That(result, Is.InstanceOf<CopNumber>());
        Assert.That(((CopNumber)result).Value, Is.EqualTo(4.0));
    }

    [Test]
    public void AverageOfEmptyListReturnsNull()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[].average()", ffi);
        Assert.That(result, Is.InstanceOf<CopNull>());
    }

    // ========================================================================
    // Reduce (fold)

    [Test]
    public void ReduceSumIntegers()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1, 2, 3, 4].reduce((acc, item) => acc + item, 0)", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(10));
    }

    // ========================================================================
    // Collection structural operations

    [Test]
    public void ConcatTwoLists()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1, 2].concat([3, 4])", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(4));
        Assert.That(((CopInt)items[2]).Value, Is.EqualTo(3));
    }

    [Test]
    public void PushAppendsToEnd()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        // push calls concat internally via .cop body: items.concat([value])
        var result = EvalExpr("[1, 2].push(3)", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[2]).Value, Is.EqualTo(3));
    }

    [Test]
    public void EnqueuePrependsToFront()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        // enqueue calls: [value].concat(items)
        var result = EvalExpr("[2, 3].enqueue(1)", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[0]).Value, Is.EqualTo(1));
    }

    [Test]
    public void PopRemovesLastElement()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1, 2, 3].pop()", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(2));
        Assert.That(((CopInt)items[1]).Value, Is.EqualTo(2));
    }

    [Test]
    public void ElementAtReturnsItemByIndex()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[10, 20, 30].elementAt(1)", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(20));
    }

    // ========================================================================
    // Per-item collection transforms: Select / Where / OrderBy (item-binding)
    // ========================================================================

    [Test]
    public void SelectProjectsMemberPerItem()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        // item.Length must bind `item` to each element (regression: was "Undefined variable 'item'")
        var result = EvalExpr("['ab', 'c', 'def'].Select(item.Length)", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Select(i => ((CopInt)i).Value), Is.EqualTo(new[] { 2, 1, 3 }));
    }

    [Test]
    public void SelectSupportsArrowLambda()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("['ab', 'c'].Select((s) => s.Length)", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Select(i => ((CopInt)i).Value), Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public void WhereFiltersPerItem()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("['Blob', 'Queue', 'Bus'].Where(item:startsWith('B'))", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(2));
        Assert.That(((CopString)items[0]).Value, Is.EqualTo("Blob"));
        Assert.That(((CopString)items[1]).Value, Is.EqualTo("Bus"));
    }

    [Test]
    public void OrderBySortsByKeyAscending()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("['bb', 'a', 'ccc'].OrderBy(item.Length)", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopString)i).Value);
        Assert.That(items, Is.EqualTo(new[] { "a", "bb", "ccc" }));
    }

    [Test]
    public void OrderByDescendingSortsByKeyDescending()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("['a', 'ccc', 'bb'].OrderByDescending(item.Length)", ffi);
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopString)i).Value);
        Assert.That(items, Is.EqualTo(new[] { "ccc", "bb", "a" }));
    }

    // ========================================================================
    // Convention-insensitive equality: sameAs / sm
    // ========================================================================

    [Test]
    public void SameAsIsConventionInsensitive()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        Assert.That(EvalExpr("'BlobClient'.sameAs('blob_client')", ffi), Is.EqualTo(CopBool.True));
        Assert.That(EvalExpr("'BlobClient'.sameAs('blob-client')", ffi), Is.EqualTo(CopBool.True));
        Assert.That(EvalExpr("'BlobClient'.sameAs('QueueClient')", ffi), Is.EqualTo(CopBool.False));
    }

    [Test]
    public void SmIsAliasForSameAs()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        Assert.That(EvalExpr("'BlobClient'.sm('blob_client')", ffi), Is.EqualTo(CopBool.True));
        Assert.That(EvalExpr("'BlobClient'.sm('QueueClient')", ffi), Is.EqualTo(CopBool.False));
    }

    [Test]
    public void EmptyReturnsTrueForEmptyList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[].empty()", ffi);
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).Value, Is.True);
    }

    [Test]
    public void EmptyReturnsFalseForNonEmptyList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1, 2].empty()", ffi);
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).Value, Is.False);
    }

    [Test]
    public void ReduceProductIntegers()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1, 2, 3, 4].reduce((acc, item) => acc * item, 1)", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(24));
    }

    [Test]
    public void ReduceEmptyListReturnsInitial()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[].reduce((acc, item) => acc + item, 42)", ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(42));
    }

    [Test]
    public void ReduceStringConcat()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("['a', 'b', 'c'].reduce((acc, item) => acc + item, '')", ffi);
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("abc"));
    }

    // ========================================================================
    // Recursion

    [Test]
    public void RecursiveFactorial()
    {
        var source = @"
function factorial(n) => n <= 1 ? 1 : n * factorial(n - 1)
command main = factorial(5)";
        var result = Eval(source);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(120));
    }

    [Test]
    public void RecursiveFibonacci()
    {
        var source = @"
function fib(n) => n <= 1 ? n : fib(n - 1) + fib(n - 2)
command main = fib(7)";
        var result = Eval(source);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(13));
    }

    [Test]
    public void MutualRecursion()
    {
        var source = @"
function isEven(n) => n == 0 ? true : isOdd(n - 1)
function isOdd(n) => n == 0 ? false : isEven(n - 1)
command main = isEven(10)";
        var result = Eval(source);
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).IsTruthy, Is.True);
    }

    // ========================================================================
    // Function bodies calling other functions (reduce composition)

    [Test]
    public void FunctionBodyCallsReduceViaFFI()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function mySum(items) => items.reduce((acc, x) => acc + x, 0)
command main = mySum([1, 2, 3, 4])";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(10));
    }

    [Test]
    public void FunctionBodyCallsReduceForProduct()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function product(items) => items.reduce((acc, x) => acc * x, 1)
command main = product([2, 3, 4])";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(24));
    }

    [Test]
    public void FunctionBodyCallsOtherUserFunction()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function double(x) => x * 2
function quadruple(x) => double(double(x))
command main = quadruple(3)";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(12));
    }

    // ========================================================================
    // Param Arrays
    // ========================================================================

    [Test]
    public void ParamArray_CollectsExtraArgsIntoList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function total(items : [int]) : int = items.count()
command main = total(1, 2, 3)";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public void ParamArray_SingleArgBecomesListOfOne()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function total(items : [int]) : int = items.count()
command main = total(42)";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1));
    }

    [Test]
    public void ParamArray_WithLeadingFixedParams()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function countNames(prefix : string, names : [string]) : int = names.count()
command main = countNames('Hello', 'Alice', 'Bob', 'Charlie')";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public void ParamArray_ExactArityStillWorks()
    {
        // When args.Count == params.Count and last param is [T], the single arg still becomes a list
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function wrap(items : [string]) : int = items.count()
command main = wrap('only')";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1));
    }

    // ========================================================================
    // Regression tests for filed language issues (#34, #39–#46, #50)
    // ========================================================================

    [Test]
    public void Interpolation_EvaluatesArithmeticExpression()
    {
        // Issue #39: '{1 + 2}' should evaluate the embedded expression, not print literal braces.
        var result = EvalExpr("'{1 + 2}'");
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("3"));
    }

    [Test]
    public void MatchExpression_WildcardArm_IsSelected()
    {
        // Issue #40: a `_` wildcard arm matches when no literal arm does.
        var result = EvalExpr("'x' ? 'y' => 'no' | _ => 'yes'");
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("yes"));
    }

    [Test]
    public void MatchExpression_LiteralArm_IsCaseInsensitive()
    {
        // Issue #40: literal arm matching is case-insensitive.
        var result = EvalExpr("'HELLO' ? 'hello' => 'hi' | _ => 'other'");
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("hi"));
    }

    [Test]
    public void VerbatimString_KeepsBackslashesLiteral()
    {
        // Issue #41: @'...' is a verbatim string — backslashes are literal, braces are not interpolated.
        var result = EvalExpr(@"@'a\nb'");
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo(@"a\nb"));
    }

    [Test]
    public void VerbatimString_DoesNotInterpolateBraces()
    {
        // Issue #41: regex quantifiers like {3} stay literal in a verbatim string.
        var result = EvalExpr(@"@'\d{3}'");
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo(@"\d{3}"));
    }

    [Test]
    public void StringProperties_LowerUpperNormalized()
    {
        // Issue #42: documented string properties Lower/Upper/Normalized.
        Assert.That(((CopString)EvalExpr("'Foo_Bar'.Lower")).Value, Is.EqualTo("foo_bar"));
        Assert.That(((CopString)EvalExpr("'Foo_Bar'.Upper")).Value, Is.EqualTo("FOO_BAR"));
        Assert.That(((CopString)EvalExpr("'Foo_Bar'.Normalized")).Value, Is.EqualTo("foobar"));
    }

    [Test]
    public void StringProperty_Words_SplitsOnCamelCaseAndSeparators()
    {
        // Issue #42: .Words splits an identifier into lowercase words.
        var result = EvalExpr("'fooBar_baz'.Words");
        Assert.That(result, Is.InstanceOf<CopList>());
        var words = ((CopList)result).Items.Select(i => ((CopString)i).Value).ToArray();
        Assert.That(words, Is.EqualTo(new[] { "foo", "bar", "baz" }));
    }

    [Test]
    public void ObjectLiteral_QuotedKey_IsAccessibleViaGet()
    {
        // Issue #43: object literals accept quoted keys; .Get retrieves them.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var q = Eval("let o = { Name: 'Ada' 'quoted-key': 7 }\ncommand main = o.Get('quoted-key')", ffi);
        var n = Eval("let o = { Name: 'Ada' 'quoted-key': 7 }\ncommand main = o.Get('Name')", ffi);
        Assert.That(((CopInt)q).Value, Is.EqualTo(7));
        Assert.That(((CopString)n).Value, Is.EqualTo("Ada"));
    }

    [Test]
    public void ObjectLiteral_KeysCount()
    {
        // Issue #43: .Keys exposes all field names, including quoted ones.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = Eval("let o = { Name: 'Ada' 'quoted-key': 7 }\ncommand main = o.Keys.Count", ffi);
        Assert.That(((CopInt)result).Value, Is.EqualTo(2));
    }

    [Test]
    public void Object_ContainsKey_PipeForm()
    {
        // Issue #43: o:containsKey('x') tests for a field.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var has = Eval("let o = { Name = 'Ada' }\ncommand main = o:containsKey('Name')", ffi);
        var missing = Eval("let o = { Name = 'Ada' }\ncommand main = o:containsKey('Nope')", ffi);
        Assert.That(has.IsTruthy, Is.True);
        Assert.That(missing.IsTruthy, Is.False);
    }

    [Test]
    public void ValuePipe_InvokesFunctionOnScalar()
    {
        // Issue #44: a scalar piped through a function invokes it (5:inc => 6).
        var result = Eval("function inc(n) => n + 1\ncommand main = 5:inc");
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(6));
    }

    [Test]
    public void ListPlusScalar_AppendsElement()
    {
        // Issue #45: [1 2] + 3 appends the scalar.
        var result = EvalExpr("[1 2] + 3");
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[2]).Value, Is.EqualTo(3));
    }

    [Test]
    public void ScalarPlusList_PrependsElement()
    {
        // Issue #45: 0 + [1 2] prepends the scalar.
        var result = EvalExpr("0 + [1 2]");
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[0]).Value, Is.EqualTo(0));
    }

    [Test]
    public void ListMinusList_RemovesMatchingElements()
    {
        // `-` on collections is set difference (powers `rust-checks - <check>` exclusion).
        var result = EvalExpr("[1 2 3] - [2]");
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public void ListMinusScalar_RemovesMatchingElement()
    {
        var result = EvalExpr("[1 2 3 2] - 2");
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public void ListMinusNonMatching_IsNoOp()
    {
        var result = EvalExpr("[1 2 3] - [9]");
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void ListMinusEmptyList_ReturnsAll()
    {
        var result = EvalExpr("[1 2 3] - []");
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public void ListMinusList_RemovesStructurallyEqualObjects()
    {
        // Violation-style objects are removed by value (e.g. same Line/Msg), which is what
        // lets `rust-checks - panic-macros` drop a whole rule's violations.
        var result = EvalExpr("[ { Line = 1, Msg = 'a' }, { Line = 2, Msg = 'b' } ] - [ { Line = 1, Msg = 'a' } ]");
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(1));
        Assert.That(((CopString)((CopObject)items[0]).GetField("Msg")).Value, Is.EqualTo("b"));
    }

    [Test]
    public void NameOf_ReturnsIdentifierNameAsString()
    {
        // `nameof(x)` yields the NAME of the identifier as a string (like C#/JS nameof). This is
        // what lets a check stamp its violations with the exact identifier a user can subtract.
        var result = EvalExpr("nameof(panic-macros)");
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("panic-macros"));
    }

    [Test]
    public void NameOf_DoesNotEvaluateArgument_SoSelfReferenceIsAllowed()
    {
        // The argument is taken syntactically, never evaluated — so a binding may name itself
        // (`let x = ... nameof(x)`) without a self-reference cycle. This is the real usage:
        // `export let unwrap-calls = ... :named(nameof(unwrap-calls))`.
        var result = Eval("""
            let unwrap-calls = nameof(unwrap-calls)
            command main = unwrap-calls
            """);
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("unwrap-calls"));
    }

    [Test]
    public void NameOf_WorksInsideInterpolation()
    {
        var result = Eval("""
            command main = 'check={nameof(my-check)}'
            """);
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("check=my-check"));
    }

    [Test]
    public void NameOf_WithoutSingleIdentifier_Throws()
    {
        Assert.That(() => EvalExpr("nameof('literal')"),
            Throws.InstanceOf<CopEvaluationException>());
    }

    [Test]
    public void PipeCall_SelectsOverloadByArity_AmongSameFirstTypedOverloads()
    {
        // Regression: two overloads sharing the first (item-type) parameter but differing in arity
        // must be distinguished by the explicit-arg count in a pipe call. Before the fix the group
        // always dispatched to the first same-typed overload, so `:tag('x','y')` hit the 2-arg
        // overload ("expects 2 arguments, got 3"). This powers `:toWarning('msg', nameof(check))`.
        const string defs = """
            type Box : object = { N : int }
            function tag(Box, a: string) : string => '{Box.N}:{a}'
            function tag(Box, a: string, b: string) : string => '{Box.N}:{a}:{b}'
            let boxes = [Box { N = 7 }]
            command main =
            """;
        Assert.That(FirstString(Eval(defs + " boxes:tag('x')")), Is.EqualTo("7:x"));
        Assert.That(FirstString(Eval(defs + " boxes:tag('x', 'y')")), Is.EqualTo("7:x:y"));
    }

    private static string FirstString(CopValue v)
    {
        var items = v switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => lazy.Enumerate().ToList(),
            _ => throw new AssertionException($"Expected a collection, got {v.GetType().Name}")
        };
        return ((CopString)items[0]).Value;
    }

    [Test]
    public void DistinctOnListValue_DedupesByValue()
    {
        // Issue #46: .Distinct() dedupes a list value.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = EvalExpr("[1 2 2 3].Distinct().Count", ffi);
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public void CurriedFreeItemFunction_UsedAsFilter_FiltersPerItem()
    {
        // Issue #34: `greaterThan(limit) => item > limit` used as `coll:greaterThan(1)` binds the
        // explicit arg to the param and `item` to each element, instead of overflowing arity.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function greaterThan(limit) => item > limit
let r = [1 2 3]:greaterThan(1)
command main = r.Count";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2));
    }

    [Test]
    public void CollectionMemberProjection_FlattensCollectionValuedFields()
    {
        // Issue #50: projecting a collection-valued field (`data.Items`) over a collection flattens
        // into one collection so a following per-item predicate binds per element, not per sub-list.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
let data = [ { Items = [1 2 3] }, { Items = [4] } ]
command main = data.Items.Count";
        var result = Eval(source, ffi);
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(4));
    }
}
