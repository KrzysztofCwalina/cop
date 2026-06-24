namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// A structured exception for evaluation errors with source location.
/// </summary>
public sealed class CopEvaluationException : Exception
{
    public int Line { get; }
    public string? FilePath { get; }

    public CopEvaluationException(string message, int line = 0, string? filePath = null)
        : base(filePath is not null ? $"{filePath}({line}): {message}" : $"line {line}: {message}")
    {
        Line = line;
        FilePath = filePath;
    }
}

/// <summary>
/// Tree-walking evaluator for the Cop language AST.
/// Has ZERO domain knowledge — all external behavior is provided via the FFI registry.
/// Uses lexical environment chains for scoping.
/// </summary>
public sealed class Evaluator
{
    private readonly Environment _globalEnv;
    private readonly ForeignFunctionRegistry _ffi;
    private readonly string? _filePath;
    private Action<string>? _traceLog;

    public Environment GlobalEnvironment => _globalEnv;
    public ForeignFunctionRegistry FFI => _ffi;
    public Action<string>? TraceLog { get => _traceLog; set => _traceLog = value; }
    public TypeRegistry? TypeRegistry { get; set; }

    public Evaluator(ForeignFunctionRegistry? ffi = null, string? filePath = null)
    {
        _ffi = ffi ?? new ForeignFunctionRegistry();
        _globalEnv = new Environment();
        _filePath = filePath;

        // Populate global env with FFI functions
        _ffi.PopulateEnvironment(_globalEnv);
    }

    // ========================================================================
    // Module Evaluation
    // ========================================================================

    /// <summary>
    /// Evaluate a module: registers all top-level declarations in the global environment.
    /// </summary>
    public void EvalModule(ModuleNode module)
    {
        foreach (var decl in module.Declarations)
            EvalDeclaration(decl);
    }

    /// <summary>
    /// Phase 1: Register functions, types, enums — everything EXCEPT let bindings.
    /// Used when data providers haven't been loaded yet.
    /// </summary>
    public void RegisterDeclarations(ModuleNode module)
    {
        foreach (var decl in module.Declarations)
        {
            if (decl is LetDecl)
                continue; // deferred to EvalLetBindings
            EvalDeclaration(decl);
        }
    }

    /// <summary>
    /// Phase 2: Register all let bindings in a module as lazy thunks.
    /// Called after providers have registered their data.
    /// The bindings are only evaluated when first accessed.
    /// </summary>
    public void EvalLetBindings(ModuleNode module, Dictionary<string, CopValue>? originalCallables = null)
    {
        foreach (var decl in module.Declarations)
        {
            if (decl is LetDecl ld)
            {
                // If this let shadows a function/callable with the same name, evaluate eagerly
                // to avoid self-referencing thunk (e.g., let codebase = codebase(csharp))
                bool isCallableShadow = false;
                if (_globalEnv.TryLookup(ld.Name, out var existing) && existing is ICopCallable)
                    isCallableShadow = true;
                else if (originalCallables != null && originalCallables.ContainsKey(ld.Name))
                    isCallableShadow = true;

                if (isCallableShadow)
                {
                    // Restore the original callable before evaluating the RHS so the call resolves correctly.
                    // This handles the case where a previous sibling module already replaced the callable with its result.
                    if (originalCallables != null && originalCallables.TryGetValue(ld.Name, out var originalCallable))
                        _globalEnv.Define(ld.Name, originalCallable);

                    var value = Eval(ld.Value, _globalEnv);
                    _globalEnv.Define(ld.Name, value);
                }
                else
                {
                    var capturedLd = ld;
                    var capturedEnv = _globalEnv;
                    var thunk = new CopThunk(() => Eval(capturedLd.Value, capturedEnv));
                    _globalEnv.Define(ld.Name, thunk);
                }
            }
        }
    }

    /// <summary>
    /// Run a named command/function from the global environment.
    /// Tries exact name first, then ALL-UPPERCASE form (since command → function uppercases names).
    /// </summary>
    public CopValue RunCommand(string name)
    {
        if (!_globalEnv.TryLookup(name, out var value))
        {
            // Try uppercase form (command desugaring uppercases names)
            var upper = name.ToUpperInvariant();
            if (!_globalEnv.TryLookup(upper, out value))
            {
                // Try lowercase form (let-binding collections used as runnable rules)
                var lower = name.ToLowerInvariant();
                if (!_globalEnv.TryLookup(lower, out value))
                    throw new CopEvaluationException($"Command '{name}' not found", filePath: _filePath);
            }
        }

        // Force thunks — let bindings used as runnable rules need evaluation
        value = ForceValue(value);

        _traceLog?.Invoke($"[trace] RunCommand: found '{name}' as {value?.GetType().Name}");

        if (value is CopFunction func && func.Declaration is not null)
        {
            _traceLog?.Invoke($"[trace] RunCommand: calling function '{func.Declaration.Name}' with body type={func.Declaration.Body?.GetType().Name}");
            return CallUserFunction(func, []);
        }

        if (value is ICopCallable callable)
            return callable.Invoke([], this, _globalEnv);

        // If it's a collection/value, return it directly (let-binding used as a named rule)
        return value;
    }

    // ========================================================================
    // Declaration Evaluation
    // ========================================================================

    private void EvalDeclaration(Declaration decl)
    {
        switch (decl)
        {
            case LetDecl ld:
                var value = Eval(ld.Value, _globalEnv);
                _globalEnv.Define(ld.Name, value);
                break;

            case FunctionDecl fd:
                var func = new CopFunction(fd, _globalEnv);
                RegisterFunctionWithOverloading(fd.Name, func);
                break;

            case CommandDecl cd:
                // Wrap command as a function for uniform call dispatch
                var cmdFunc = new CopFunction(
                    new FunctionDecl(cd.Name, [], null,
                        new ExpressionBody(new LiteralExpr(null)),
                        cd.IsExported, null, cd.DocComment, cd.Line),
                    _globalEnv);
                // Store a special callable that executes the command body
                var cmdCallable = new CopCommandFunction(cd, _globalEnv);
                _globalEnv.Define(cd.Name, cmdCallable);
                break;

            case EnumDecl ed:
                // Register enum members as string constants
                foreach (var member in ed.Members)
                    _globalEnv.Define(member, new CopString(member));
                // Register enum type name as a constructor: TypeKind('value') → CopString
                _globalEnv.Define(ed.Name, new CopEnumConstructor(ed.Name));
                break;

            case FlagsDecl fd2:
                // Register flags members as power-of-2 integer values
                for (int i = 0; i < fd2.Members.Count; i++)
                    _globalEnv.Define(fd2.Members[i], new CopInt(1 << i));
                break;

            case TypeDecl:
            case ImportDecl:
                // Types and imports are handled at bind time, not evaluation
                break;
        }
    }

    // ========================================================================
    // Statement Execution
    // ========================================================================

    internal void ExecStatements(List<Statement> statements, Environment env)
    {
        foreach (var stmt in statements)
            ExecStatement(stmt, env);
    }

    internal void ExecStatement(Statement stmt, Environment env)
    {
        switch (stmt)
        {
            case LetStatement ls:
                var val = Eval(ls.Value, env);
                env.Define(ls.Name, val);
                break;

            case ForEachStatement fs:
                ExecForEach(fs, env);
                break;

            case ExpressionStatement es:
                Eval(es.Expr, env);
                break;

            case PipelineStatement ps:
                ExecPipeline(ps, env);
                break;
        }
    }

    private void ExecForEach(ForEachStatement stmt, Environment env)
    {
        var collection = Eval(stmt.Collection, env);
        // Named subset resolution: predicate Clients(Types) used as foreach source
        collection = TryResolveNamedSubset(collection, env);
        _traceLog?.Invoke($"[trace] ExecForEach: {ExpressionRenderer.Render(stmt.Collection)}  (collection={DescribeValue(collection)}, body stmts={stmt.Body.Count})");
        var items = CoerceToEnumerable(collection);
        int count = 0;

        foreach (var item in items)
        {
            count++;
            var iterEnv = env.Extend();
            iterEnv.Define("item", item);
            iterEnv.Define(stmt.Variable, item);

            foreach (var bodyStmt in stmt.Body)
            {
                if (bodyStmt is ExpressionStatement es)
                {
                    // In foreach, expression results are auto-printed (implicit output)
                    var result = Eval(es.Expr, iterEnv);
                    if (count == 1) _traceLog?.Invoke($"[trace] ExecForEach first body result: {result?.GetType().Name} = {result?.Display()?.Substring(0, Math.Min(result?.Display()?.Length ?? 0, 80))}");
                    if (result is not CopNull && result is not null)
                        CallPrint(result, iterEnv);
                }
                else
                {
                    ExecStatement(bodyStmt, iterEnv);
                }
            }
        }
        _traceLog?.Invoke($"[trace] ExecForEach iterated {count} items");
    }

    private void CallPrint(CopValue value, Environment env)
    {
        if (env.TryLookup("print", out var printFn) && printFn is ICopCallable callable)
            callable.Invoke([value], this, env);
    }

    private void ExecPipeline(PipelineStatement stmt, Environment env)
    {
        var source = Eval(stmt.Source, env);
        var items = CoerceToEnumerable(source);

        foreach (var item in items)
        {
            var pipeEnv = env.Extend();
            pipeEnv.Define("item", item);

            CopValue current = item;
            foreach (var stage in stmt.Stages)
            {
                current = Eval(stage.Expr, pipeEnv);
            }

            // Final pipeline result is auto-printed
            if (current is not CopNull && current is not null)
                CallPrint(current, pipeEnv);
        }
    }

    // ========================================================================
    // Expression Evaluation
    // ========================================================================

    public CopValue Eval(Expression expr, Environment env)
    {
        return expr switch
        {
            LiteralExpr lit => EvalLiteral(lit),
            IdentifierExpr id => EvalIdentifier(id, env),
            BinaryExpr bin => EvalBinary(bin, env),
            UnaryExpr un => EvalUnary(un, env),
            CallExpr call => EvalCall(call, env),
            MemberExpr mem => EvalMember(mem, env),
            IndexExpr idx => EvalIndex(idx, env),
            LambdaExpr lam => new CopLambda(lam, env),
            ConditionalExpr cond => EvalConditional(cond, env),
            MatchExpr match => EvalMatch(match, env),
            ListExpr list => EvalList(list, env),
            ObjectExpr obj => EvalObject(obj, env),
            InterpolatedStringExpr interp => EvalInterpolation(interp, env),
            FilterExpr filter => EvalFilter(filter, env),
            _ => throw new CopEvaluationException($"Unknown expression type: {expr.GetType().Name}", expr.Line, _filePath)
        };
    }

    private CopValue EvalLiteral(LiteralExpr lit) => lit.Value switch
    {
        null => CopNull.Instance,
        bool b => CopBool.Of(b),
        int i => new CopInt(i),
        double d => new CopNumber(d),
        string s => new CopString(s),
        _ => throw new CopEvaluationException($"Unknown literal type: {lit.Value.GetType()}", lit.Line, _filePath)
    };

    private CopValue EvalIdentifier(IdentifierExpr id, Environment env)
    {
        if (env.TryLookup(id.Name, out var value))
            return value;

        // Check FFI registry as fallback (for lazily registered externals)
        var ext = _ffi.Resolve(id.Name);
        if (ext is not null)
            return ext;

        throw new CopEvaluationException($"Undefined variable '{id.Name}'", id.Line, _filePath);
    }

    private CopValue EvalBinary(BinaryExpr bin, Environment env)
    {
        // Short-circuit for logical operators
        if (bin.Op == BinaryOp.And)
        {
            var left = CoerceCallableToBool(ForceValue(Eval(bin.Left, env)), env);
            return left.IsTruthy ? CoerceCallableToBool(ForceValue(Eval(bin.Right, env)), env) : left;
        }
        if (bin.Op == BinaryOp.Or)
        {
            var left = CoerceCallableToBool(ForceValue(Eval(bin.Left, env)), env);
            return left.IsTruthy ? left : CoerceCallableToBool(ForceValue(Eval(bin.Right, env)), env);
        }

        var l = ForceValue(Eval(bin.Left, env));
        var r = ForceValue(Eval(bin.Right, env));

        return bin.Op switch
        {
            BinaryOp.Add => NumericOp(l, r, (a, b) => a + b, bin.Line)
                            ?? CollectionConcat(l, r)
                            ?? StringConcat(l, r, bin.Line),
            BinaryOp.Subtract => NumericOp(l, r, (a, b) => a - b, bin.Line)
                            ?? throw new CopEvaluationException("Cannot subtract non-numeric values", bin.Line, _filePath),
            BinaryOp.Multiply => NumericOp(l, r, (a, b) => a * b, bin.Line)
                            ?? throw new CopEvaluationException("Cannot multiply non-numeric values", bin.Line, _filePath),
            BinaryOp.Divide => NumericOp(l, r, (a, b) => a / b, bin.Line)
                            ?? throw new CopEvaluationException("Cannot divide non-numeric values", bin.Line, _filePath),
            BinaryOp.Modulo => NumericOp(l, r, (a, b) => a % b, bin.Line)
                            ?? throw new CopEvaluationException("Cannot modulo non-numeric values", bin.Line, _filePath),
            BinaryOp.Equal => CopBool.Of(ValuesEqual(l, r)),
            BinaryOp.NotEqual => CopBool.Of(!ValuesEqual(l, r)),
            BinaryOp.LessThan => CompareOp(l, r, (cmp) => cmp < 0, bin.Line),
            BinaryOp.GreaterThan => CompareOp(l, r, (cmp) => cmp > 0, bin.Line),
            BinaryOp.LessOrEqual => CompareOp(l, r, (cmp) => cmp <= 0, bin.Line),
            BinaryOp.GreaterOrEqual => CompareOp(l, r, (cmp) => cmp >= 0, bin.Line),
            BinaryOp.BitwiseAnd => NumericOp(l, r, (a, b) => (int)a & (int)b, bin.Line)
                            ?? CopNull.Instance,
            BinaryOp.BitwiseOr => NumericOp(l, r, (a, b) => (int)a | (int)b, bin.Line)
                            ?? CopNull.Instance,
            _ => throw new CopEvaluationException($"Unknown binary operator: {bin.Op}", bin.Line, _filePath)
        };
    }

    private CopValue EvalUnary(UnaryExpr un, Environment env)
    {
        var operand = ForceValue(Eval(un.Operand, env));
        return un.Op switch
        {
            // Coerce a bare predicate reference (a callable) to a bool by invoking it on the
            // current `item`, mirroring how `&&`/`||` handle a bare predicate. Without this,
            // `=> !somePredicate` would negate the (always-truthy) callable and yield false.
            UnaryOp.Not => CopBool.Of(!CoerceCallableToBool(operand, env).IsTruthy),
            UnaryOp.Negate => operand switch
            {
                CopInt i => new CopInt(-i.Value),
                CopNumber n => new CopNumber(-n.Value),
                _ => throw new CopEvaluationException("Cannot negate non-numeric value", un.Line, _filePath)
            },
            _ => throw new CopEvaluationException($"Unknown unary operator: {un.Op}", un.Line, _filePath)
        };
    }

    private CopValue EvalCall(CallExpr call, Environment env)
    {
        // Method call dispatch: obj.method(args) → try method(obj, args) via FFI
        if (call.Callee is MemberExpr mem)
        {
            var obj = ForceValue(Eval(mem.Object, env));

            // Per-item collection transforms (Select/Where/OrderBy) evaluate their
            // argument once per element with `item` bound (Cop convention), so they must
            // run before the generic eager-argument dispatch below — which evaluates args
            // in the outer environment and would fail to resolve `item`.
            if (IsCollectionValue(obj) &&
                TryEvalPerItemTransform(obj, mem.Member, call.Args, env, out var transformResult))
                return transformResult;

            var args = call.Args.Select(a => Eval(a, env)).ToList();

            // First, try resolving as a member that is callable
            CopValue? memberVal = TryGetMember(obj, mem.Member);
            if (memberVal is ICopCallable memberCallable)
                return memberCallable.Invoke(args, this, env);

            // Method dispatch: look up method name as an FFI/env function, pass obj as first arg
            if (env.TryLookup(mem.Member, out var fn) && fn is ICopCallable methodFn)
            {
                var methodArgs = new List<CopValue> { obj };
                methodArgs.AddRange(args);
                return methodFn.Invoke(methodArgs, this, env);
            }

            // Try FFI registry directly
            var ffiMethod = _ffi.Resolve(mem.Member);
            if (ffiMethod is ICopCallable ffiFn)
            {
                var methodArgs = new List<CopValue> { obj };
                methodArgs.AddRange(args);
                return ffiFn.Invoke(methodArgs, this, env);
            }

            throw new CopEvaluationException(
                $"Cannot call '{mem.Member}' on {obj.GetType().Name}",
                call.Line, _filePath);
        }

        var callee = ForceValue(Eval(call.Callee, env));
        var callArgs = call.Args.Select(a => Eval(a, env)).ToList();

        // Currying / partial application: a DIRECT call that extends a closure, or under-applies an
        // item-parameter function (its leading parameter is a type the filter fills per element),
        // produces a partial-application closure rather than executing. The closure is completed per
        // item when used in a filter chain (see CopClosure). Filter calls bypass this path — they
        // resolve the callable in ApplyFilterPredicate* and invoke it with the item directly.
        if (callee is CopClosure closure)
            return closure.WithExplicit(callArgs);
        // A user function may be bound as a function group; pick an item-parameter overload that
        // this call under-applies, and curry it.
        var curryTarget = callee switch
        {
            CopFunction f => f,
            CopFunctionGroup g => g.Functions
                .FirstOrDefault(o => o.Declaration.Params.Count > callArgs.Count && IsItemParameterFirst(o)),
            _ => null
        };
        if (curryTarget is not null && IsItemParameterFirst(curryTarget)
            && callArgs.Count < curryTarget.Declaration.Params.Count)
        {
            int explicitArity = curryTarget.Declaration.Params.Count - 1;
            if (explicitArity >= 1)
                return new CopClosure(curryTarget, callArgs, explicitArity);
        }

        if (callee is ICopCallable callable)
            return callable.Invoke(callArgs, this, env);

        throw new CopEvaluationException(
            $"Value of type {callee.GetType().Name} is not callable",
            call.Line, _filePath);
    }

    /// <summary>
    /// True if the function's leading parameter is an implicit item parameter — a type-named
    /// parameter (uppercase first letter) that a filter fills per element rather than the caller.
    /// Used to skip that parameter when currying so `format('[')` binds the first EXPLICIT param.
    /// </summary>
    private static bool IsItemParameterFirst(CopFunction fn)
    {
        if (fn.Declaration.Params.Count == 0) return false;
        var name = fn.Declaration.Params[0].Name;
        return name.Length > 0 && char.IsUpper(name[0]);
    }

    private static bool IsCollectionValue(CopValue v) =>
        v is CopList or CopLazyCollection or CopQueryable or CopQueryableProperty;

    /// <summary>
    /// Collection transforms whose single argument is a per-element expression
    /// (Cop convention: `item` is the element). Unlike FFI collection functions
    /// (text, distinct, take, ...), the argument must be evaluated once per item
    /// with `item` bound, so these are handled here instead of via eager dispatch.
    /// Supports both the implicit-item form `Select(item.Name)` and an explicit
    /// single-parameter lambda `Select((t) => t.Name)`.
    /// </summary>
    private bool TryEvalPerItemTransform(CopValue collection, string member,
        IReadOnlyList<Expression> argExprs, Environment env, out CopValue result)
    {
        result = CopNull.Instance;
        switch (member)
        {
            case "Select" or "select":
            {
                if (argExprs.Count != 1) return false;
                var projected = new List<CopValue>();
                foreach (var item in CoerceToEnumerable(collection))
                    projected.Add(ForceValue(EvalElementExpr(argExprs[0], item, env)));
                result = new CopList(projected);
                return true;
            }
            case "Where" or "where":
            {
                if (argExprs.Count != 1) return false;
                var kept = new List<CopValue>();
                foreach (var item in CoerceToEnumerable(collection))
                {
                    var v = ForceValue(EvalElementExpr(argExprs[0], item, env));
                    if (v is ICopCallable callable)
                        v = callable.Invoke([item], this, env);
                    if (v.IsTruthy)
                        kept.Add(item);
                }
                result = new CopList(kept);
                return true;
            }
            case "OrderBy" or "orderBy" or "OrderByDescending" or "orderByDescending":
            {
                if (argExprs.Count != 1) return false;
                bool descending = member.Contains("Descending", StringComparison.OrdinalIgnoreCase);
                var keyed = new List<(CopValue Item, CopValue Key)>();
                foreach (var item in CoerceToEnumerable(collection))
                    keyed.Add((item, ForceValue(EvalElementExpr(argExprs[0], item, env))));
                keyed.Sort((a, b) => CompareValues(a.Key, b.Key) * (descending ? -1 : 1));
                result = new CopList(keyed.Select(k => k.Item).ToList());
                return true;
            }
            case "any" or "all" or "none" or "count":
            {
                // Aggregates over an inline element expression (e.g. items.any(item > 2)).
                var agg = TryEvalAggregate(member, collection, argExprs, negated: false, env);
                if (agg is null) return false;
                result = agg;
                return true;
            }
            default:
                return false;
        }
    }

    /// <summary>
    /// Evaluate a per-element transform argument with `item` bound to the element.
    /// An explicit single-parameter lambda also binds its parameter to the element.
    /// </summary>
    private CopValue EvalElementExpr(Expression argExpr, CopValue item, Environment env)
    {
        var elemEnv = env.Extend();
        elemEnv.Define("item", item);
        if (argExpr is LambdaExpr lambda)
        {
            if (lambda.Params.Count >= 1)
                elemEnv.Define(lambda.Params[0].Name, item);
            return Eval(lambda.Body, elemEnv);
        }
        return Eval(argExpr, elemEnv);
    }

    /// <summary>
    /// Evaluates a per-item aggregate (any/all/none/count) over a collection. The single
    /// argument expression is evaluated once per element with <c>item</c> bound, so an inline
    /// expression (<c>item:contains('x')</c>), a bare predicate name, or a lambda all work
    /// uniformly. Returns null when the argument count is not exactly one (caller falls back).
    /// </summary>
    private CopValue? TryEvalAggregate(string methodName, CopValue collection,
        IReadOnlyList<Expression> argExprs, bool negated, Environment env)
    {
        if (argExprs.Count != 1) return null;

        int matched = 0;
        foreach (var item in CoerceToEnumerable(collection))
        {
            var v = ForceValue(EvalElementExpr(argExprs[0], item, env));
            if (v is ICopCallable callable)
                v = callable.Invoke([item], this, env);

            if (v.IsTruthy)
            {
                matched++;
                if (methodName == "any") return Negate(true);
                if (methodName == "none") return Negate(false);
            }
            else if (methodName == "all")
            {
                return Negate(false);
            }
        }

        return methodName switch
        {
            "any" => Negate(false),
            "none" => Negate(true),
            "all" => Negate(true),       // vacuously true for an empty collection
            "count" => new CopInt(matched),
            _ => null
        };

        CopValue Negate(bool b) => CopBool.Of(negated ? !b : b);
    }

    /// <summary>3-way compare for OrderBy: numeric when both sides are numbers, else ordinal string.</summary>
    private static int CompareValues(CopValue a, CopValue b)
    {
        var (an, bn) = (ToNumber(a), ToNumber(b));
        if (an is not null && bn is not null)
            return an.Value.CompareTo(bn.Value);
        return string.Compare(a.Display(), b.Display(), StringComparison.Ordinal);
    }

    private CopValue? TryGetMember(CopValue obj, string member)
    {
        // Force thunks before member access
        if (obj is CopThunk thunk)
            obj = thunk.Force();

        return obj switch
        {
            CopObject co when co.HasField(member) => co.GetField(member),
            CopDynamicObject dyn when dyn.HasField(member) => dyn.GetField(member),
            CopProviderProxy proxy when proxy.HasField(member) => proxy.GetField(member),
            _ => null
        };
    }

    private CopValue EvalMember(MemberExpr mem, Environment env)
    {
        var obj = EvalMemberObject(mem.Object, env);

        // Force thunks before member access
        if (obj is CopThunk thunk)
            obj = thunk.Force();

        // Named subset resolution: if obj is a predicate-as-collection, expand it
        obj = TryResolveNamedSubset(obj, env);

        var result = obj switch
        {
            CopObject co => co.GetField(mem.Member),
            CopDynamicObject dyn => dyn.GetField(mem.Member),
            CopProviderProxy proxy => proxy.GetField(mem.Member),
            CopList list => EvalListMember(list, mem.Member, mem.Line),
            CopLazyCollection lazy => EvalLazyMember(lazy, mem.Member, mem.Line),
            CopString str => EvalStringMember(str, mem.Member, mem.Line),
            // Null propagation: accessing a member of a null/absent value yields null rather than
            // crashing, so checks over real-world data with missing fields (e.g. a C# type in the
            // global namespace whose `File.Namespace` is null) stay robust — `null.Length` is null,
            // and downstream predicates like `:greaterThan(0)` then simply evaluate to false.
            CopNull => CopNull.Instance,
            _ when mem.Member == "Type" => new CopString(CopTypeNameOf(obj)),
            _ => throw new CopEvaluationException(
                $"Cannot access member '{mem.Member}' on {obj.GetType().Name}",
                mem.Line, _filePath)
        };

        // Computed property fallback for trait conformance
        if (result == CopNull.Instance && TypeRegistry is not null)
        {
            var typeName = obj switch
            {
                CopDynamicObject d => d.TypeName,
                CopObject o => o.TypeName,
                _ => null
            };
            if (typeName is not null)
            {
                var computedExpr = TypeRegistry.GetComputedProperty(typeName, mem.Member);
                if (computedExpr is not null)
                {
                    // Evaluate computed expression with 'item' bound to the object
                    var computedEnv = new Environment(env);
                    computedEnv.Define("item", obj);
                    result = Eval(computedExpr, computedEnv);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// If value is a predicate with 1 parameter matching a known collection,
    /// resolve it to the filtered collection (named subset pattern).
    /// </summary>
    private CopValue TryResolveNamedSubset(CopValue value, Environment env)
    {
        if (value is CopFunction predFunc && predFunc.Declaration.Params.Count == 1)
        {
            var inputName = predFunc.Declaration.Params[0].Name;
            if (env.TryLookup(inputName, out var inputCollection))
            {
                // Force thunks before checking type
                if (inputCollection is CopThunk thunk)
                    inputCollection = thunk.Force();
                if (inputCollection is CopList || inputCollection is CopLazyCollection)
                {
                    var captured = inputCollection;
                    return new CopLazyCollection(() =>
                    {
                        var items = CoerceToEnumerable(captured);
                        var filtered = new List<CopValue>();
                        foreach (var item in items)
                        {
                            var result = predFunc.Invoke([item], this, env);
                            if (result.IsTruthy)
                                filtered.Add(item);
                        }
                        return filtered;
                    });
                }
            }
        }
        else if (value is CopFunctionGroup group)
        {
            var firstFunc = group.Functions.FirstOrDefault();
            if (firstFunc is not null && firstFunc.Declaration.Params.Count == 1)
            {
                var inputName = firstFunc.Declaration.Params[0].Name;
                if (env.TryLookup(inputName, out var inputCollection))
                {
                    // Force thunks before checking type
                    if (inputCollection is CopThunk thunk)
                        inputCollection = thunk.Force();
                    if (inputCollection is CopList || inputCollection is CopLazyCollection)
                    {
                        var captured = inputCollection;
                        return new CopLazyCollection(() =>
                        {
                            var items = CoerceToEnumerable(captured);
                            var filtered = new List<CopValue>();
                            foreach (var item in items)
                            {
                                var result = group.Invoke([item], this, env);
                                if (result.IsTruthy)
                                    filtered.Add(item);
                            }
                            return filtered;
                        });
                    }
                }
            }
        }
        return value;
    }

    private static string CopTypeNameOf(CopValue value) => value switch
    {
        CopThunk thunk => CopTypeNameOf(thunk.Force()),
        CopObject co => co.TypeName ?? "object",
        CopDynamicObject dyn => dyn.TypeName ?? "object",
        CopString => "string",
        CopInt => "int",
        CopNumber => "float",
        CopBool => "bool",
        CopList => "collection",
        CopLazyCollection => "collection",
        CopNull => "nic",
        _ => "object"
    };

    /// <summary>
    /// Evaluate the object part of a member expression.
    /// Handles the Cop convention where Type.Property means "access Property on the parameter
    /// whose type is Type" — the parameter name is the lowercase form of the type name.
    /// </summary>
    private CopValue EvalMemberObject(Expression objExpr, Environment env)
    {
        if (objExpr is IdentifierExpr id && id.Name.Length > 0 && char.IsUpper(id.Name[0]))
        {
            // Try direct lookup first
            if (env.TryLookup(id.Name, out var val))
                return val;
            // Type-qualified access: Folder.X → look up "folder" parameter
            var lower = char.ToLower(id.Name[0]) + id.Name[1..];
            if (env.TryLookup(lower, out var paramVal))
                return paramVal;
        }
        return Eval(objExpr, env);
    }

    private CopValue EvalListMember(CopList list, string member, int line) => member switch
    {
        "Count" or "count" or "Length" or "length" => new CopInt(list.Items.Count),
        "First" or "first" => list.Items.Count > 0 ? list.Items[0] : CopNull.Instance,
        "Last" or "last" => list.Items.Count > 0 ? list.Items[^1] : CopNull.Instance,
        // Collection projection: list.Name → map each item's Name field
        _ => ProjectCollection(list.Items, member)
    };

    private CopValue EvalLazyMember(CopLazyCollection lazy, string member, int line) => member switch
    {
        "Count" or "count" or "Length" or "length" => new CopInt(lazy.Enumerate().Count()),
        "First" or "first" => lazy.Enumerate().FirstOrDefault() ?? CopNull.Instance,
        // Collection projection: collection.Name → map each item's Name field
        _ => ProjectCollection(lazy.Enumerate().ToList(), member)
    };

    private CopValue EvalStringMember(CopString str, string member, int line) => member switch
    {
        "Length" or "length" => new CopInt(str.Value.Length),
        "Type" => new CopString("string"),
        _ => throw new CopEvaluationException($"Unknown string member '{member}'", line, _filePath)
    };

    /// <summary>
    /// Project a member across all items in a collection: items.Name → [item1.Name, item2.Name, ...]
    /// </summary>
    private static CopValue ProjectCollection(IReadOnlyList<CopValue> items, string member)
    {
        var projected = new List<CopValue>(items.Count);
        foreach (var item in items)
        {
            var field = item switch
            {
                CopDynamicObject dyn => dyn.GetField(member),
                CopObject obj => obj.GetField(member),
                _ => CopNull.Instance
            };
            if (field is not CopNull)
                projected.Add(field);
        }
        return new CopList(projected);
    }

    private CopValue EvalIndex(IndexExpr idx, Environment env)
    {
        var obj = ForceValue(Eval(idx.Object, env));
        var index = ForceValue(Eval(idx.Index, env));

        return (obj, index) switch
        {
            (CopList list, CopInt i) => i.Value >= 0 && i.Value < list.Items.Count
                ? list.Items[i.Value]
                : CopNull.Instance,
            (CopString str, CopInt i) => i.Value >= 0 && i.Value < str.Value.Length
                ? new CopString(str.Value[i.Value].ToString())
                : CopNull.Instance,
            (CopObject co, CopString key) => co.GetField(key.Value),
            _ => throw new CopEvaluationException("Invalid index operation", idx.Line, _filePath)
        };
    }

    private CopValue EvalConditional(ConditionalExpr cond, Environment env)
    {
        var rawCondition = ForceValue(Eval(cond.Condition, env));
        _traceLog?.Invoke($"[trace] EvalConditional: {ExpressionRenderer.Render(cond.Condition)}  (raw={DescribeValue(rawCondition)})");
        var condition = CoerceCallableToBool(rawCondition, env);
        _traceLog?.Invoke($"[trace] EvalConditional: coerced={DescribeValue(condition)}, truthy={condition?.IsTruthy}");
        return condition.IsTruthy
            ? Eval(cond.Then, env)
            : Eval(cond.Else, env);
    }

    /// <summary>
    /// Describes a runtime value's collection kind for diagnostics — e.g. CopList[412],
    /// CopQueryable&lt;csharp&gt; — without forcing or enumerating lazy/queryable sources.
    /// </summary>
    private static string DescribeValue(CopValue? value) => value switch
    {
        null => "null",
        CopList l => $"CopList[{l.Items.Count}]",
        CopQueryable q => $"CopQueryable<{q.ProviderName}>",
        CopQueryableProperty qp => $"CopQueryableProperty<{qp.Source.ProviderName}.{qp.PropertyName}>",
        CopLazyCollection => "CopLazyCollection[lazy]",
        _ => value.GetType().Name
    };

    private CopValue EvalMatch(MatchExpr match, Environment env)
    {
        var discriminant = Eval(match.Discriminant, env);

        foreach (var arm in match.Arms)
        {
            if (MatchPattern(arm.Pat, discriminant, env))
                return Eval(arm.Body, env);
        }

        return CopNull.Instance;
    }

    private CopValue EvalList(ListExpr list, Environment env)
    {
        var items = list.Elements.Select(e => Eval(e, env)).ToList();
        return new CopList(items);
    }

    private CopValue EvalObject(ObjectExpr obj, Environment env)
    {
        var fields = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (var field in obj.Fields)
            fields[field.Name] = Eval(field.Value, env);
        return new CopObject(fields) { TypeName = obj.TypeHint };
    }

    private CopValue EvalInterpolation(InterpolatedStringExpr interp, Environment env)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var part in interp.Parts)
        {
            switch (part)
            {
                case TextPart tp:
                    sb.Append(tp.Text);
                    break;
                case ExpressionPart ep:
                    var val = Eval(ep.Expr, env);
                    if (ep.Format is not null)
                        sb.Append($"{{{val.Display()}@{ep.Format}}}");
                    else
                        sb.Append(val.Display());
                    break;
            }
        }
        return new CopString(sb.ToString());
    }

    private CopValue EvalFilter(FilterExpr filter, Environment env)
    {
        var collection = ForceValue(Eval(filter.Collection, env));
        bool negated = filter.Negated;

        _traceLog?.Invoke($"[trace] EvalFilter: {ExpressionRenderer.Render(filter)}  (collection={DescribeValue(collection)})");

        // Queryable collection: try to compile predicate and accumulate filter (lazy pushdown)
        if (collection is CopQueryable queryable)
        {
            // A user-defined (non-intrinsic) predicate is NOT a provider property and must not be
            // pushed down — the provider would treat it as a missing field and yield null. Compiling
            // `people:canVote` to a PropertyFilter('canVote') crashed the json provider with
            // "PropertyFilter expects bool for 'canVote', got null" (issue #33). Materialize and
            // evaluate per item so the predicate body actually runs. Real fields (e.g. `people:active`)
            // don't resolve to a user function and still push down.
            if (ResolvesToUserPredicate(filter.Predicate, env))
            {
                _traceLog?.Invoke($"[trace] EvalFilter: '{ExpressionRenderer.Render(filter.Predicate)}' is a user predicate, materializing queryable for per-item evaluation");
                collection = ForceValue(queryable.Materialize());
            }
            else
            {
                // Try to compile the predicate AST to a FilterExpression for pushdown
                var compiled = PredicateCompiler.TryCompile(filter.Predicate, negated);
                if (compiled is not null)
                {
                    _traceLog?.Invoke($"[trace] EvalFilter: compiled predicate '{ExpressionRenderer.Render(filter.Predicate)}' to FilterExpression, accumulating on queryable");
                    return queryable.WithFilter(compiled);
                }

                // Check if this is a property access (map semantic) that could be part of a compound filter
                var propName = PredicateCompiler.TryExtractPropertyAccess(filter.Predicate);
                if (propName is not null)
                {
                    // Return a queryable with property context — next filter in chain may compile to StringOp/Comparison
                    _traceLog?.Invoke($"[trace] EvalFilter: property access '{propName}' on queryable, creating property proxy");
                    return new CopQueryableProperty(queryable, propName);
                }

                // Cannot compile — materialize and fall through to normal path
                _traceLog?.Invoke($"[trace] EvalFilter: cannot compile predicate '{ExpressionRenderer.Render(filter.Predicate)}', materializing queryable");
                collection = queryable.Materialize();
                collection = ForceValue(collection);
            }
        }

        // Queryable property: compound pattern e.g., queryable:Name:startsWith('A')
        if (collection is CopQueryableProperty queryProp)
        {
            // Try to compile the predicate with property context
            var compiled = PredicateCompiler.TryCompile(filter.Predicate, negated, queryProp.PropertyName);
            if (compiled is not null)
            {
                _traceLog?.Invoke($"[trace] EvalFilter: compiled compound filter {queryProp.PropertyName}:{ExpressionRenderer.Render(filter.Predicate)}");
                return queryProp.Source.WithFilter(compiled);
            }

            // Cannot compile — materialize the source and apply property access + filter locally
            _traceLog?.Invoke($"[trace] EvalFilter: cannot compile compound, materializing");
            collection = queryProp.Source.Materialize();
            collection = ForceValue(collection);
            // Apply property access as a map, then let normal filter logic handle it
            var mapped = CoerceToEnumerable(collection)
                .Select(item => GetFieldOnValue(item, queryProp.PropertyName))
                .Where(v => v is not CopNull)
                .ToList();
            collection = new CopList(mapped);
        }

        // If collection resolved to a predicate/function, resolve as a "named subset":
        // predicate Clients(Types) => ... used bare as `Clients:filter` means Types:Clients:filter
        collection = TryResolveNamedSubset(collection, env);

        // If collection is a scalar (not a list/collection), apply predicate as a test
        if (collection is not CopList and not CopLazyCollection)
        {
            _traceLog?.Invoke($"[trace] EvalFilter: scalar branch, collection={collection?.Display()?.Substring(0, Math.Min(collection?.Display()?.Length ?? 0, 80))}");
            var result = ApplyFilterPredicate(filter.Predicate, collection, env);
            return CopBool.Of(negated ? !result : result);
        }

        // Bare identifier on a collection: a function whose SOLE parameter is a collection
        // (core's `empty(items:[T]) : bool`, `distinct(items:[T])`, `pop(items:[T])`, ...) is a
        // collection-level operation — invoke it once with the whole collection. The per-item
        // filter loop would wrongly pass each element (e.g. `empty` got each child statement and
        // returned an always-truthy collection, matching non-empty collections in a boolean
        // context — issue #32; `distinct`/`pop` errored with "expects [collection], got <item>").
        // If instead a scalar overload's parameter matches the item type (e.g. files'
        // `empty(Folder)`), keep per-item filtering.
        if (filter.Predicate is IdentifierExpr bareId
            && ShouldApplyAtCollectionLevel(bareId.Name, collection, env))
        {
            if (env.TryLookup(bareId.Name, out var bareBinding)
                && CollectionParamOverload(bareBinding) is { } collOverload)
            {
                var r = collOverload.Invoke([collection], this, env);
                return negated && r is CopBool rb ? CopBool.Of(!rb.Value) : r;
            }
            // Builtin `:empty` with no user collection overload in scope → emptiness test.
            bool isEmpty = IsEmpty(collection);
            return CopBool.Of(negated ? !isEmpty : isEmpty);
        }

        // Check for collection-level predicates (contains, any, all, none, count)
        // These operate on the collection as a whole, not per-item.
        var collResult = TryEvalCollectionPredicate(filter.Predicate, collection, negated, env);
        if (collResult is not null)
            return collResult;

        // For collections: apply predicate to each item.
        // If predicate returns a non-bool object (map semantic), collect those results.
        // Otherwise filter items by truthiness (filter semantic).
        var predName = filter.Predicate is IdentifierExpr pid ? pid.Name : filter.Predicate is CallExpr pcall && pcall.Callee is IdentifierExpr pcid ? pcid.Name : "?";
        return new CopLazyCollection(() =>
        {
            var items = CoerceToEnumerable(collection);
            var mapped = new List<CopValue>();
            bool isMap = false;
            bool firstItem = true;
            int itemCount = 0;

            foreach (var item in items)
            {
                itemCount++;
                try
                {
                    var result = ApplyFilterPredicateValue(filter.Predicate, item, env);

                    // A predicate whose body is a sole bare predicate name (e.g. `=> isPublic`)
                    // evaluates to that predicate's function group instead of invoking it. Invoke
                    // it on the item so the filter sees a bool — otherwise map-mode is mis-detected
                    // below and the filter yields function groups, which crash downstream (e.g. in
                    // toError). Mirrors how `&&`/`||` coerce a bare predicate via CoerceCallableToBool.
                    if (result is ICopCallable)
                    {
                        var coerceEnv = env.Extend();
                        coerceEnv.Define("item", item);
                        result = CoerceCallableToBool(result, coerceEnv);
                    }

                    if (firstItem)
                    {
                        _traceLog?.Invoke($"[trace] EvalFilter({predName}): first result type={result?.GetType().Name}, value={result?.Display()?.Substring(0, Math.Min(result?.Display()?.Length ?? 0, 50))}, item={item?.Display()?.Substring(0, Math.Min(item?.Display()?.Length ?? 0, 50))}");
                        // Determine mode from first result:
                        // If it's a non-bool structured value, this is a map operation
                        isMap = result is not CopBool && result is not CopNull && result.IsTruthy;
                        firstItem = false;
                    }

                    if (isMap)
                    {
                        // Map mode: collect non-null results
                        if (result is not CopNull && result.IsTruthy)
                            mapped.Add(result);
                    }
                    else
                    {
                        // Filter mode: keep items where predicate is truthy (respecting negation)
                        bool passes = result.IsTruthy;
                        if (negated) passes = !passes;
                        if (passes) mapped.Add(item);
                    }
                }
                catch (CopEvaluationException ex)
                {
                    throw new CopEvaluationException(
                        $"Predicate '{predName}' failed on item {itemCount}: {ex.Message}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    throw new CopEvaluationException(
                        $"Predicate '{predName}' failed on item {itemCount}: {ex.Message}");
                }
            }

            _traceLog?.Invoke($"[trace] EvalFilter({predName}): enumerated {itemCount} items, mapped {mapped.Count} results");
            return mapped;
        });
    }

    /// <summary>
    /// Check if a filter predicate is a collection-level membership test (contains).
    /// Returns null if the predicate should be handled per-item.
    /// Only intercepts predicates that are unambiguously collection-level operations.
    /// </summary>
    private CopValue? TryEvalCollectionPredicate(Expression predicateExpr, CopValue collection, bool negated, Environment env)
    {
        if (predicateExpr is not CallExpr call) return null;

        string? methodName = call.Callee switch
        {
            IdentifierExpr id => id.Name,
            _ => null
        };

        if (methodName is null || call.Args.Count == 0) return null;

        // Per-item aggregates (any/all/none/count) accept an inline element expression
        // (e.g. items:any(item:contains('x'))), a bare predicate name, or a lambda —
        // evaluate the argument once per element with `item` bound. Without this, an inline
        // expression isn't a callable, so the colon form silently fell through to the per-item
        // filter loop and returned a collection instead of a bool.
        if (methodName is "any" or "all" or "none" or "count")
        {
            var agg = TryEvalAggregate(methodName, collection, call.Args, negated, env);
            if (agg is not null) return agg;
        }

        // Generic dispatch: collection-level when any arg is a lambda/callable (e.g.
        // Types:all(isPublic)) OR the resolved function's first parameter is a collection
        // (e.g. References:containsAny(['cop']) → containsAny(items:[string], values:[string])).
        // Without the latter, `coll:containsAny([...])` dispatched per item and failed with
        // "'containsAny' parameter 'items' expects [string], got string" (issue #30).
        if (HasCallableArg(call.Args, env)
            || FirstParamIsCollectionFunction(methodName, call.Args.Count, collection, env))
        {
            var evaluatedArgs = call.Args.Select(a => Eval(a, env)).ToList();
            var allArgs = new List<CopValue> { collection };
            allArgs.AddRange(evaluatedArgs);

            ICopCallable? method = null;
            if (env.TryLookup(methodName, out var fn) && fn is ICopCallable c)
                method = c;
            else if (_ffi.Resolve(methodName) is ICopCallable f)
                method = f;

            if (method is not null)
            {
                var result = method.Invoke(allArgs, this, env);
                if (negated && result is CopBool b)
                    return CopBool.Of(!b.Value);
                return result;
            }
        }

        // Fallback: contains(value) for collection membership test
        if (methodName == "contains")
        {
            var items = CoerceToEnumerable(collection).ToList();
            var searchVal = Eval(call.Args[0], env).Display();
            bool found = items.Any(item => string.Equals(item.Display(), searchVal, StringComparison.OrdinalIgnoreCase));
            return CopBool.Of(negated ? !found : found);
        }

        return null;
    }

    /// <summary>
    /// Checks if any argument in a call expression is a callable (lambda expression
    /// or identifier that resolves to ICopCallable). Used to determine whether a
    /// colon-syntax call should dispatch at the collection level.
    /// </summary>
    private bool HasCallableArg(IReadOnlyList<Expression> args, Environment env)
    {
        foreach (var arg in args)
        {
            if (arg is LambdaExpr)
                return true;
            if (arg is IdentifierExpr id)
            {
                if (env.TryLookup(id.Name, out var val) && val is ICopCallable)
                    return true;
                if (_ffi.Resolve(id.Name) is ICopCallable)
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Apply a filter predicate expression to a subject value, returning the raw CopValue result
    /// (not just bool). Used by EvalFilter to distinguish filter vs map semantics.
    /// </summary>
    private CopValue ApplyFilterPredicateValue(Expression predicateExpr, CopValue subject, Environment env)
    {
        switch (predicateExpr)
        {
            case IdentifierExpr id:
                if (env.TryLookup(id.Name, out var fn) && ForceValue(fn) is ICopCallable callable)
                    return callable.Invoke([subject], this, env);
                var ffiFn = _ffi.Resolve(id.Name);
                if (ffiFn is ICopCallable ffiCallable)
                    return ffiCallable.Invoke([subject], this, env);
                if (subject is CopDynamicObject dyn && dyn.HasField(id.Name))
                    return dyn.GetField(id.Name);
                if (subject is CopObject obj && obj.HasField(id.Name))
                    return obj.GetField(id.Name);
                if (id.Name == "empty")
                    return CopBool.Of(IsEmpty(subject));
                return CopBool.False;

            case CallExpr call:
                var methodName = call.Callee switch
                {
                    IdentifierExpr mid => mid.Name,
                    MemberExpr mem => mem.Member,
                    _ => null
                };
                if (methodName is not null)
                {
                    // Built-in :in([...]) membership test
                    if (methodName == "in" && call.Args.Count == 1)
                    {
                        var listVal = ForceValue(Eval(call.Args[0], env));
                        if (listVal is CopList list)
                            return CopBool.Of(EvalInMembership(subject, list));
                    }

                    // Evaluate args with item bound to subject (for template interpolation)
                    var argEnv = env.Extend();
                    argEnv.Define("item", subject);
                    var args = call.Args.Select(a => Eval(a, argEnv)).ToList();
                    var allArgs = new List<CopValue> { subject };
                    allArgs.AddRange(args);

                    if (env.TryLookup(methodName, out var mfn) && ForceValue(mfn) is ICopCallable mc)
                        return mc.Invoke(allArgs, this, env);
                    var mffi = _ffi.Resolve(methodName);
                    if (mffi is ICopCallable mfc)
                        return mfc.Invoke(allArgs, this, env);
                }
                var itemEnv = env.Extend();
                itemEnv.Define("item", subject);
                return Eval(call, itemEnv);

            case MemberExpr mem2:
                return EvalMemberOnSubject(mem2, subject, env);

            default:
                var val = Eval(predicateExpr, env);
                if (val is ICopCallable predCallable)
                    return predCallable.Invoke([subject], this, env);
                return val;
        }
    }

    /// <summary>
    /// Apply a filter predicate expression to a subject value.
    /// For `:isPublic` → calls isPublic(subject)
    /// For `:startsWith('A')` → calls startsWith(subject, 'A')
    /// For `:empty` → tests subject.IsTruthy (inverted: empty means !truthy for collections)
    /// </summary>
    private bool ApplyFilterPredicate(Expression predicateExpr, CopValue subject, Environment env)
    {
        switch (predicateExpr)
        {
            case IdentifierExpr id:
                // :predicateName → call predicateName(subject)
                if (env.TryLookup(id.Name, out var fn) && fn is ICopCallable callable)
                    return callable.Invoke([subject], this, env).IsTruthy;
                var ffiFn = _ffi.Resolve(id.Name);
                if (ffiFn is ICopCallable ffiCallable)
                    return ffiCallable.Invoke([subject], this, env).IsTruthy;
                // Check if it's a bool property on the subject
                if (subject is CopDynamicObject dyn && dyn.HasField(id.Name))
                    return dyn.GetField(id.Name).IsTruthy;
                if (subject is CopObject obj && obj.HasField(id.Name))
                    return obj.GetField(id.Name).IsTruthy;
                // "empty" pseudo-predicate: collection with 0 items
                if (id.Name == "empty")
                    return IsEmpty(subject);
                return false;

            case CallExpr call:
                // :method(args) → call method(subject, args...)
                var methodName = call.Callee switch
                {
                    IdentifierExpr mid => mid.Name,
                    MemberExpr mem => mem.Member,
                    _ => null
                };
                if (methodName is not null)
                {
                    // Built-in :in([...]) membership test
                    if (methodName == "in" && call.Args.Count == 1)
                    {
                        var listVal = ForceValue(Eval(call.Args[0], env));
                        if (listVal is CopList list)
                            return EvalInMembership(subject, list);
                    }

                    var argEnv = env.Extend();
                    argEnv.Define("item", subject);
                    var args = call.Args.Select(a => Eval(a, argEnv)).ToList();
                    var allArgs = new List<CopValue> { subject };
                    allArgs.AddRange(args);

                    if (env.TryLookup(methodName, out var mfn) && mfn is ICopCallable mc)
                        return mc.Invoke(allArgs, this, env).IsTruthy;
                    var mffi = _ffi.Resolve(methodName);
                    if (mffi is ICopCallable mfc)
                        return mfc.Invoke(allArgs, this, env).IsTruthy;
                }
                // Fallback: evaluate the call expression in a context where item is bound
                var itemEnv = env.Extend();
                itemEnv.Define("item", subject);
                return Eval(call, itemEnv).IsTruthy;

            case MemberExpr mem2:
                // :Type.Property → access subject.Property (qualified filter)
                // The member expression path represents a property chain on the subject
                return EvalMemberOnSubject(mem2, subject, env).IsTruthy;

            default:
                // Evaluate predicate expression normally and use its truthiness
                var val = Eval(predicateExpr, env);
                return ApplyPredicate(val, subject, env);
        }
    }

    private CopValue EvalMemberOnSubject(MemberExpr mem, CopValue subject, Environment env)
    {
        // For chains like Type.Methods, resolve from the subject
        if (mem.Object is IdentifierExpr)
        {
            // The identifier is the type qualifier (e.g., "Type" in "Type.Methods")
            // Access the member on the subject directly
            return subject switch
            {
                CopDynamicObject dyn when dyn.HasField(mem.Member) => dyn.GetField(mem.Member),
                CopObject obj when obj.HasField(mem.Member) => obj.GetField(mem.Member),
                _ => CopNull.Instance
            };
        }
        // Nested: evaluate the object part then access member
        var obj2 = Eval(mem.Object, env);
        return obj2 switch
        {
            CopDynamicObject dyn => dyn.GetField(mem.Member),
            CopObject co => co.GetField(mem.Member),
            _ => CopNull.Instance
        };
    }

    private static bool IsEmpty(CopValue value) => value switch
    {
        CopThunk thunk => IsEmpty(thunk.Force()),
        CopList list => list.Items.Count == 0,
        CopLazyCollection lazy => !lazy.Enumerate().Any(),
        CopString str => str.Value.Length == 0,
        CopNull => true,
        _ => false
    };

    /// <summary>
    /// Decides whether a bare `collection:name` filter should run at the COLLECTION level
    /// (invoke once with the whole collection → e.g. `empty`, `distinct`, `pop`) rather than
    /// per item. It is collection-level when a sole-collection-parameter overload (or the builtin
    /// `empty`) applies AND no scalar overload claims the items. A scalar overload claims the items
    /// when its parameter matches the first item's type; for an EMPTY collection (no item to
    /// inspect) the presence of any scalar overload is taken as per-item intent so that, e.g.,
    /// `folders():empty` keeps filtering and yields an empty collection instead of a bool.
    /// </summary>
    private bool ShouldApplyAtCollectionLevel(string name, CopValue collection, Environment env)
    {
        bool isBuiltinEmpty = name == "empty";
        var overloads = env.TryLookup(name, out var binding) ? OverloadsOf(binding) : [];
        bool hasCollectionParam = overloads.Any(o =>
            o.Declaration.Params.Count == 1 && o.Declaration.Params[0].Type is { IsCollection: true });
        if (!hasCollectionParam && !isBuiltinEmpty) return false;

        var scalarOverloads = overloads
            .Where(o => o.Declaration.Params.Count == 1 && o.Declaration.Params[0].Type is { IsCollection: false })
            .ToList();
        if (scalarOverloads.Count == 0) return true; // no per-item interpretation exists

        var itemType = FirstItemTypeName(collection);
        if (itemType is null) return false; // empty collection + a scalar overload → assume per-item

        var registry = TypeRegistry;
        foreach (var o in scalarOverloads)
        {
            var pt = o.Declaration.Params[0].Type!;
            if (string.Equals(pt.Name, itemType, StringComparison.OrdinalIgnoreCase)) return false;
            if (registry is not null && (registry.IsSubtypeOf(itemType, pt.Name) || registry.ConformsTo(itemType, pt.Name)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// User-function overloads bound to a value (single function or function group).
    /// FFI builtins (CopExternalFunction) are intentionally excluded.
    /// </summary>
    private static List<CopFunction> OverloadsOf(CopValue binding) => binding switch
    {
        CopFunction f => [f],
        CopFunctionGroup g => g.Functions.ToList(),
        _ => []
    };

    /// <summary>
    /// True if a filter predicate expression resolves to a user-defined, non-intrinsic predicate or
    /// function (i.e. one with a real body, not a provider property or builtin). Such predicates
    /// cannot be pushed down to a provider as a property filter and must be evaluated per item.
    /// </summary>
    private static bool ResolvesToUserPredicate(Expression pred, Environment env)
    {
        string? name = pred switch
        {
            IdentifierExpr id => id.Name,
            CallExpr { Callee: IdentifierExpr cid } => cid.Name,
            _ => null
        };
        if (name is null || !env.TryLookup(name, out var v)) return false;
        return v switch
        {
            CopFunction f => f.Declaration.Body is not IntrinsicBody,
            CopFunctionGroup g => g.Functions.Any(o => o.Declaration.Body is not IntrinsicBody),
            _ => false
        };
    }

    /// <summary>
    /// True if `name` resolves to a user function of arity (1 + argCount) whose FIRST parameter is
    /// a collection type — e.g. `containsAny(items:[string], values:[string])`. Such a
    /// `coll:name(args)` call is collection-level: invoke once with the whole collection as the
    /// first argument. If a same-arity overload's scalar first parameter matches the item type,
    /// per-item dispatch is preferred instead.
    /// </summary>
    private bool FirstParamIsCollectionFunction(string name, int argCount, CopValue collection, Environment env)
    {
        if (!env.TryLookup(name, out var binding)) return false;
        var overloads = OverloadsOf(binding);
        bool hasCollectionFirst = overloads.Any(o =>
            o.Declaration.Params.Count == argCount + 1 && o.Declaration.Params[0].Type is { IsCollection: true });
        if (!hasCollectionFirst) return false;

        var itemType = FirstItemTypeName(collection);
        if (itemType is null) return true;
        var registry = TypeRegistry;
        foreach (var o in overloads)
        {
            if (o.Declaration.Params.Count != argCount + 1) continue;
            var pt = o.Declaration.Params[0].Type;
            if (pt is null || pt.IsCollection) continue;
            if (string.Equals(pt.Name, itemType, StringComparison.OrdinalIgnoreCase)) return false;
            if (registry is not null && (registry.IsSubtypeOf(itemType, pt.Name) || registry.ConformsTo(itemType, pt.Name)))
                return false;
        }
        return true;
    }

    /// <summary>
    /// The overload (if any) whose sole parameter is a collection type — e.g.
    /// `empty(items:[T])`, `distinct(items:[T])`. Used to invoke a bare `:name` filter at the
    /// collection level rather than per item.
    /// </summary>
    private static CopFunction? CollectionParamOverload(CopValue binding)
        => OverloadsOf(binding).FirstOrDefault(o =>
            o.Declaration.Params.Count == 1 && o.Declaration.Params[0].Type is { IsCollection: true });

    /// <summary>Type name of the first item in a collection, or null if empty/unknown.</summary>
    private string? FirstItemTypeName(CopValue collection)
    {
        var first = CoerceToEnumerable(collection).FirstOrDefault();
        if (first is CopThunk t) first = t.Force();
        return first switch
        {
            CopDynamicObject dyn => dyn.TypeName,
            CopObject obj => obj.TypeName,
            CopString => "string",
            CopInt => "int",
            CopNumber => "float",
            CopBool => "bool",
            CopList or CopLazyCollection => "collection",
            _ => null
        };
    }

    private static bool EvalInMembership(CopValue subject, CopList list)
    {
        var subjectStr = subject is CopString cs ? cs.Value
            : subject is CopNull ? ""
            : subject?.Display() ?? "";
        foreach (var item in list.Items)
        {
            var itemStr = item is CopString s ? s.Value
                : item is CopNull ? ""
                : item?.Display() ?? "";
            if (subjectStr.Equals(itemStr, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static CopValue GetFieldOnValue(CopValue value, string fieldName) => value switch
    {
        CopDynamicObject dyn when dyn.HasField(fieldName) => dyn.GetField(fieldName),
        CopObject obj when obj.HasField(fieldName) => obj.GetField(fieldName),
        _ => CopNull.Instance
    };

    // ========================================================================
    // Call Dispatch (public for ICopCallable implementations)
    // ========================================================================

    /// <summary>
    /// Call a user-defined function with given arguments.
    /// </summary>
    public CopValue CallUserFunction(CopFunction func, IReadOnlyList<CopValue> args)
    {
        // Validate arguments before dispatch (arity + parameter types)
        TypeValidator.ValidateArguments(func.Declaration, args, TypeRegistry);

        // First-parameter guard (e.g. `predicate isMissingNamespace(Type:isCSharp)`): a guarded
        // predicate only applies to arguments its guard accepts; for others it is simply false and
        // does NOT run its body. Multi-overload guard dispatch lives in CopFunctionGroup; this
        // covers a single overload bound directly as a CopFunction so the guard isn't ignored
        // (issue #35 — otherwise `isMissingNamespace(Type:isCSharp)` ran on non-C# types too).
        if (args.Count > 0 && func.Declaration.IsPredicate
            && func.Declaration.Params.Count > 0
            && func.Declaration.Params[0].Type?.Constraint is { } guardName)
        {
            if (func.Closure.TryLookup(guardName, out var guardVal) && guardVal is ICopCallable guard
                && !guard.Invoke([args[0]], this, func.Closure).IsTruthy)
                return CopBool.False;
        }

        var funcEnv = func.Closure.Extend();

        // Check if last parameter is a param array (collection-typed)
        var paramCount = func.Declaration.Params.Count;
        bool hasParamArray = paramCount > 0 &&
            func.Declaration.Params[^1].Type is { IsCollection: true } &&
            args.Count >= paramCount;

        // Don't use param-array collation when a single list arg is passed to match the collection param exactly
        if (hasParamArray && args.Count == paramCount)
        {
            var lastArg = args[^1];
            if (lastArg is CopThunk t) lastArg = t.Force();
            if (lastArg is CopList or CopLazyCollection)
                hasParamArray = false;
        }

        // Bind parameters
        if (hasParamArray)
        {
            // Bind all params except the last normally
            for (int i = 0; i < paramCount - 1 && i < args.Count; i++)
                funcEnv.Define(func.Declaration.Params[i].Name, args[i]);

            // Collect remaining args into the last param as a CopList
            var paramArrayItems = new List<CopValue>();
            for (int i = paramCount - 1; i < args.Count; i++)
                paramArrayItems.Add(args[i]);
            funcEnv.Define(func.Declaration.Params[^1].Name, new CopList(paramArrayItems));
        }
        else
        {
            for (int i = 0; i < paramCount && i < args.Count; i++)
                funcEnv.Define(func.Declaration.Params[i].Name, args[i]);

            // If fewer args than params, bind remaining to null
            for (int i = args.Count; i < paramCount; i++)
                funcEnv.Define(func.Declaration.Params[i].Name, CopNull.Instance);
        }

        // Bind "item" to first argument (Cop convention: item references the context object)
        if (args.Count > 0)
            funcEnv.Define("item", args[0]);

        // TRACE: debug isPublic
        if (func.Declaration.Name == "isPublic" && _traceLog != null)
        {
            var paramName = func.Declaration.Params.Count > 0 ? func.Declaration.Params[0].Name : "(none)";
            var argDisplay = args.Count > 0 ? $"{args[0].GetType().Name}" : "(no args)";
            _traceLog($"[trace] CallUserFunction: isPublic, param='{paramName}', arg={argDisplay}");
            if (args.Count > 0 && args[0] is CopDynamicObject dobj)
            {
                var mods = dobj.GetField("Modifiers");
                _traceLog($"[trace]   arg.Modifiers = {mods?.GetType().Name}({mods?.Display()})");
                // Check if Public is in scope
                funcEnv.TryLookup("Public", out var pubVal);
                _traceLog($"[trace]   'Public' in env = {pubVal?.GetType().Name}({pubVal?.Display()})");
            }
        }

        // Evaluate body
        var result = func.Declaration.Body switch
        {
            ExpressionBody eb => Eval(eb.Expr, funcEnv),
            MappingBody mb => EvalMappingBody(mb, funcEnv),
            BlockBody bb => EvalBlockBody(bb, funcEnv),
            IntrinsicBody => CallIntrinsicViaFFI(func.Declaration.Name, args, funcEnv),
            _ => CopNull.Instance
        };

        // Validate return type after execution
        TypeValidator.ValidateReturn(func.Declaration, result);

        return result;
    }

    /// <summary>
    /// Dispatch an intrinsic-body function to the FFI registry.
    /// </summary>
    private CopValue CallIntrinsicViaFFI(string name, IReadOnlyList<CopValue> args, Environment env)
    {
        var ffiFunc = _ffi.Resolve(name);
        if (ffiFunc is ICopCallable callable)
            return callable.Invoke(args, this, env);
        throw new CopEvaluationException(
            $"Intrinsic function '{name}' has no implementation registered in FFI",
            0, _filePath);
    }

    /// <summary>
    /// Call a lambda with given arguments.
    /// </summary>
    public CopValue CallLambda(CopLambda lambda, IReadOnlyList<CopValue> args)
    {
        var lamEnv = lambda.Closure.Extend();

        for (int i = 0; i < lambda.Expr.Params.Count && i < args.Count; i++)
            lamEnv.Define(lambda.Expr.Params[i].Name, args[i]);

        return Eval(lambda.Expr.Body, lamEnv);
    }

    private CopValue EvalMappingBody(MappingBody body, Environment env)
    {
        var fields = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (var mapping in body.Mappings)
            fields[mapping.FieldName] = Eval(mapping.Value, env);
        return new CopObject(fields);
    }

    private CopValue EvalBlockBody(BlockBody body, Environment env)
    {
        CopValue result = CopNull.Instance;
        foreach (var stmt in body.Statements)
        {
            if (stmt is ExpressionStatement es)
                result = Eval(es.Expr, env);
            else
            {
                ExecStatement(stmt, env);
                result = CopNull.Instance;
            }
        }
        return result;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>
    /// Force a value if it is a thunk. Returns the concrete value.
    /// Non-thunks pass through unchanged.
    /// </summary>
    public static CopValue ForceValue(CopValue value) =>
        value is CopThunk thunk ? thunk.Force() : value;

    private bool ApplyPredicate(CopValue predicate, CopValue item, Environment env)
    {
        if (predicate is ICopCallable callable)
        {
            var result = callable.Invoke([item], this, env);
            return result.IsTruthy;
        }

        // If predicate is just a string name, look it up and call
        if (predicate is CopString name && env.TryLookup(name.Value, out var fn) && fn is ICopCallable c)
        {
            var result = c.Invoke([item], this, env);
            return result.IsTruthy;
        }

        // Truthiness test as fallback
        return predicate.IsTruthy;
    }

    private IEnumerable<CopValue> CoerceToEnumerable(CopValue value)
    {
        // Force thunks before coercing
        if (value is CopThunk thunk)
            value = thunk.Force();

        // Materialize queryable collections on demand
        if (value is CopQueryable queryable)
            return queryable.Enumerate();

        if (value is CopQueryableProperty queryProp)
            return queryProp.Source.Enumerate();

        return value switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => lazy.Enumerate(),
            CopNull => Enumerable.Empty<CopValue>(),
            _ => [value] // Single item as collection of one
        };
    }

    private bool MatchPattern(Pattern pattern, CopValue value, Environment env)
    {
        return pattern switch
        {
            WildcardPattern => true,
            LiteralPattern lp => ValuesEqual(EvalLiteral(new LiteralExpr(lp.Value)), value),
            IdentifierPattern ip => BindPatternVar(ip, value, env),
            _ => false
        };
    }

    private bool BindPatternVar(IdentifierPattern pattern, CopValue value, Environment env)
    {
        // Identifier patterns always match and bind the value
        env.Define(pattern.Name, value);
        return true;
    }

    /// <summary>
    /// If value is a callable (bare predicate reference in boolean context), auto-invoke with "item".
    /// E.g., `isAbstract && ...` becomes `isAbstract(item) && ...`
    /// </summary>
    private CopValue CoerceCallableToBool(CopValue value, Environment env)
    {
        if (value is ICopCallable callable && env.TryLookup("item", out var item))
            return callable.Invoke([item], this, env);
        return value;
    }

    private static bool ValuesEqual(CopValue a, CopValue b)
    {
        return (a, b) switch
        {
            (CopNull, CopNull) => true,
            (CopBool ba, CopBool bb) => ba.Value == bb.Value,
            (CopInt ia, CopInt ib) => ia.Value == ib.Value,
            (CopNumber na, CopNumber nb) => Math.Abs(na.Value - nb.Value) < double.Epsilon,
            (CopInt ia, CopNumber nb) => Math.Abs(ia.Value - nb.Value) < double.Epsilon,
            (CopNumber na, CopInt ib) => Math.Abs(na.Value - ib.Value) < double.Epsilon,
            (CopString sa, CopString sb) => sa.Value == sb.Value,
            // Provider proxy names compare as strings (enum members like 'csharp' may be
            // shadowed by provider proxies named 'csharp' — treat proxy as its name string)
            (CopString sa, CopProviderProxy pp) => sa.Value == pp.ProviderName,
            (CopProviderProxy pp, CopString sa) => pp.ProviderName == sa.Value,
            _ => ReferenceEquals(a, b)
        };
    }

    private CopValue? NumericOp(CopValue l, CopValue r, Func<double, double, double> op, int line)
    {
        var (ln, rn) = (ToNumber(l), ToNumber(r));
        if (ln is null || rn is null) return null;

        var result = op(ln.Value, rn.Value);

        // Return int if both inputs were int and result is integral
        if (l is CopInt && r is CopInt && result == Math.Floor(result) && result is >= int.MinValue and <= int.MaxValue)
            return new CopInt((int)result);

        return new CopNumber(result);
    }

    private CopValue StringConcat(CopValue l, CopValue r, int line)
    {
        if (l is CopString || r is CopString)
            return new CopString(l.Display() + r.Display());
        throw new CopEvaluationException("Cannot add non-numeric, non-string values", line, _filePath);
    }

    private CopValue? CollectionConcat(CopValue l, CopValue r)
    {
        // Force thunks before collection concat
        if (l is CopThunk lt) l = lt.Force();
        if (r is CopThunk rt) r = rt.Force();

        var left = AsEnumerable(l);
        var right = AsEnumerable(r);
        if (left is null || right is null) return null;
        return new CopList(left.Concat(right).ToList());

        static IEnumerable<CopValue>? AsEnumerable(CopValue v) => v switch
        {
            CopList list => list.Items,
            CopLazyCollection lazy => lazy.Enumerate(),
            _ => null
        };
    }

    private CopValue CompareOp(CopValue l, CopValue r, Func<int, bool> predicate, int line)
    {
        var (ln, rn) = (ToNumber(l), ToNumber(r));
        if (ln is not null && rn is not null)
            return CopBool.Of(predicate(ln.Value.CompareTo(rn.Value)));

        if (l is CopString ls && r is CopString rs)
            return CopBool.Of(predicate(string.Compare(ls.Value, rs.Value, StringComparison.Ordinal)));

        return CopBool.False;
    }

    private static double? ToNumber(CopValue v) => v switch
    {
        CopInt i => i.Value,
        CopNumber n => n.Value,
        _ => null
    };

    /// <summary>
    /// Register a function, creating a function group if there's already a function with the same name.
    /// </summary>
    private void RegisterFunctionWithOverloading(string name, CopFunction func)
    {
        if (_globalEnv.TryLookup(name, out var existing))
        {
            if (existing is CopFunctionGroup group)
            {
                group.Add(func);
                return;
            }
            if (existing is CopFunction existingFunc)
            {
                var newGroup = new CopFunctionGroup(name);
                newGroup.Add(existingFunc);
                newGroup.Add(func);
                _globalEnv.Define(name, newGroup);
                return;
            }
        }
        _globalEnv.Define(name, func);
    }
}

/// <summary>
/// Callable wrapper for command declarations (statement-block bodies).
/// </summary>
internal sealed class CopCommandFunction : CopValue, ICopCallable
{
    private readonly CommandDecl _decl;
    private readonly Environment _closure;

    public CopCommandFunction(CommandDecl decl, Environment closure)
    {
        _decl = decl;
        _closure = closure;
    }

    public int Arity => _decl.Parameters?.Count ?? 0;

    public CopValue Invoke(IReadOnlyList<CopValue> args, Evaluator evaluator, Environment env)
    {
        var cmdEnv = _closure.Extend();

        // Bind command parameters
        if (_decl.Parameters is not null)
        {
            for (int i = 0; i < _decl.Parameters.Count && i < args.Count; i++)
                cmdEnv.Define(_decl.Parameters[i], args[i]);
        }

        // Execute all but the last statement, then return last expression's value
        CopValue result = CopNull.Instance;
        foreach (var stmt in _decl.Body)
        {
            if (stmt is ExpressionStatement es)
                result = evaluator.Eval(es.Expr, cmdEnv);
            else
            {
                evaluator.ExecStatement(stmt, cmdEnv);
                result = CopNull.Instance;
            }
        }
        return result;
    }

    public override string Display() => $"<command {_decl.Name}>";
    public override string ToString() => Display();
}
