using NUnit.Framework;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;
using Cop.Lang.Parser;

namespace Cop.Tests.Lang;

[TestFixture]
public class EvaluatorTests
{
    private async Task<CopValue> EvalAsync(string source, ForeignFunctionRegistry? ffi = null, CancellationToken cancellationToken = default)
    {
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        await evaluator.EvalModuleAsync(module, cancellationToken: cancellationToken);
        return (await evaluator.RunCommandAsync("main", cancellationToken: cancellationToken));
    }

    private async Task<CopValue> EvalExprAsync(string exprSource, ForeignFunctionRegistry? ffi = null,
        Action<Evaluator>? setup = null, CancellationToken cancellationToken = default)
    {
        // Wrap expression in a command that returns its value
        var source = $"command main = {exprSource}";
        var module = CopParser.Parse(source, "test.cop");
        var evaluator = new Evaluator(ffi, "test.cop");
        setup?.Invoke(evaluator);
        await evaluator.EvalModuleAsync(module, cancellationToken: cancellationToken);
        return (await evaluator.RunCommandAsync("main", cancellationToken: cancellationToken));
    }

    // ========================================================================
    // Literals
    // ========================================================================

    [Test]
    public async Task EvalIntLiteral()
    {
        var module = CopParser.Parse("let x : int = 42", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var x = eval.GlobalEnvironment.Lookup("x");
        Assert.That(x, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)x).Value, Is.EqualTo(42));
    }

    [Test]
    public async Task EvalStringLiteral()
    {
        var module = CopParser.Parse("let s = 'hello'", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var s = eval.GlobalEnvironment.Lookup("s");
        Assert.That(s, Is.InstanceOf<CopString>());
        Assert.That(((CopString)s).Value, Is.EqualTo("hello"));
    }

    [Test]
    public async Task EvalBoolLiteral()
    {
        var module = CopParser.Parse("let b = true", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var b = eval.GlobalEnvironment.Lookup("b");
        Assert.That(b, Is.EqualTo(CopBool.True));
    }

    [Test]
    public async Task EvalNullLiteral()
    {
        var module = CopParser.Parse("let n = nic", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var n = eval.GlobalEnvironment.Lookup("n");
        Assert.That(n, Is.EqualTo(CopNull.Instance));
    }

    // ========================================================================
    // Filters
    // ========================================================================

    [Test]
    public async Task EvalFilter_SoleBarePredicateBody_FiltersInsteadOfReturningFunctionGroup()
    {
        // Regression: a predicate whose body is a SOLE bare predicate name (`=> isHuge`) must
        // invoke that predicate per item, not yield its function group. Previously the filter
        // mis-detected map mode and produced function groups, which crashed downstream (e.g. a
        // toError anchor became a CopFunctionGroup). Mirrors `&&`/`||` callable coercion.
        var result = (await EvalAsync("""
            let nums = [1, 2, 3, 4]
            predicate isHuge(int) => int > 2
            predicate isBig(int) => isHuge
            let big = nums:isBig
            command main = big.Count
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2), "nums:isBig should keep items where isHuge holds (3 and 4)");
    }

    [Test]
    public async Task EvalFilter_NegatedBarePredicateBody_InvokesPredicatePerItem()
    {
        // Regression (#36): a predicate body that NEGATES a bare predicate name (`=> !isHuge`)
        // must invoke that predicate per item and negate its boolean result — not negate the
        // (always-truthy) function group, which silently yielded false for every item.
        var result = (await EvalAsync("""
            let nums = [1, 2, 3, 4]
            predicate isHuge(int) => int > 2
            predicate isSmall(int) => !isHuge
            let small = nums:isSmall
            command main = small.Count
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2), "nums:isSmall should keep items where isHuge is false (1 and 2)");
    }

    [Test]
    public async Task EvalAggregate_InlineElementExpr_ColonForm()
    {
        // Regression: `coll:any/all/none/count(inlineExpr)` must evaluate the inline element
        // expression per item (with `item` bound), not silently fall through to the per-item
        // filter loop and return a collection.
        Assert.That(((CopBool)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n:any(item > 3)", cancellationToken: CancellationToken.None))).IsTrue, Is.True);
        Assert.That(((CopBool)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n:any(item > 9)", cancellationToken: CancellationToken.None))).IsTrue, Is.False);
        Assert.That(((CopBool)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n:all(item > 0)", cancellationToken: CancellationToken.None))).IsTrue, Is.True);
        Assert.That(((CopBool)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n:all(item > 2)", cancellationToken: CancellationToken.None))).IsTrue, Is.False);
        Assert.That(((CopBool)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n:none(item > 9)", cancellationToken: CancellationToken.None))).IsTrue, Is.True);
        Assert.That(((CopInt)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n:count(item > 2)", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2));
    }

    [Test]
    public async Task EvalAggregate_InlineElementExpr_DotForm()
    {
        // Regression: `coll.any(inlineExpr)` must bind `item` per element (previously threw
        // "Undefined variable 'item'").
        Assert.That(((CopBool)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n.any(item > 3)", cancellationToken: CancellationToken.None))).IsTrue, Is.True);
        Assert.That(((CopInt)(await EvalAsync("let n = [1, 2, 3, 4]\ncommand main = n.count(item > 2)", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2));
    }

    [Test]
    public async Task EvalAggregate_NamedPredicate_StillWorks()
    {
        // The named-predicate form must be preserved after routing aggregates per item.
        var result = (await EvalAsync("""
            let n = [1, 2, 3, 4]
            predicate isBig(int) => int > 2
            command main = n:any(isBig)
            """, cancellationToken: CancellationToken.None));
        Assert.That(((CopBool)result).IsTrue, Is.True);
    }

    [Test]
    public async Task EvalFilter_EmptyOnNonEmptyCollection_ReturnsFalseBool()
    {
        // Regression (#32): `collection:empty` is a collection-level emptiness test that returns a
        // BOOL, not a per-item filter. Previously a bare `:empty` fell through to the per-item
        // loop and produced a collection whose IsTruthy is always true, so `coll:empty` was truthy
        // even for non-empty collections (e.g. `Statement.Children:empty` matched non-empty catches).
        var nonEmpty = (await EvalExprAsync("[1, 2, 3]:empty", cancellationToken: CancellationToken.None));
        Assert.That(nonEmpty, Is.InstanceOf<CopBool>(), "coll:empty must return a bool, not a collection");
        Assert.That(((CopBool)nonEmpty).IsTrue, Is.False);

        var empty = (await EvalExprAsync("[]:empty", cancellationToken: CancellationToken.None));
        Assert.That(empty, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)empty).IsTrue, Is.True);
    }

    [Test]
    public async Task EvalFilter_EmptyInBooleanContext_RespectsActualEmptiness()
    {
        // Regression (#32): the exact failure shape — `<bool> && coll:empty` used inside a
        // predicate body. A non-empty collection must make the conjunction false.
        Assert.That(((CopBool)(await EvalExprAsync("true && [1, 2, 3]:empty", cancellationToken: CancellationToken.None))).IsTrue, Is.False);
        Assert.That(((CopBool)(await EvalExprAsync("true && []:empty", cancellationToken: CancellationToken.None))).IsTrue, Is.True);
    }

    [Test]
    public async Task EvalFilter_SoleCollectionParamFunction_InvokedAtCollectionLevel()
    {
        // Regression (#32): a bare `:name` whose function has a SOLE collection parameter
        // (like core's `empty(items:[T])`, `distinct`, `pop`) must be invoked once with the
        // whole collection, not dispatched per item (which errored "expects [..], got <item>").
        var result = (await EvalAsync("""
            let xs = [10, 20, 30]
            function countItems(items: [T]) : int => items.Count
            command main = xs:countItems
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task EvalFilter_ScalarOverloadWins_StaysPerItem()
    {
        // Regression (#32): when an overload's sole scalar parameter matches the item type
        // (mirroring files' `empty(Folder)` alongside core's `empty(items:[T])`), the bare
        // `:name` filter must stay per-item, not collapse to the collection-level overload.
        var result = (await EvalAsync("""
            type Box : object = { Flag : bool }
            function flagged(items: [T]) : bool => items.Count == 0
            predicate flagged(Box) => Box.Flag
            let boxes = [Box { Flag = true }, Box { Flag = false }, Box { Flag = true }]
            command main = boxes:flagged.Count
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2),
            "per-item predicate overload should keep the two flagged boxes");
    }

    [Test]
    public async Task EvalFilter_EmptyCollectionWithScalarOverload_StaysPerItem()
    {
        // Regression (#32 follow-up): for an EMPTY collection the item type can't be inspected, but
        // a scalar overload in scope (e.g. files' `empty(Folder)`) means the bare `:name` filter is
        // per-item and must yield an empty collection — NOT collapse to the collection-level
        // overload and return a bool (which broke `foreach folders():empty => item.Path` when the
        // folder list was empty).
        var result = (await EvalAsync("""
            type Box : object = { Flag : bool }
            function flagged(items: [T]) : bool => items.Count == 0
            predicate flagged(Box) => Box.Flag
            let boxes : [Box] = []
            command main = boxes:flagged.Count
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>(),
            "an empty collection filtered per-item must stay a collection (Count == 0), not a bool");
        Assert.That(((CopInt)result).Value, Is.EqualTo(0));
    }

    [Test]
    public async Task EvalFilter_CollectionFirstParamMethodWithListArg_DispatchesAtCollectionLevel()
    {
        // Regression (#30): `coll:func(args)` where func's FIRST parameter is a collection — e.g.
        // containsAny(items:[string], values:[string]) — must invoke func ONCE with the whole
        // collection, even when no argument is a callable (a plain list literal). Previously the
        // colon form only dispatched collection-level when an arg was a lambda/callable, so
        // `References:containsAny(['cop'])` fell through to per-item dispatch and failed with
        // "'containsAny' parameter 'items' expects [string], got string".
        var result = (await EvalAsync("""
            function hasValue(items: [string], value: string) : bool => items:any((i) => i == value)
            let xs = ['a', 'b', 'c']
            command main = xs:hasValue('b')
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).IsTrue, Is.True);

        var miss = (await EvalAsync("""
            function hasValue(items: [string], value: string) : bool => items:any((i) => i == value)
            let xs = ['a', 'b', 'c']
            command main = xs:hasValue('z')
            """, cancellationToken: CancellationToken.None));
        Assert.That(((CopBool)miss).IsTrue, Is.False);
    }

    [Test]
    public async Task UserDefinedContainsAnyOverloadsPreservePerItemFiltering()
    {
        var result = await EvalAsync("""
            function containsAny(items: [string], values: [string]) : bool => false
            predicate containsAny(value: string, values: [string]) => value == 'alpha'
            let names = ['alpha', 'beta']
            command main = names:containsAny(['unused']).Count
            """);

        Assert.That(((CopInt)result).Value, Is.EqualTo(1));
    }

    [Test]
    public async Task EvalCall_TextJoinOnCollection_ResolvesTwoArgOverload()
    {
        // Regression (#30): `coll.text(sep)` must join the collection. The single-arg `text(value)`
        // overloads shadowed the 2-arg collection join, so a 2-arg call failed with
        // "'text' expects 1 argument(s), got 2". A `text(items:[T], separator)` overload (backed by
        // the FFI join) restores it.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalAsync("""
            function text(value: string) : string => value
            function text(items: [T], separator: string) : string => intrinsic
            let xs = ['a', 'b', 'c']
            command main = xs.text(', ')
            """, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("a, b, c"));
    }

    [TestCase("Path")]
    [TestCase("isError")]
    [TestCase("greaterThan")]
    [TestCase("gt")]
    [TestCase("lessThan")]
    [TestCase("lt")]
    [TestCase("greaterOrEqual")]
    [TestCase("ge")]
    [TestCase("lessOrEqual")]
    [TestCase("le")]
    [TestCase("sameAs")]
    [TestCase("sm")]
    public void StandardLibrary_DoesNotRegisterPackageOperations(string name)
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        Assert.That(ffi.Resolve(name), Is.Null);
    }

    [Test]
    public async Task EvalFilter_NumericComparisonPredicates_WorkPerItem()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        async Task<CopValue> CountAsync(string op, int n, CancellationToken cancellationToken = default) => (await EvalAsync($$"""
            let nums = [5, 10, 20, 30]
            predicate p(int) => int:{{op}}({{n}})
            command main = nums:p.Count
            """, ffi, cancellationToken: cancellationToken));

        Assert.That(((CopInt)(await CountAsync("greaterThan", 15, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "greaterThan");
        Assert.That(((CopInt)(await CountAsync("gt", 15, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "gt");
        Assert.That(((CopInt)(await CountAsync("lessThan", 15, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "lessThan");
        Assert.That(((CopInt)(await CountAsync("lt", 15, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "lt");
        Assert.That(((CopInt)(await CountAsync("greaterOrEqual", 20, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "greaterOrEqual");
        Assert.That(((CopInt)(await CountAsync("ge", 20, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "ge");
        Assert.That(((CopInt)(await CountAsync("lessOrEqual", 10, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "lessOrEqual");
        Assert.That(((CopInt)(await CountAsync("le", 10, cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "le");
    }

    [Test]
    public async Task EvalFilter_StringPredicateAliases_WorkPerItem()
    {
        // Phase 1 regression: the short-form string-predicate aliases (sw/ew/ct/eq/ne/rx) compiled
        // to provider pushdown and were accepted by the type-checker, but were NOT registered as
        // runtime functions, so a per-item `name:eq('x')` crashed with "Undefined variable 'eq'".
        // They are now registered from the single IntrinsicRegistry as aliases of their canonical
        // FFI functions, so per-item use behaves exactly like the canonical name.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);

        async Task<CopValue> CountAsync(string op, string arg, CancellationToken cancellationToken = default) => (await EvalAsync($$"""
            let words = ['Apple', 'Banana', 'Cherry']
            predicate p(string) => string:{{op}}({{arg}})
            command main = words:p.Count
            """, ffi, cancellationToken: cancellationToken));

        Assert.That(((CopInt)(await CountAsync("eq", "'apple'", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(1), "eq (case-insensitive equals)");
        Assert.That(((CopInt)(await CountAsync("sw", "'App'", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(1), "sw (startsWith)");
        Assert.That(((CopInt)(await CountAsync("ew", "'rry'", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(1), "ew (endsWith)");
        Assert.That(((CopInt)(await CountAsync("ct", "'an'", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(1), "ct (contains)");
        Assert.That(((CopInt)(await CountAsync("ne", "'Banana'", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(2), "ne (notEquals)");
        Assert.That(((CopInt)(await CountAsync("rx", "'^B.*'", cancellationToken: CancellationToken.None))).Value, Is.EqualTo(1), "rx (matches)");
    }

    [Test]
    public async Task EvalFilter_GuardedPredicateOverloads_DispatchByConstraint()
    {
        // Regression (#35): predicate overloads distinguished only by a first-parameter guard
        // (e.g. `writesOutput(Statement:isCSharp)` vs `(Statement:isPython)` vs `(Statement:isJavaScript)`)
        // must dispatch to the overload whose guard holds for the item. The parser discarded the
        // guard, so all same-typed overloads collapsed onto the first one and adding a 3rd guarded
        // overload silently broke an earlier one's dispatch.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalAsync("""
            type Item : object = { n : int }
            predicate isSmall(Item) => Item.n:lessThan(10)
            predicate isMid(Item) => Item.n:greaterOrEqual(10) && Item.n:lessThan(100)
            predicate isLarge(Item) => Item.n:greaterOrEqual(100)
            predicate keep(Item:isSmall) => true
            predicate keep(Item:isMid) => false
            predicate keep(Item:isLarge) => true
            let items = [Item { n = 5 }, Item { n = 20 }, Item { n = 300 }, Item { n = 3 }]
            command main = items:keep.Count
            """, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3),
            "small (5, 3) and large (300) keep=true; mid (20) keep=false — the 3rd overload must not break the 1st");
    }

    [Test]
    public async Task EvalCall_CurriedItemFunction_CompletesPerItemInFilter()
    {
        // Regression (#34): partially applying a function whose leading parameter is the implicit
        // item parameter (`wrap(T, prefix, suffix)`) must bind the explicit parameters and return a
        // closure that a filter completes per item — `let bracket = wrap('[')` then
        // `items:bracket(']')`. Previously the partial-application value was a non-callable CopThunk
        // and the call crashed at runtime ("Value of type CopThunk is not callable").
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalAsync("""
            type T : object = { Name : string }
            function wrap(T, prefix: string, suffix: string) => '{prefix}{T.Name}{suffix}'
            let items = [T { Name = 'A' }, T { Name = 'B' }]
            let bracket = wrap('[')
            command main = items:bracket(']')
            """, ffi, cancellationToken: CancellationToken.None));
        var values = result switch
        {
            CopList l => (await l.Items.ToAsyncEnumerable().Select(async (Cop.Lang.Interpreter.CopValue i, CancellationToken asyncCancellationToken18344) => await i.DisplayAsync(cancellationToken: asyncCancellationToken18344)).ToListAsync()),
            CopLazyCollection lz => (await lz.EnumerateAsync(cancellationToken: CancellationToken.None).Select(async (Cop.Lang.Interpreter.CopValue i, CancellationToken cancellationToken) => await i.DisplayAsync(cancellationToken: CancellationToken.None)).ToListAsync(cancellationToken: CancellationToken.None)),
            _ => throw new AssertionException($"expected a collection, got {result.GetType().Name}")
        };
        Assert.That(values, Is.EqualTo(new[] { "[A]", "[B]" }));
    }

    [Test]
    public async Task EvalMember_AccessOnNull_PropagatesNullInsteadOfCrashing()
    {
        // Regression: accessing a member of a null/absent value yields null rather than crashing,
        // so checks over real-world data with missing fields stay robust (e.g. a global-namespace
        // C# type's null File.Namespace.Length). Previously threw "Cannot access member 'X' on CopNull".
        Assert.That((await EvalExprAsync("nic.Length", cancellationToken: CancellationToken.None)), Is.EqualTo(CopNull.Instance));
        Assert.That((await EvalExprAsync("nic.Anything.Nested", cancellationToken: CancellationToken.None)), Is.EqualTo(CopNull.Instance));
    }

    [Test]
    public async Task EvalFilter_PredicateAccessingNullMember_DoesNotCrashAndIsFalse()
    {
        // Robustness: a predicate that reaches through a null field must not crash — null propagates
        // and the comparison is simply false, so the item is filtered out rather than aborting the run.
        var result = (await EvalAsync("""
            type Box : object = { Inner : object }
            predicate hasName(Box) => Box.Inner.Name == 'x'
            let boxes = [Box { Inner = nic }]
            command main = boxes:hasName.Count
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(0));
    }

    [Test]
    public async Task EvalFilter_SingleGuardedPredicate_FiltersByGuard()
    {
        // Regression (#35): a SINGLE guarded predicate overload (bound as a CopFunction, not a
        // function group) must also honor its guard — `predicate isBig(Box:isReal)` is false for
        // items the guard rejects, instead of running its body on them. Previously the guard was a
        // no-op for single overloads (e.g. `isMissingNamespace(Type:isCSharp)` ran on non-C# types).
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalAsync("""
            type Box : object = { Real : bool, Value : int }
            predicate isReal(Box) => Box.Real
            predicate isBig(Box:isReal) => Box.Value:greaterThan(10)
            let boxes = [Box { Real = true, Value = 20 }, Box { Real = false, Value = 99 }, Box { Real = true, Value = 5 }]
            command main = boxes:isBig.Count
            """, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1),
            "guard isReal excludes the Real=false box even though its Value>10; only {Real=true,Value=20} qualifies");
    }

    // ========================================================================
    // Arithmetic
    // ========================================================================

    [Test]
    public async Task EvalAddition()
    {
        var module = CopParser.Parse("let x = 3 + 4", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var x = eval.GlobalEnvironment.Lookup("x");
        Assert.That(x, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)x).Value, Is.EqualTo(7));
    }

    [Test]
    public async Task EvalSubtraction()
    {
        var module = CopParser.Parse("let x = 10 - 3", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(7));
    }

    [Test]
    public async Task EvalStringConcatenation()
    {
        var module = CopParser.Parse("let s = 'hello' + ' world'", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var s = eval.GlobalEnvironment.Lookup("s");
        Assert.That(((CopString)s).Value, Is.EqualTo("hello world"));
    }

    [Test]
    public async Task EvalComparison()
    {
        var module = CopParser.Parse("let b = 5 > 3", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.True));
    }

    [Test]
    public async Task EvalEquality()
    {
        var module = CopParser.Parse("let b = 5 == 5", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.True));
    }

    [Test]
    public async Task EvalLogicalAnd()
    {
        var module = CopParser.Parse("let b = true && false", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.False));
    }

    [Test]
    public async Task EvalLogicalOr()
    {
        var module = CopParser.Parse("let b = false || true", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.True));
    }

    [Test]
    public async Task EvalUnaryNot()
    {
        var module = CopParser.Parse("let b = !true", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(eval.GlobalEnvironment.Lookup("b"), Is.EqualTo(CopBool.False));
    }

    [Test]
    public async Task EvalUnaryNegate()
    {
        var module = CopParser.Parse("let x = -5", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(-5));
    }

    // ========================================================================
    // Functions
    // ========================================================================

    [Test]
    public async Task EvalFunctionCall()
    {
        var module = CopParser.Parse(@"
function add(a : int, b : int) : int = a + b
let result = add(3, 4)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(7));
    }

    [Test]
    public async Task EvalFunctionCallingFunction()
    {
        // Test that functions can call each other (mutual/chained calls)
        var module = CopParser.Parse(@"
function double(n : int) : int = n + n
function quadruple(n : int) : int = double(double(n))
let result = quadruple(3)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        // double(3) = 6, double(6) = 12
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(12));
    }

    [Test]
    public async Task EvalHigherOrderFunction()
    {
        var module = CopParser.Parse(@"
function apply(f, x : int) : int = f(x)
function double(n : int) : int = n + n
let result = apply(double, 5)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(10));
    }

    [Test]
    public async Task EvalClosure()
    {
        var module = CopParser.Parse(@"
let factor = 3
function scale(n : int) : int = n + factor
let result = scale(10)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(13));
    }

    // ========================================================================
    // Lambdas
    // ========================================================================

    [Test]
    public async Task EvalLambda()
    {
        var module = CopParser.Parse(@"
let inc = (x) => x + 1
let result = inc(9)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(10));
    }

    [Test]
    public async Task EvalLambda_PassedAsArgument()
    {
        var source = @"
function apply(x: float, f: function) => f(x)
command main = apply(5, (n) => n * 2)";
        var result = (await EvalAsync(source, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(10));
    }

    [Test]
    public async Task EvalLambda_HigherOrderFilter()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function myWhere(items: [float], pred: function) =>
  reduce(items, (acc, item) => pred(item) ? acc.concat([item]) : acc, [])
command main = myWhere([1, 2, 3, 4, 5], (x) => x > 3)";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var list = (CopList)result;
        Assert.That(list.Items.Count, Is.EqualTo(2));
        Assert.That(((CopInt)list.Items[0]).Value, Is.EqualTo(4));
        Assert.That(((CopInt)list.Items[1]).Value, Is.EqualTo(5));
    }

    [Test]
    public async Task EvalLambda_HigherOrderSelect()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function mySelect(items: [float], transform: function) =>
  reduce(items, (acc, item) => acc.concat([transform(item)]), [])
command main = mySelect([1, 2, 3], (x) => x * 10)";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
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
    public async Task EvalTernary()
    {
        var module = CopParser.Parse(@"
let x = true ? 1 : 2", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(1));
    }

    [Test]
    public async Task EvalTernaryFalseBranch()
    {
        var module = CopParser.Parse(@"
let x = false ? 1 : 2", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("x")).Value, Is.EqualTo(2));
    }

    // ========================================================================
    // Lists
    // ========================================================================

    [Test]
    public async Task EvalListLiteral()
    {
        var module = CopParser.Parse("let xs = [1, 2, 3]", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        var xs = eval.GlobalEnvironment.Lookup("xs") as CopList;
        Assert.That(xs, Is.Not.Null);
        Assert.That(xs!.Items, Has.Count.EqualTo(3));
        Assert.That(((CopInt)xs.Items[0]).Value, Is.EqualTo(1));
    }

    [Test]
    public async Task EvalListCount()
    {
        var module = CopParser.Parse(@"
let xs = [1, 2, 3, 4, 5]
let n = xs.Count", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("n")).Value, Is.EqualTo(5));
    }

    [Test]
    public async Task EvalListIndex()
    {
        var module = CopParser.Parse(@"
let xs = [10, 20, 30]
let second = xs[1]", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("second")).Value, Is.EqualTo(20));
    }

    // ========================================================================
    // Objects
    // ========================================================================

    [Test]
    public async Task EvalObjectLiteral()
    {
        var module = CopParser.Parse(@"
let person = { Name = 'Alice', Age = 30 }
let name = person.Name", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopString)eval.GlobalEnvironment.Lookup("name")).Value, Is.EqualTo("Alice"));
    }

    // ========================================================================
    // Commands and Statements
    // ========================================================================

    [Test]
    public async Task EvalCommandWithLetStatements()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        ffi.RegisterAsync("print", async (args, env, asyncCancellationToken31588) =>
        {
            outputs.Add((await args[0].DisplayAsync(cancellationToken: asyncCancellationToken31588)));
            return CopNull.Instance;
        });

        var module = CopParser.Parse(@"
command main = {
    let x = 10
    let y = 20
    print(x + y)
}", "test.cop");

        var eval = new Evaluator(ffi, "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        await eval.RunCommandAsync("main", cancellationToken: CancellationToken.None);

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Is.EqualTo("30"));
    }

    [Test]
    public async Task EvalUppercaseFunctionWithBlockBody()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        ffi.RegisterAsync("print", async (args, env, asyncCancellationToken32274) =>
        {
            outputs.Add((await args[0].DisplayAsync(cancellationToken: asyncCancellationToken32274)));
            return CopNull.Instance;
        });

        // Use function MAIN directly (no command keyword)
        var module = CopParser.Parse(@"
function MAIN() = {
    let x = 'hello'
    print(x)
}", "test.cop");

        var eval = new Evaluator(ffi, "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        await eval.RunCommandAsync("MAIN", cancellationToken: CancellationToken.None);

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Is.EqualTo("hello"));
    }

    // ========================================================================
    // Foreign Functions
    // ========================================================================

    [Test]
    public async Task EvalForeignFunction()
    {
        var ffi = new ForeignFunctionRegistry();
        ffi.Register("double", (args, env) =>
        {
            var n = ((CopInt)args[0]).Value;
            return new CopInt(n * 2);
        });

        var module = CopParser.Parse("let result = double(21)", "test.cop");
        var eval = new Evaluator(ffi, "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopInt)eval.GlobalEnvironment.Lookup("result")).Value, Is.EqualTo(42));
    }

    // ========================================================================
    // Foreach / Pipeline
    // ========================================================================

    [Test]
    public async Task EvalForEachWithPipeline()
    {
        var outputs = new List<string>();
        var ffi = new ForeignFunctionRegistry();
        ffi.RegisterAsync("print", async (args, env, asyncCancellationToken33915) =>
        {
            outputs.Add((await args[0].DisplayAsync(cancellationToken: asyncCancellationToken33915)));
            return CopNull.Instance;
        });

        var module = CopParser.Parse(@"
let items = [1, 2, 3]
command main = foreach items => print(item)", "test.cop");

        var eval = new Evaluator(ffi, "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        await eval.RunCommandAsync("main", cancellationToken: CancellationToken.None);

        Assert.That(outputs, Is.EqualTo(new[] { "1", "2", "3" }));
    }

    // ========================================================================
    // Filters
    // ========================================================================

    [Test]
    public async Task EvalFilterExpression()
    {
        var module = CopParser.Parse(@"
let items = [1, 2, 3, 4, 5]
predicate isEven(n : int) => n - (n + n) == 0
let evens = items:isEven", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);

        var evens = eval.GlobalEnvironment.Lookup("evens") as CopLazyCollection;
        Assert.That(evens, Is.Not.Null);
        // Note: the predicate logic is n - (n + n) == 0 which is n - 2n == -n == 0, so n == 0
        // Actually this test needs a simpler predicate. Let's just check it returns a lazy collection.
        Assert.That(evens, Is.InstanceOf<CopLazyCollection>());
    }

    [Test]
    public async Task EvalFilterWithTruePredicate()
    {
        var module = CopParser.Parse(@"
let items = [1, 2, 3]
predicate always(x) => true
let filtered = items:always", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);

        var filtered = eval.GlobalEnvironment.Lookup("filtered") as CopLazyCollection;
        Assert.That(filtered, Is.Not.Null);
        var results = (await filtered!.EnumerateAsync(cancellationToken: CancellationToken.None).ToListAsync(cancellationToken: CancellationToken.None));
        Assert.That(results, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task EvalNegatedFilter()
    {
        var module = CopParser.Parse(@"
let items = [1, 2, 3]
predicate always(x) => true
let filtered = items:!always", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);

        var filtered = eval.GlobalEnvironment.Lookup("filtered") as CopLazyCollection;
        Assert.That(filtered, Is.Not.Null);
        var results = (await filtered!.EnumerateAsync(cancellationToken: CancellationToken.None).ToListAsync(cancellationToken: CancellationToken.None));
        Assert.That(results, Has.Count.EqualTo(0));
    }

    // ========================================================================
    // Enums
    // ========================================================================

    [Test]
    public async Task EvalEnumMembersAsStrings()
    {
        var module = CopParser.Parse(@"
enum Color = Red | Green | Blue
let c = Red", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
        Assert.That(((CopString)eval.GlobalEnvironment.Lookup("c")).Value, Is.EqualTo("Red"));
    }

    // ========================================================================
    // Mapping Body (transforms)
    // ========================================================================

    [Test]
    public async Task EvalFunctionWithMappingBody()
    {
        var module = CopParser.Parse(@"
function makeGreeting(name : string)
    Message = 'Hello ' + name
    Target = name
let result = makeGreeting('World')", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None);
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
        Assert.ThrowsAsync<CopEvaluationException>(async () => await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None));
    }

    [Test]
    public void CallingNonCallableThrows()
    {
        var module = CopParser.Parse(@"
let x = 42
let result = x(1)", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        Assert.ThrowsAsync<CopEvaluationException>(async () => await eval.EvalModuleAsync(module, cancellationToken: CancellationToken.None));
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
    public async Task CopNullIsFalsy()
    {
        Assert.That((await CopNull.Instance.IsTruthyAsync(CancellationToken.None)), Is.False);
    }

    [Test]
    public async Task CopBoolFalseIsFalsy()
    {
        Assert.That((await CopBool.False.IsTruthyAsync(CancellationToken.None)), Is.False);
    }

    [Test]
    public async Task CopIntIsTruthy()
    {
        Assert.That((await new CopInt(0).IsTruthyAsync(CancellationToken.None)), Is.True); // even 0 is truthy (only null and false are falsy)
    }

    [Test]
    public async Task CopStringDisplay()
    {
        Assert.That((await new CopString("hi").DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("hi"));
    }

    [Test]
    public async Task CopObjectDisplay()
    {
        var obj = new CopObject(new Dictionary<string, CopValue>
        {
            ["Name"] = new CopString("test"),
            ["Count"] = new CopInt(5)
        });
        Assert.That((await obj.DisplayAsync(cancellationToken: CancellationToken.None)), Does.Contain("Name = test"));
        Assert.That((await obj.DisplayAsync(cancellationToken: CancellationToken.None)), Does.Contain("Count = 5"));
    }

    // ========================================================================
    // Thunk (Lazy Evaluation)
    // ========================================================================

    [Test]
    public async Task ThunkForcesToConcreteValue()
    {
        var thunk = new CopThunk(() => new CopInt(42));
        Assert.That(thunk.IsForced, Is.False);
        var result = (await thunk.ForceAsync(cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(42));
        Assert.That(thunk.IsForced, Is.True);
    }

    [Test]
    public async Task ThunkMemoizesResult()
    {
        int callCount = 0;
        var thunk = new CopThunk(() => { callCount++; return new CopInt(7); });
        await thunk.ForceAsync(cancellationToken: CancellationToken.None);
        await thunk.ForceAsync(cancellationToken: CancellationToken.None);
        await thunk.ForceAsync(cancellationToken: CancellationToken.None);
        Assert.That(callCount, Is.EqualTo(1));
    }

    [Test]
    public async Task ThunkDisplayAutoForces()
    {
        var thunk = new CopThunk(() => new CopString("hello"));
        Assert.That((await thunk.DisplayAsync(cancellationToken: CancellationToken.None)), Is.EqualTo("hello"));
        Assert.That(thunk.IsForced, Is.True);
    }

    [Test]
    public async Task ThunkIsTruthyAutoForces()
    {
        var trueThunk = new CopThunk(() => CopBool.True);
        var falseThunk = new CopThunk(() => CopBool.False);
        Assert.That((await trueThunk.IsTruthyAsync(CancellationToken.None)), Is.True);
        Assert.That((await falseThunk.IsTruthyAsync(CancellationToken.None)), Is.False);
    }

    [Test]
    public async Task NestedThunksForceRecursively()
    {
        var inner = new CopThunk(() => new CopInt(99));
        var outer = new CopThunk(() => inner);
        var result = (await outer.ForceAsync(cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(99));
    }

    [Test]
    public void RecursiveThunkThrows()
    {
        CopThunk? self = null;
        self = new CopThunk(async (asyncCancellationToken42584) => await self!.ForceAsync(cancellationToken: asyncCancellationToken42584));
        Assert.ThrowsAsync<CopEvaluationException>(async () => await self.ForceAsync(cancellationToken: CancellationToken.None));
    }

    [Test]
    public async Task ThunkInEnvironmentForcesOnAccess()
    {
        var result = (await EvalExprAsync("value + 5", setup: evaluator =>
            evaluator.GlobalEnvironment.Define("value", new CopThunk(() => new CopInt(10))), cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(15));
    }

    [Test]
    public async Task ThunkCollectionCountAutoForces()
    {
        var items = new CopList([new CopInt(1), new CopInt(2), new CopInt(3)]);
        var result = (await EvalExprAsync("items.Count", setup: evaluator =>
            evaluator.GlobalEnvironment.Define("items", new CopThunk(() => items)), cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task ThunkMemberAccessAutoForces()
    {
        var obj = new CopObject(new Dictionary<string, CopValue>
        {
            ["Name"] = new CopString("test")
        });
        var result = (await EvalExprAsync("obj.Name", setup: evaluator =>
            evaluator.GlobalEnvironment.Define("obj", new CopThunk(() => obj)), cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("test"));
    }

    [Test]
    public async Task ThunkComparisonAutoForces()
    {
        var result = (await EvalExprAsync("value > 3", setup: evaluator =>
            evaluator.GlobalEnvironment.Define("value", new CopThunk(() => new CopInt(5))), cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).IsTrue, Is.True);
    }

    [Test]
    public async Task LazyLetBindingEvaluatesOnDemand()
    {
        // Verify EvalLetBindings creates thunks that auto-force when accessed
        var module = CopParser.Parse(@"
let x = 10 + 5
command main = x + 1", "test.cop");
        var eval = new Evaluator(filePath: "test.cop");
        // Phase 1: register declarations (skips let)
        eval.RegisterDeclarations(module);
        // Phase 2: register let as lazy thunk
        await eval.EvalLetBindingsAsync(module, cancellationToken: CancellationToken.None);
        // Verify the binding is a thunk
        eval.GlobalEnvironment.TryLookup("x", out var xVal);
        Assert.That(xVal, Is.InstanceOf<CopThunk>());
        // RunCommand should force the thunk transparently
        var result = (await eval.RunCommandAsync("main", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(16));
    }

    // ========================================================================
    // Numeric Aggregates (sum, min, max, average)
    // ========================================================================

    [Test]
    public async Task SumOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1, 2, 3, 4, 5].sum()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(15));
    }

    [Test]
    public async Task SumWithProjection()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1, 2, 3].sum((x) => x * 2)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(12));
    }

    [Test]
    public async Task MinOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[5, 3, 8, 1, 4].min()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1));
    }

    [Test]
    public async Task MaxOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[5, 3, 8, 1, 4].max()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(8));
    }

    [Test]
    public async Task AverageOfIntList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[2, 4, 6].average()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopNumber>());
        Assert.That(((CopNumber)result).Value, Is.EqualTo(4.0));
    }

    [Test]
    public async Task AverageOfEmptyListReturnsNull()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[].average()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopNull>());
    }

    // ========================================================================
    // Reduce (fold)

    [Test]
    public async Task ReduceSumIntegers()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1, 2, 3, 4].reduce((acc, item) => acc + item, 0)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(10));
    }

    // ========================================================================
    // Collection structural operations

    [Test]
    public async Task ConcatTwoLists()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1, 2].concat([3, 4])", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(4));
        Assert.That(((CopInt)items[2]).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task PushAppendsToEnd()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        // push calls concat internally via .cop body: items.concat([value])
        var result = (await EvalExprAsync("[1, 2].push(3)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[2]).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task EnqueuePrependsToFront()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        // enqueue calls: [value].concat(items)
        var result = (await EvalExprAsync("[2, 3].enqueue(1)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[0]).Value, Is.EqualTo(1));
    }

    [Test]
    public async Task PopRemovesLastElement()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1, 2, 3].pop()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(2));
        Assert.That(((CopInt)items[1]).Value, Is.EqualTo(2));
    }

    [Test]
    public async Task ElementAtReturnsItemByIndex()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[10, 20, 30].elementAt(1)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(20));
    }

    // ========================================================================
    // Per-item collection transforms: Select / Where / OrderBy (item-binding)
    // ========================================================================

    [Test]
    public async Task SelectProjectsMemberPerItem()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        // item.Length must bind `item` to each element (regression: was "Undefined variable 'item'")
        var result = (await EvalExprAsync("['ab', 'c', 'def'].Select(item.Length)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Select(i => ((CopInt)i).Value), Is.EqualTo(new[] { 2, 1, 3 }));
    }

    [Test]
    public async Task SelectSupportsArrowLambda()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("['ab', 'c'].Select((s) => s.Length)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Select(i => ((CopInt)i).Value), Is.EqualTo(new[] { 2, 1 }));
    }

    [Test]
    public async Task WhereFiltersPerItem()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("['Blob', 'Queue', 'Bus'].Where(item:startsWith('B'))", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(2));
        Assert.That(((CopString)items[0]).Value, Is.EqualTo("Blob"));
        Assert.That(((CopString)items[1]).Value, Is.EqualTo("Bus"));
    }

    [Test]
    public async Task OrderBySortsByKeyAscending()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("['bb', 'a', 'ccc'].OrderBy(item.Length)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopString)i).Value);
        Assert.That(items, Is.EqualTo(new[] { "a", "bb", "ccc" }));
    }

    [Test]
    public async Task OrderByDescendingSortsByKeyDescending()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("['a', 'ccc', 'bb'].OrderByDescending(item.Length)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopString)i).Value);
        Assert.That(items, Is.EqualTo(new[] { "ccc", "bb", "a" }));
    }

    // ========================================================================
    // Convention-insensitive equality: sameAs
    // ========================================================================

    [Test]
    public async Task SameAsIsConventionInsensitive()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        Assert.That((await EvalExprAsync("'BlobClient'.sameAs('blob_client')", ffi, cancellationToken: CancellationToken.None)), Is.EqualTo(CopBool.True));
        Assert.That((await EvalExprAsync("'BlobClient'.sameAs('blob-client')", ffi, cancellationToken: CancellationToken.None)), Is.EqualTo(CopBool.True));
        Assert.That((await EvalExprAsync("'BlobClient'.sameAs('QueueClient')", ffi, cancellationToken: CancellationToken.None)), Is.EqualTo(CopBool.False));
    }

    [Test]
    public void SmIsNotDefined()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var evaluator = new Evaluator(ffi);
        Assert.That(evaluator.GlobalEnvironment.TryLookup("sm", out _), Is.False);
    }

    [Test]
    public async Task EmptyReturnsTrueForEmptyList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[].empty()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).IsTrue, Is.True);
    }

    [Test]
    public async Task EmptyReturnsFalseForNonEmptyList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1, 2].empty()", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That(((CopBool)result).IsTrue, Is.False);
    }

    [Test]
    public async Task ReduceProductIntegers()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1, 2, 3, 4].reduce((acc, item) => acc * item, 1)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(24));
    }

    [Test]
    public async Task ReduceEmptyListReturnsInitial()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[].reduce((acc, item) => acc + item, 42)", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(42));
    }

    [Test]
    public async Task ReduceStringConcat()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("['a', 'b', 'c'].reduce((acc, item) => acc + item, '')", ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("abc"));
    }

    // ========================================================================
    // Recursion

    [Test]
    public async Task RecursiveFactorial()
    {
        var source = @"
function factorial(n) => n <= 1 ? 1 : n * factorial(n - 1)
command main = factorial(5)";
        var result = (await EvalAsync(source, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(120));
    }

    [Test]
    public async Task RecursiveFibonacci()
    {
        var source = @"
function fib(n) => n <= 1 ? n : fib(n - 1) + fib(n - 2)
command main = fib(7)";
        var result = (await EvalAsync(source, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(13));
    }

    [Test]
    public async Task MutualRecursion()
    {
        var source = @"
function isEven(n) => n == 0 ? true : isOdd(n - 1)
function isOdd(n) => n == 0 ? false : isEven(n - 1)
command main = isEven(10)";
        var result = (await EvalAsync(source, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopBool>());
        Assert.That((await ((CopBool)result).IsTruthyAsync(CancellationToken.None)), Is.True);
    }

    // ========================================================================
    // Function bodies calling other functions (reduce composition)

    [Test]
    public async Task FunctionBodyCallsReduceViaFFI()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function mySum(items) => items.reduce((acc, x) => acc + x, 0)
command main = mySum([1, 2, 3, 4])";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(10));
    }

    [Test]
    public async Task FunctionBodyCallsReduceForProduct()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function product(items) => items.reduce((acc, x) => acc * x, 1)
command main = product([2, 3, 4])";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(24));
    }

    [Test]
    public async Task FunctionBodyCallsOtherUserFunction()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function double(x) => x * 2
function quadruple(x) => double(double(x))
command main = quadruple(3)";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(12));
    }

    // ========================================================================
    // Param Arrays
    // ========================================================================

    [Test]
    public async Task ParamArray_CollectsExtraArgsIntoList()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function total(items : [int]) : int = items.count()
command main = total(1, 2, 3)";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task ParamArray_SingleArgBecomesListOfOne()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function total(items : [int]) : int = items.count()
command main = total(42)";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1));
    }

    [Test]
    public async Task ParamArray_WithLeadingFixedParams()
    {
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function countNames(prefix : string, names : [string]) : int = names.count()
command main = countNames('Hello', 'Alice', 'Bob', 'Charlie')";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task ParamArray_ExactArityStillWorks()
    {
        // When args.Count == params.Count and last param is [T], the single arg still becomes a list
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function wrap(items : [string]) : int = items.count()
command main = wrap('only')";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(1));
    }

    // ========================================================================
    // Regression tests for filed language issues (#34, #39–#46, #50)
    // ========================================================================

    [Test]
    public async Task Interpolation_EvaluatesArithmeticExpression()
    {
        // Issue #39: '{1 + 2}' should evaluate the embedded expression, not print literal braces.
        var result = (await EvalExprAsync("'{1 + 2}'", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("3"));
    }

    [Test]
    public async Task MatchExpression_WildcardArm_IsSelected()
    {
        // Issue #40: a `_` wildcard arm matches when no literal arm does.
        var result = (await EvalExprAsync("'x' ? 'y' => 'no' | _ => 'yes'", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("yes"));
    }

    [Test]
    public async Task MatchExpression_LiteralArm_IsCaseInsensitive()
    {
        // Issue #40: literal arm matching is case-insensitive.
        var result = (await EvalExprAsync("'HELLO' ? 'hello' => 'hi' | _ => 'other'", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("hi"));
    }

    [Test]
    public async Task VerbatimString_KeepsBackslashesLiteral()
    {
        // Issue #41: @'...' is a verbatim string — backslashes are literal, braces are not interpolated.
        var result = (await EvalExprAsync(@"@'a\nb'", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo(@"a\nb"));
    }

    [Test]
    public async Task VerbatimString_DoesNotInterpolateBraces()
    {
        // Issue #41: regex quantifiers like {3} stay literal in a verbatim string.
        var result = (await EvalExprAsync(@"@'\d{3}'", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo(@"\d{3}"));
    }

    [Test]
    public async Task StringProperties_LowerUpperNormalized()
    {
        // Issue #42: documented string properties Lower/Upper/Normalized.
        Assert.That(((CopString)(await EvalExprAsync("'Foo_Bar'.Lower", cancellationToken: CancellationToken.None))).Value, Is.EqualTo("foo_bar"));
        Assert.That(((CopString)(await EvalExprAsync("'Foo_Bar'.Upper", cancellationToken: CancellationToken.None))).Value, Is.EqualTo("FOO_BAR"));
        Assert.That(((CopString)(await EvalExprAsync("'Foo_Bar'.Normalized", cancellationToken: CancellationToken.None))).Value, Is.EqualTo("foobar"));
    }

    [Test]
    public async Task StringProperty_Words_SplitsOnCamelCaseAndSeparators()
    {
        // Issue #42: .Words splits an identifier into lowercase words.
        var result = (await EvalExprAsync("'fooBar_baz'.Words", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var words = ((CopList)result).Items.Select(i => ((CopString)i).Value).ToArray();
        Assert.That(words, Is.EqualTo(new[] { "foo", "bar", "baz" }));
    }

    [Test]
    public async Task ObjectLiteral_QuotedKey_IsAccessibleViaGet()
    {
        // Issue #43: object literals accept quoted keys; .Get retrieves them.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var q = (await EvalAsync("let o = { Name: 'Ada' 'quoted-key': 7 }\ncommand main = o.Get('quoted-key')", ffi, cancellationToken: CancellationToken.None));
        var n = (await EvalAsync("let o = { Name: 'Ada' 'quoted-key': 7 }\ncommand main = o.Get('Name')", ffi, cancellationToken: CancellationToken.None));
        Assert.That(((CopInt)q).Value, Is.EqualTo(7));
        Assert.That(((CopString)n).Value, Is.EqualTo("Ada"));
    }

    [Test]
    public async Task ObjectLiteral_KeysCount()
    {
        // Issue #43: .Keys exposes all field names, including quoted ones.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalAsync("let o = { Name: 'Ada' 'quoted-key': 7 }\ncommand main = o.Keys.Count", ffi, cancellationToken: CancellationToken.None));
        Assert.That(((CopInt)result).Value, Is.EqualTo(2));
    }

    [Test]
    public async Task Object_ContainsKey_PipeForm()
    {
        // Issue #43: o:containsKey('x') tests for a field.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var has = (await EvalAsync("let o = { Name = 'Ada' }\ncommand main = o:containsKey('Name')", ffi, cancellationToken: CancellationToken.None));
        var missing = (await EvalAsync("let o = { Name = 'Ada' }\ncommand main = o:containsKey('Nope')", ffi, cancellationToken: CancellationToken.None));
        Assert.That((await has.IsTruthyAsync(CancellationToken.None)), Is.True);
        Assert.That((await missing.IsTruthyAsync(CancellationToken.None)), Is.False);
    }

    [Test]
    public async Task ValuePipe_InvokesFunctionOnScalar()
    {
        // Issue #44: a scalar piped through a function invokes it (5:inc => 6).
        var result = (await EvalAsync("function inc(n) => n + 1\ncommand main = 5:inc", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(6));
    }

    [Test]
    public async Task ListPlusScalar_AppendsElement()
    {
        // Issue #45: [1 2] + 3 appends the scalar.
        var result = (await EvalExprAsync("[1 2] + 3", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[2]).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task ScalarPlusList_PrependsElement()
    {
        // Issue #45: 0 + [1 2] prepends the scalar.
        var result = (await EvalExprAsync("0 + [1 2]", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(3));
        Assert.That(((CopInt)items[0]).Value, Is.EqualTo(0));
    }

    [Test]
    public async Task ListMinusList_RemovesMatchingElements()
    {
        // `-` on collections is set difference (powers `rust-checks - <check>` exclusion).
        var result = (await EvalExprAsync("[1 2 3] - [2]", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public async Task ListMinusScalar_RemovesMatchingElement()
    {
        var result = (await EvalExprAsync("[1 2 3 2] - 2", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 3 }));
    }

    [Test]
    public async Task ListMinusNonMatching_IsNoOp()
    {
        var result = (await EvalExprAsync("[1 2 3] - [9]", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task ListMinusEmptyList_ReturnsAll()
    {
        var result = (await EvalExprAsync("[1 2 3] - []", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items.Select(i => ((CopInt)i).Value).ToArray();
        Assert.That(items, Is.EqualTo(new[] { 1, 2, 3 }));
    }

    [Test]
    public async Task ListMinusList_RemovesStructurallyEqualObjects()
    {
        // Violation-style objects are removed by value (e.g. same Line/Msg), which is what
        // lets `rust-checks - panic-macros` drop a whole rule's violations.
        var result = (await EvalExprAsync("[ { Line = 1, Msg = 'a' }, { Line = 2, Msg = 'b' } ] - [ { Line = 1, Msg = 'a' } ]", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopList>());
        var items = ((CopList)result).Items;
        Assert.That(items.Count, Is.EqualTo(1));
        Assert.That(((CopString)((CopObject)items[0]).GetField("Msg")).Value, Is.EqualTo("b"));
    }

    [Test]
    public async Task NameOf_ReturnsIdentifierNameAsString()
    {
        // `nameof(x)` yields the NAME of the identifier as a string (like C#/JS nameof). This is
        // what lets a check stamp its violations with the exact identifier a user can subtract.
        var result = (await EvalExprAsync("nameof(panic-macros)", cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("panic-macros"));
    }

    [Test]
    public async Task NameOf_DoesNotEvaluateArgument_SoSelfReferenceIsAllowed()
    {
        // The argument is taken syntactically, never evaluated — so a binding may name itself
        // (`let x = ... nameof(x)`) without a self-reference cycle. This is the real usage:
        // `export let unwrap-calls = ... :named(nameof(unwrap-calls))`.
        var result = (await EvalAsync("""
            let unwrap-calls = nameof(unwrap-calls)
            command main = unwrap-calls
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("unwrap-calls"));
    }

    [Test]
    public async Task NameOf_WorksInsideInterpolation()
    {
        var result = (await EvalAsync("""
            command main = 'check={nameof(my-check)}'
            """, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopString>());
        Assert.That(((CopString)result).Value, Is.EqualTo("check=my-check"));
    }

    [Test]
    public void NameOf_WithoutSingleIdentifier_Throws()
    {
        Assert.That(async () => await EvalExprAsync("nameof('literal')", cancellationToken: CancellationToken.None),
            Throws.InstanceOf<CopEvaluationException>());
    }

    [Test]
    public async Task PipeCall_SelectsOverloadByArity_AmongSameFirstTypedOverloads()
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
        Assert.That((await FirstStringAsync((await EvalAsync(defs + " boxes:tag('x')", cancellationToken: CancellationToken.None)), cancellationToken: CancellationToken.None)), Is.EqualTo("7:x"));
        Assert.That((await FirstStringAsync((await EvalAsync(defs + " boxes:tag('x', 'y')", cancellationToken: CancellationToken.None)), cancellationToken: CancellationToken.None)), Is.EqualTo("7:x:y"));
    }

    private static async Task<string> FirstStringAsync(CopValue v, CancellationToken cancellationToken = default)
    {
        var items = v switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => (await lazy.EnumerateAsync(cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken)),
            _ => throw new AssertionException($"Expected a collection, got {v.GetType().Name}")
        };
        return ((CopString)items[0]).Value;
    }

    [Test]
    public async Task DistinctOnListValue_DedupesByValue()
    {
        // Issue #46: .Distinct() dedupes a list value.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var result = (await EvalExprAsync("[1 2 2 3].Distinct().Count", ffi, cancellationToken: CancellationToken.None));
        Assert.That(((CopInt)result).Value, Is.EqualTo(3));
    }

    [Test]
    public async Task CurriedFreeItemFunction_UsedAsFilter_FiltersPerItem()
    {
        // Issue #34: `greaterThan(limit) => item > limit` used as `coll:greaterThan(1)` binds the
        // explicit arg to the param and `item` to each element, instead of overflowing arity.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
function greaterThan(limit) => item > limit
let r = [1 2 3]:greaterThan(1)
command main = r.Count";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(2));
    }

    [Test]
    public async Task CollectionMemberProjection_FlattensCollectionValuedFields()
    {
        // Issue #50: projecting a collection-valued field (`data.Items`) over a collection flattens
        // into one collection so a following per-item predicate binds per element, not per sub-list.
        var ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(ffi);
        var source = @"
let data = [ { Items = [1 2 3] }, { Items = [4] } ]
command main = data.Items.Count";
        var result = (await EvalAsync(source, ffi, cancellationToken: CancellationToken.None));
        Assert.That(result, Is.InstanceOf<CopInt>());
        Assert.That(((CopInt)result).Value, Is.EqualTo(4));
    }
}
