// Tree-walking evaluator for the Cop language AST.

using Cop.Core;

namespace Cop.Lang.Interpreter;

using System.Text;
using Cop.Lang.Ast;

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

    /// <summary>
    /// The root environment containing built-ins and loaded declarations.
    /// </summary>
    public Environment GlobalEnvironment => _globalEnv;
    /// <summary>
    /// The registry used to resolve and invoke foreign functions.
    /// </summary>
    public ForeignFunctionRegistry FFI => _ffi;
    /// <summary>
    /// An optional sink for per-expression evaluation diagnostics.
    /// </summary>
    public Action<string>? TraceLog { get => _traceLog; set => _traceLog = value; }
    /// <summary>
    /// The optional registry used for runtime type resolution and validation.
    /// </summary>
    public TypeRegistry? TypeRegistry { get; set; }
    /// <summary>
    /// An optional notification invoked after a let binding is evaluated.
    /// </summary>
    public Action<LetDecl>? LetBindingEvaluated { get; set; }
    /// <summary>
    /// An optional notification invoked when an entry command finishes.
    /// </summary>
    public Action? EntryCommandCompleted { get; set; }

    /// <summary>
    /// The single implicit iteration/subject variable bound inside foreach bodies, pipelines,
    /// per-element transforms, computed properties and filter predicates. Centralized here so the
    /// name exists in exactly one place (it was previously a magic string literal at 12+ sites).
    /// </summary>
    public const string ImplicitItemVariable = "item";

    public Evaluator(ForeignFunctionRegistry? ffi = null, string? filePath = null)
    {
        _ffi = ffi ?? new ForeignFunctionRegistry();
        _globalEnv = new Environment();
        _filePath = filePath;

        StandardLibrary.RegisterPackageFunctions(this);
        // Explicit runtime registrations can override package defaults.
        _ffi.PopulateEnvironment(_globalEnv);
    }

    // ========================================================================
    // Module Evaluation
    // ========================================================================

    /// <summary>
    /// Evaluate a module: registers all top-level declarations in the global environment.
    /// </summary>
    public async ValueTask EvalModuleAsync(ModuleNode module, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (Declaration decl in module.Declarations)
        {
            if (decl is LetDecl ld)
            {
                CopValue value = ld.Value is InterpolatedStringExpr
                    && !_globalEnv.TryLookup(ImplicitItemVariable, out _)
                    ? CreateLetBinding(ld, _globalEnv)
                    : (await EvalLetBindingAsync(ld, _globalEnv, cancellationToken: cancellationToken));
                _globalEnv.Define(ld.Name, value);
            }
            else
            {
                RegisterDeclaration(decl);
            }
        }
    }

    /// <summary>
    /// Phase 1: Register functions, types, enums — everything EXCEPT let bindings.
    /// Used when data providers haven't been loaded yet.
    /// </summary>
    public void RegisterDeclarations(ModuleNode module)
    {
        foreach (Declaration decl in module.Declarations)
        {
            if (decl is LetDecl)
            {
                continue; // deferred to EvalLetBindings
            }

            RegisterDeclaration(decl);
        }
    }

    /// <summary>
    /// Phase 2: Register all let bindings in a module as lazy thunks.
    /// Called after providers have registered their data.
    /// The bindings are only evaluated when first accessed.
    /// </summary>
    public async ValueTask EvalLetBindingsAsync(ModuleNode module, Dictionary<string, CopValue>? originalCallables = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (Declaration decl in module.Declarations)
        {
            if (decl is LetDecl ld)
            {
                // If this let shadows a function/callable with the same name, evaluate eagerly
                // to avoid self-referencing thunk (e.g., let codebase = codebase(csharp))
                bool isCallableShadow = false;
                if (_globalEnv.TryLookup(ld.Name, out CopValue? existing) && existing is ICopCallable)
                {
                    isCallableShadow = true;
                }
                else if (originalCallables != null && originalCallables.ContainsKey(ld.Name))
                {
                    isCallableShadow = true;
                }

                if (isCallableShadow)
                {
                    // Restore the original callable before evaluating the RHS so the call resolves correctly.
                    // This handles the case where a previous sibling module already replaced the callable with its result.
                    if (originalCallables != null && originalCallables.TryGetValue(ld.Name, out CopValue? originalCallable))
                    {
                        _globalEnv.Define(ld.Name, originalCallable);
                    }

                    CopValue value = (await EvalLetBindingAsync(ld, _globalEnv, cancellationToken: cancellationToken));
                    _globalEnv.Define(ld.Name, value);
                }
                else
                {
                    _globalEnv.Define(ld.Name, CreateLetBinding(ld, _globalEnv));
                }
            }
        }
    }

    /// <summary>
    /// Evaluates a let declaration using its declared type and supplied environment.
    /// </summary>
    public async ValueTask<CopValue> EvalLetBindingAsync(LetDecl declaration, Environment environment, CancellationToken cancellationToken = default)
    {
        CopValue value = (await EvalAsync(declaration.Value, environment, cancellationToken: cancellationToken));
        LetBindingEvaluated?.Invoke(declaration);
        return value;
    }

    internal CopThunk CreateLetBinding(LetDecl declaration, Environment environment) =>
        CreateBinding(declaration.Value, environment,
            (scope, cancellationToken) => EvalLetBindingAsync(declaration, scope, cancellationToken));

    private static CopThunk CreateBinding(Expression expression, Environment environment,
        Func<Environment, CancellationToken, ValueTask<CopValue>> evaluate) =>
        new(async cancellationToken => (await evaluate(environment, cancellationToken)))
    {
        ConstantExpression = expression,
        ConstantEnvironment = environment,
        EvaluateInEnvironment = evaluate
    };

    /// <summary>
    /// Run a named command/function from the global environment.
    /// Tries exact name first, then ALL-UPPERCASE form (since command → function uppercases names).
    /// </summary>
    public async ValueTask<CopValue> RunCommandAsync(string name, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_globalEnv.TryLookup(name, out CopValue value))
        {
            // Try uppercase form (command desugaring uppercases names)
            string upper = name.ToUpperInvariant();
            if (!_globalEnv.TryLookup(upper, out value))
            {
                // Try lowercase form (let-binding collections used as runnable rules)
                string lower = name.ToLowerInvariant();
                if (!_globalEnv.TryLookup(lower, out value))
                {
                    throw new CopEvaluationException($"Command '{name}' not found", filePath: _filePath);
                }
            }
        }

        // Force thunks — let bindings used as runnable rules need evaluation
        value = (await ForceValueAsync(value, cancellationToken: cancellationToken));

        _traceLog?.Invoke($"[trace] RunCommand: found '{name}' as {value.GetType().Name}");

        if (value is CopFunction func && func.Declaration is not null)
        {
            _traceLog?.Invoke($"[trace] RunCommand: calling function '{func.Declaration.Name}' with body type={func.Declaration.Body?.GetType().Name}");
            CopValue result = (await CallUserFunctionAsync(func, [], cancellationToken: cancellationToken));
            EntryCommandCompleted?.Invoke();
            return result;
        }

        if (value is ICopCallable callable)
        {
            CopValue result = (await callable.InvokeAsync([], this, _globalEnv, cancellationToken: cancellationToken));
            EntryCommandCompleted?.Invoke();
            return result;
        }

        // If it's a collection/value, return it directly (let-binding used as a named rule)
        return value;
    }

    // ========================================================================
    // Declaration Evaluation
    // ========================================================================

    private void RegisterDeclaration(Declaration decl)
    {
        switch (decl)
        {
            case FunctionDecl fd:
                CopFunction func = new CopFunction(fd, _globalEnv);
                RegisterFunctionWithOverloading(fd.Name, func);
                break;

            case CommandDecl cd:
                // Wrap command as a function for uniform call dispatch
                CopFunction cmdFunc = new CopFunction(
                    new FunctionDecl(cd.Name, [], null,
                        new ExpressionBody(new LiteralExpr(null)),
                        cd.IsExported, null, cd.DocComment, cd.Line),
                    _globalEnv);
                // Store a special callable that executes the command body
                CopCommandFunction cmdCallable = new CopCommandFunction(cd, _globalEnv);
                _globalEnv.Define(cd.Name, cmdCallable);
                break;

            case EnumDecl ed:
                // Register enum members as string constants
                foreach (string member in ed.Members)
                {
                    _globalEnv.Define(member, new CopString(member));
                }
                // Register enum type name as a constructor: TypeKind('value') → CopString
                _globalEnv.Define(ed.Name, new CopEnumConstructor(ed.Name));
                break;

            case FlagsDecl fd2:
                // Register flags members as power-of-2 integer values
                for (int i = 0; i < fd2.Members.Count; i++)
                {
                    _globalEnv.Define(fd2.Members[i], new CopInt(1 << i));
                }

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

    internal async ValueTask ExecStatementsAsync(List<Statement> statements, Environment env, CancellationToken cancellationToken = default)
    {
        foreach (Statement stmt in statements)
        {
            await ExecStatementAsync(stmt, env, cancellationToken: cancellationToken);
        }
    }

    internal async ValueTask ExecStatementAsync(Statement stmt, Environment env, CancellationToken cancellationToken = default)
    {
        switch (stmt)
        {
            case LetStatement ls:
                CopValue val = ls.Value is InterpolatedStringExpr
                    && !env.TryLookup(ImplicitItemVariable, out _)
                    ? CreateBinding(ls.Value, env,
                        (scope, cancellationToken) => EvalAsync(ls.Value, scope, cancellationToken))
                    : (await EvalAsync(ls.Value, env, cancellationToken: cancellationToken));
                env.Define(ls.Name, val);
                break;

            case ForEachStatement fs:
                await ExecForEachAsync(fs, env, cancellationToken: cancellationToken);
                break;

            case ExpressionStatement es:
                await EvalAsync(es.Expr, env, cancellationToken: cancellationToken);
                break;

            case PipelineStatement ps:
                await ExecPipelineAsync(ps, env, cancellationToken: cancellationToken);
                break;
        }
    }

    private async ValueTask ExecForEachAsync(ForEachStatement stmt, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue collection = (await EvalAsync(stmt.Collection, env, cancellationToken: cancellationToken));
        // Named subset resolution: predicate Clients(Types) used as foreach source
        collection = (await TryResolveNamedSubsetAsync(collection, env, cancellationToken: cancellationToken));
        _traceLog?.Invoke($"[trace] ExecForEach: {ExpressionRenderer.Render(stmt.Collection)}  (collection={DescribeValue(collection)}, body stmts={stmt.Body.Count})");
        IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken);
        int count = 0;

        await foreach (CopValue item in items)
        {
            count++;
            Environment iterEnv = env.Extend();
            iterEnv.Define(ImplicitItemVariable, item);
            iterEnv.Define(stmt.Variable, item);

            foreach (Statement bodyStmt in stmt.Body)
            {
                if (bodyStmt is ExpressionStatement es)
                {
                    // In foreach, expression results are auto-printed (implicit output)
                    CopValue? result = (await EvalAsync(es.Expr, iterEnv, cancellationToken: cancellationToken));
                    if (count == 1)
                    {
                        _traceLog?.Invoke($"[trace] ExecForEach first body result: {result?.GetType().Name} = {result?.Display()?.Substring(0, Math.Min(result?.Display()?.Length ?? 0, 80))}");
                    }

                    if (result is not CopNull && result is not null)
                    {
                        await CallPrintAsync(result, iterEnv, cancellationToken: cancellationToken);
                    }
                }
                else
                {
                    await ExecStatementAsync(bodyStmt, iterEnv, cancellationToken: cancellationToken);
                }
            }
        }
        _traceLog?.Invoke($"[trace] ExecForEach iterated {count} items");
    }

    private async ValueTask CallPrintAsync(CopValue value, Environment env, CancellationToken cancellationToken = default)
    {
        if (env.TryLookup("print", out CopValue? printFn) && printFn is ICopCallable callable)
        {
            await callable.InvokeAsync([value], this, env, cancellationToken: cancellationToken);
        }
    }

    private async ValueTask ExecPipelineAsync(PipelineStatement stmt, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue source = (await EvalAsync(stmt.Source, env, cancellationToken: cancellationToken));
        IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(source, cancellationToken: cancellationToken);

        await foreach (CopValue item in items)
        {
            Environment pipeEnv = env.Extend();
            pipeEnv.Define(ImplicitItemVariable, item);

            CopValue current = item;
            foreach (PipelineStage stage in stmt.Stages)
            {
                current = (await EvalAsync(stage.Expr, pipeEnv, cancellationToken: cancellationToken));
            }

            // Final pipeline result is auto-printed
            if (current is not CopNull && current is not null)
            {
                await CallPrintAsync(current, pipeEnv, cancellationToken: cancellationToken);
            }
        }
    }

    // ========================================================================
    // Expression Evaluation
    // ========================================================================

    /// <summary>
    /// Evaluates an expression in the supplied lexical environment.
    /// </summary>
    public async ValueTask<CopValue> EvalAsync(Expression expr, Environment env, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return expr switch
        {
            LiteralExpr lit => EvalLiteral(lit),
            IdentifierExpr id => EvalIdentifier(id, env),
            BinaryExpr bin => (await EvalBinaryAsync(bin, env, cancellationToken: cancellationToken)),
            UnaryExpr un => (await EvalUnaryAsync(un, env, cancellationToken: cancellationToken)),
            CallExpr call => (await EvalCallAsync(call, env, cancellationToken: cancellationToken)),
            MemberExpr mem => (await EvalMemberAsync(mem, env, cancellationToken: cancellationToken)),
            IndexExpr idx => (await EvalIndexAsync(idx, env, cancellationToken: cancellationToken)),
            LambdaExpr lam => new CopLambda(lam, env),
            ConditionalExpr cond => (await EvalConditionalAsync(cond, env, cancellationToken: cancellationToken)),
            MatchExpr match => (await EvalMatchAsync(match, env, cancellationToken: cancellationToken)),
            ListExpr list => (await EvalListAsync(list, env, cancellationToken: cancellationToken)),
            ObjectExpr obj => (await EvalObjectAsync(obj, env, cancellationToken: cancellationToken)),
            InterpolatedStringExpr interp => (await EvalInterpolationAsync(interp, env, cancellationToken: cancellationToken)),
            FilterExpr filter => (await EvalFilterAsync(filter, env, cancellationToken: cancellationToken)),
            ForEachExpr fe => (await EvalForEachExprAsync(fe, env, cancellationToken: cancellationToken)),
            _ => throw new CopEvaluationException($"Unknown expression type: {expr.GetType().Name}", expr.Line, _filePath)
        };
    }

    /// <summary>
    /// Evaluate a foreach used in expression position: run the loop for its side effects (the body's
    /// auto-printed results) and yield nil so it can sit as an operand of `&` or similar.
    /// </summary>
    private async ValueTask<CopValue> EvalForEachExprAsync(ForEachExpr fe, Environment env, CancellationToken cancellationToken = default)
    {
        await ExecForEachAsync(fe.Loop, env, cancellationToken: cancellationToken);
        return CopNull.Instance;
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
        if (env.TryLookup(id.Name, out CopValue? value))
        {
            if (value is CopThunk thunk
                && thunk.ConstantEnvironment is { } closure
                && thunk.EvaluateInEnvironment is { } evaluate
                && !closure.TryLookup(ImplicitItemVariable, out _)
                && env.TryLookup(ImplicitItemVariable, out CopValue? item)
                && IsTemplate(thunk))
            {
                return CreateContextualTemplate(thunk, closure, evaluate, item);
            }
            return value;
        }

        // Check FFI registry as fallback (for lazily registered externals)
        CopExternalFunction? ext = _ffi.Resolve(id.Name);
        if (ext is not null)
        {
            return ext;
        }

        throw new CopEvaluationException($"Undefined variable '{id.Name}'", id.Line, _filePath);
    }

    private CopThunk CreateContextualTemplate(CopThunk thunk, Environment closure,
        Func<Environment, CancellationToken, ValueTask<CopValue>> evaluate, CopValue item) =>
        new(async forcingCancellationToken => await thunk.ForceTemplateAsync(
            async renderCancellationToken =>
            {
                Environment scope = closure.Extend();
                // Track actual reads, so nested item scopes and constant templates stay memoized.
                CopThunk contextualItem = new(() => item);
                scope.Define(ImplicitItemVariable, contextualItem);
                CopValue rendered = await ForceValueAsync(await evaluate(scope, renderCancellationToken),
                    cancellationToken: renderCancellationToken);
                return (rendered, !contextualItem.IsForced);
            }, cancellationToken: forcingCancellationToken));

    private static bool IsTemplate(CopThunk thunk)
    {
        HashSet<CopThunk>? aliases = null;
        while (!thunk.IsForced)
        {
            if (thunk.ConstantExpression is InterpolatedStringExpr)
            {
                return true;
            }

            if (thunk.ConstantExpression is not IdentifierExpr alias
                || thunk.ConstantEnvironment is not { } closure
                || !closure.TryLookup(alias.Name, out CopValue? value)
                || value is not CopThunk next)
            {
                return false;
            }

            aliases ??= [];
            if (!aliases.Add(thunk))
            {
                return false;
            }

            thunk = next;
        }
        return false;
    }

    private async ValueTask<CopValue> EvalBinaryAsync(BinaryExpr bin, Environment env, CancellationToken cancellationToken = default)
    {
        // Short-circuit for logical operators
        if (bin.Op == BinaryOp.And)
        {
            CopValue left = (await CoerceCallableToBoolAsync((await ForceValueAsync((await EvalAsync(bin.Left, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)), env, cancellationToken: cancellationToken));
            return (await left.IsTruthyAsync(cancellationToken)) ? (await CoerceCallableToBoolAsync((await ForceValueAsync((await EvalAsync(bin.Right, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)), env, cancellationToken: cancellationToken)) : left;
        }
        if (bin.Op == BinaryOp.Or)
        {
            CopValue left = (await CoerceCallableToBoolAsync((await ForceValueAsync((await EvalAsync(bin.Left, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)), env, cancellationToken: cancellationToken));
            return (await left.IsTruthyAsync(cancellationToken)) ? left : (await CoerceCallableToBoolAsync((await ForceValueAsync((await EvalAsync(bin.Right, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)), env, cancellationToken: cancellationToken));
        }

        CopValue l = (await ForceValueAsync((await EvalAsync(bin.Left, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
        CopValue r = (await ForceValueAsync((await EvalAsync(bin.Right, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));

        return bin.Op switch
        {
            BinaryOp.Add => NumericOp(l, r, (a, b) => a + b, bin.Line)
                            ?? (await CollectionConcatAsync(l, r, cancellationToken: cancellationToken))
                            ?? (await StringConcatAsync(l, r, bin.Line, cancellationToken: cancellationToken)),
            BinaryOp.Subtract => NumericOp(l, r, (a, b) => a - b, bin.Line)
                            ?? (await CollectionDifferenceAsync(l, r, cancellationToken: cancellationToken))
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

    private async ValueTask<CopValue> EvalUnaryAsync(UnaryExpr un, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue operand = (await ForceValueAsync((await EvalAsync(un.Operand, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
        return un.Op switch
        {
            // Coerce a bare predicate reference (a callable) to a bool by invoking it on the
            // current `item`, mirroring how `&&`/`||` handle a bare predicate. Without this,
            // `=> !somePredicate` would negate the (always-truthy) callable and yield false.
            UnaryOp.Not => CopBool.Of(!(await (await CoerceCallableToBoolAsync(operand, env, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken))),
            UnaryOp.Negate => operand switch
            {
                CopInt i => new CopInt(-i.Value),
                CopNumber n => new CopNumber(-n.Value),
                _ => throw new CopEvaluationException("Cannot negate non-numeric value", un.Line, _filePath)
            },
            _ => throw new CopEvaluationException($"Unknown unary operator: {un.Op}", un.Line, _filePath)
        };
    }

    private async ValueTask<CopValue> EvalCallAsync(CallExpr call, Environment env, CancellationToken cancellationToken = default)
    {
        // `nameof(x)` is a compile-time intrinsic: it yields the NAME of its single identifier
        // argument as a string WITHOUT evaluating it. Check authors use it to stamp a violation
        // list with the exact binding name a user subtracts to exclude it (`package - that-name`);
        // because the argument is never evaluated, `let x = ... nameof(x)` is not a self-reference.
        // This is a general language feature (like C#/JS `nameof`), not check-specific machinery.
        if (call.Callee is IdentifierExpr { Name: "nameof" })
        {
            if (call.Args.Count == 1 && call.Args[0] is IdentifierExpr nameArg)
            {
                return new CopString(nameArg.Name);
            }

            throw new CopEvaluationException(
                "nameof expects a single identifier, e.g. nameof(my-check)", call.Line, _filePath);
        }

        // Method call dispatch: obj.method(args) → try method(obj, args) via FFI
        if (call.Callee is MemberExpr mem)
        {
            CopValue obj = (await ForceValueAsync((await EvalAsync(mem.Object, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));

            // Per-item collection transforms (Select/Where/OrderBy) evaluate their
            // argument once per element with `item` bound (Cop convention), so they must
            // run before the generic eager-argument dispatch below — which evaluates args
            // in the outer environment and would fail to resolve `item`.
            if (IsCollectionValue(obj) &&
                (await FindPerItemTransformAsync(obj, mem.Member, call.Args, env, cancellationToken: cancellationToken)) is { } transformResult)
            {
                return transformResult is CopQueryable ? transformResult : QuerySource.TrackLocal(obj, transformResult,
                    source => call with { Callee = mem with { Object = source } }, $"{mem.Member} executes locally");
            }

            // String transforms that take arguments (Name.Trim('Async'), Name.Replace('a','b')).
            // Argument-less string members (Lower/Upper/Words/...) are handled as member access in
            // EvalStringMember; these argument-taking forms must be handled here in call position.
            if (obj is CopString strSubject &&
                (await FindStringMethodAsync(strSubject, mem.Member, call.Args, env, cancellationToken: cancellationToken)) is { } strResult)
            {
                return strResult;
            }

            List<CopValue> args = await EvalArgumentsAsync(call.Args, env, cancellationToken);

            // First, try resolving as a member that is callable
            CopValue? memberVal = (await TryGetMemberAsync(obj, mem.Member, cancellationToken: cancellationToken));
            if (memberVal is ICopCallable memberCallable)
            {
                return LocalCall((await InvokeWithLineAsync(memberCallable, args, env, call.Line, cancellationToken: cancellationToken)));
            }

            // Method dispatch: look up method name as an FFI/env function, pass obj as first arg
            if (env.TryLookup(mem.Member, out CopValue? fn) && fn is ICopCallable methodFn)
            {
                List<CopValue> methodArgs = new List<CopValue> { obj };
                methodArgs.AddRange(args);
                return LocalCall((await InvokeWithLineAsync(methodFn, methodArgs, env, call.Line, cancellationToken: cancellationToken)));
            }

            // Try FFI registry directly
            CopExternalFunction? ffiMethod = _ffi.Resolve(mem.Member);
            if (ffiMethod is ICopCallable ffiFn)
            {
                List<CopValue> methodArgs = new List<CopValue> { obj };
                methodArgs.AddRange(args);
                return LocalCall((await InvokeWithLineAsync(ffiFn, methodArgs, env, call.Line, cancellationToken: cancellationToken)));
            }

            throw new CopEvaluationException(
                $"Cannot call '{mem.Member}' on a {(await CopTypeNameOfAsync(obj, cancellationToken: cancellationToken))} value",
                call.Line, _filePath);

            CopValue LocalCall(CopValue result) => QuerySource.TrackLocal(obj, result,
                source => call with { Callee = mem with { Object = source } }, $"{mem.Member} executes locally");
        }

        CopValue callee = (await ForceValueAsync((await EvalAsync(call.Callee, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
        List<CopValue> callArgs = await EvalArgumentsAsync(call.Args, env, cancellationToken);

        // Currying / partial application: a DIRECT call that extends a closure, or under-applies an
        // item-parameter function (its leading parameter is a type the filter fills per element),
        // produces a partial-application closure rather than executing. The closure is completed per
        // item when used in a filter chain (see CopClosure). Filter calls bypass this path — they
        // resolve the callable in ApplyFilterPredicate* and invoke it with the item directly.
        if (callee is CopClosure closure)
        {
            return closure.WithExplicit(callArgs);
        }
        // A user function may be bound as a function group; pick an item-parameter overload that
        // this call under-applies, and curry it.
        CopFunction? curryTarget = callee switch
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
            {
                return new CopClosure(curryTarget, callArgs, explicitArity);
            }
        }

        if (callee is ICopCallable callable)
        {
            CopValue result = (await InvokeWithLineAsync(callable, callArgs, env, call.Line, cancellationToken: cancellationToken));
            if (call.Callee is IdentifierExpr { Name: "count" or "first" or "last" or "take" or "skip" } function
                && callArgs.Count > 0)
            {
                return QuerySource.TrackLocal(callArgs[0], result,
                    source => call with { Args = [source, .. call.Args.Skip(1)] }, $"{function.Name} executes locally");
            }

            return result;
        }

        throw new CopEvaluationException(
            $"Value of type {(await CopTypeNameOfAsync(callee, cancellationToken: cancellationToken))} is not callable",
            call.Line, _filePath);
    }

    /// <summary>
    /// Invokes a callable, re-attaching the call site's line to any error thrown without a location
    /// (FFI builtins such as read()/fail() throw with Line == 0). This gives users a real line number
    /// instead of "line 0".
    /// </summary>
    private async ValueTask<CopValue> InvokeWithLineAsync(ICopCallable callable, List<CopValue> args, Environment env, int line, CancellationToken cancellationToken = default)
    {
        try
        {
            return (await callable.InvokeAsync(args, this, env, cancellationToken: cancellationToken));
        }
        catch (CopEvaluationException ex) when (ex.Line == 0 && line > 0)
        {
            throw new CopEvaluationException(ex.RawMessage, line, _filePath);
        }
    }

    /// <summary>
    /// True if the function's leading parameter is an implicit item parameter — a type-named
    /// parameter (uppercase first letter) that a filter fills per element rather than the caller.
    /// Used to skip that parameter when currying so `format('[')` binds the first EXPLICIT param.
    /// </summary>
    private static bool IsItemParameterFirst(CopFunction fn)
    {
        if (fn.Declaration.Params.Count == 0)
        {
            return false;
        }

        string name = fn.Declaration.Params[0].Name;
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
    private async ValueTask<CopValue?> FindPerItemTransformAsync(CopValue collection, string member,
        IReadOnlyList<Expression> argExprs, Environment env, CancellationToken cancellationToken = default)
    {
        async ValueTask<CopValue> TransformAsync(CopLazyCollection deferred) => collection is CopList
            ? new CopList(await deferred.EnumerateAsync(cancellationToken).ToListAsync(cancellationToken))
            : deferred;
        CopValue result = CopNull.Instance;
        switch (member)
        {
            case "Select" or "select":
            {
                if (argExprs.Count != 1)
                    {
                        return null;
                    }

                    result = await TransformAsync(new CopLazyCollection(deferredCancellationToken =>
                        CoerceToEnumerableAsync(collection, deferredCancellationToken)
                    .Select(async (item, itemCancellationToken) =>
                        await ForceValueAsync(await EvalElementExprAsync(argExprs[0], item, env,
                            itemCancellationToken), itemCancellationToken))));
                return result;
            }
            case "Where" or "where":
            {
                if (argExprs.Count != 1)
                    {
                        return null;
                    }

                    if (collection is CopQueryable query)
                {
                        ResolvedQueryPredicate resolved = ResolvedQueryPredicate.Resolve(
                            argExprs[0], env, query.ItemType, QueryPredicateContext.Element);
                    if (PredicateCompiler.CompileElementOrDefault(resolved.Expression, resolved.Environment, query.ItemType) is { } compiled
                        && query.CanPushFilter(compiled))
                    {
                        result = query.WithFilter(compiled, new QueryStep(resolved.Expression, resolved.Definitions, Method: member));
                        return result;
                    }
                }
                result = await TransformAsync(new CopLazyCollection(deferredCancellationToken =>
                    CoerceToEnumerableAsync(collection, deferredCancellationToken).Where(async (item, itemCancellationToken) =>
                {
                    CopValue value = (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: itemCancellationToken)), cancellationToken: itemCancellationToken));
                    return value is ICopCallable callable ? (await (await callable.InvokeAsync([item], this, env, cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken)) : (await value.IsTruthyAsync(itemCancellationToken));
                })));
                return result;
            }
            case "OrderBy" or "orderBy" or "OrderByDescending" or "orderByDescending":
            {
                if (argExprs.Count != 1)
                    {
                        return null;
                    }

                    bool descending = member.Contains("Descending", StringComparison.OrdinalIgnoreCase);
                result = await TransformAsync(new CopLazyCollection(async deferredCancellationToken =>
                {
                    List<(CopValue Item, CopValue Key)> keyed = (await CoerceToEnumerableAsync(collection, cancellationToken: deferredCancellationToken).Select(async (item, itemCancellationToken) =>
                        (Item: item, Key: (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: itemCancellationToken)), cancellationToken: itemCancellationToken)))).ToListAsync(cancellationToken: cancellationToken));
                    await keyed.SortAsync(async (a, b, comparisonCancellationToken) => (await CompareValuesAsync(a.Key, b.Key, cancellationToken: comparisonCancellationToken)) * (descending ? -1 : 1), cancellationToken: deferredCancellationToken);
                    return keyed.Select(k => k.Item);
                }));
                return result;
            }
            case "any" or "all" or "none" or "count":
            {
                    // Aggregates over an inline element expression (e.g. items.any(item > 2)).
                    CopValue? agg = (await TryEvalAggregateAsync(member, collection, argExprs, negated: false, env, cancellationToken: cancellationToken));
                if (agg is null)
                    {
                        return null;
                    }

                    result = agg;
                return result;
            }
            case "ElementAt" or "elementAt":
            {
                if (argExprs.Count != 1)
                    {
                        return null;
                    }

                    List<CopValue> items = (await CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
                    CopValue idxVal = (await ForceValueAsync((await EvalAsync(argExprs[0], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
                int idx = idxVal is CopInt ci ? ci.Value : -1;
                result = idx >= 0 && idx < items.Count ? items[idx] : CopNull.Instance;
                return result;
            }
            case "Distinct" or "distinct":
            {
                    // Distinct() dedupes by value; Distinct(expr) dedupes by a per-item key expression.
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    List<CopValue> outp = new List<CopValue>();
                await foreach (CopValue item in CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken))
                {
                        CopValue key = argExprs.Count == 1 ? (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)) : item;
                    if (seen.Add((await key.DisplayAsync(cancellationToken: cancellationToken))))
                        {
                            outp.Add(item);
                        }
                    }
                result = new CopList(outp);
                return result;
            }
            case "Sum" or "sum" or "Average" or "average":
            {
                double total = 0; int n = 0;
                await foreach (CopValue item in CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken))
                {
                        CopValue v = argExprs.Count == 1 ? (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)) : item;
                    total += ToNumber(v) ?? 0; n++;
                }
                bool isAvg = member.Equals("Average", StringComparison.OrdinalIgnoreCase) || member == "average";
                // The average of an empty collection is undefined → null (Sum stays 0).
                if (isAvg && n == 0) { result = CopNull.Instance; return result; }
                double val = isAvg ? total / n : total;
                result = !isAvg && val == Math.Floor(val) ? new CopInt((int)val) : new CopNumber(val);
                return result;
            }
            case "Min" or "min" or "Max" or "max":
            {
                bool isMax = member.Equals("Max", StringComparison.OrdinalIgnoreCase) || member == "max";
                double? best = null;
                await foreach (CopValue item in CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken))
                {
                        CopValue v = argExprs.Count == 1 ? (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)) : item;
                        double? num = ToNumber(v);
                    if (num is null)
                        {
                            continue;
                        }

                        best = best is null ? num : (isMax ? Math.Max(best.Value, num.Value) : Math.Min(best.Value, num.Value));
                }
                if (best is null) { result = CopNull.Instance; return result; }
                result = best.Value == Math.Floor(best.Value) ? new CopInt((int)best.Value) : new CopNumber(best.Value);
                return result;
            }
            case "GroupBy" or "groupBy":
            {
                // Group items by a per-item key expression. Returns a list of Group objects, each
                // with Key, Items, and Count properties (documented in language-reference.md).
                if (argExprs.Count != 1)
                    {
                        return null;
                    }

                    List<(CopValue Key, List<CopValue> Items)> order = new List<(CopValue Key, List<CopValue> Items)>();
                    Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
                await foreach (CopValue item in CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken))
                {
                        CopValue key = (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
                        string keyStr = (await key.DisplayAsync(cancellationToken: cancellationToken));
                    if (!index.TryGetValue(keyStr, out int gi))
                    {
                        gi = order.Count;
                        index[keyStr] = gi;
                        order.Add((key, []));
                    }
                    order[gi].Items.Add(item);
                }
                result = new CopList(order.Select(g => (CopValue)new CopObject(
                    new Dictionary<string, CopValue>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["Key"] = g.Key,
                        ["Items"] = new CopList(g.Items),
                        ["Count"] = new CopInt(g.Items.Count),
                    })
                { TypeName = "Group" }).ToList());
                return result;
            }
            case "Reduce":
            {
                // Reduce(operator, itemExpr, [separator]) — fold a collection into one value using a
                // string operator. '+' sums numbers or joins strings with the optional separator.
                // (The lowercase `reduce(items, (acc,item)=>..., initial)` lambda fold is a separate
                // FFI intrinsic and is intentionally NOT intercepted here.)
                if (argExprs.Count is < 2 or > 3)
                    {
                        return null;
                    }

                    string op = (await (await ForceValueAsync((await EvalAsync(argExprs[0], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken));
                    string separator = argExprs.Count == 3 ? (await (await ForceValueAsync((await EvalAsync(argExprs[2], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken)) : "";
                    List<CopValue> values = new List<CopValue>();
                await foreach (CopValue item in CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken))
                    {
                        values.Add((await ForceValueAsync((await EvalElementExprAsync(argExprs[1], item, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)));
                    }

                    result = (await ReduceValuesAsync(op, values, separator, cancellationToken: cancellationToken));
                return result;
            }
            case "Text":
            {
                // Items.Text(template) — render the {item.X} template once per element and join with
                // newlines (the documented per-item form). Lowercase `text` keeps its FFI join form.
                if (argExprs.Count != 1)
                    {
                        return null;
                    }

                    List<string> rendered = new List<string>();
                await foreach (CopValue item in CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken))
                    {
                        rendered.Add((await (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken)));
                    }

                    result = new CopString(string.Join("\n", rendered));
                return result;
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Evaluate a per-element transform argument with `item` bound to the element.
    /// An explicit single-parameter lambda also binds its parameter to the element.
    /// </summary>
    private async ValueTask<CopValue> EvalElementExprAsync(Expression argExpr, CopValue item, Environment env, CancellationToken cancellationToken = default)
    {
        Environment elemEnv = env.Extend();
        elemEnv.Define(ImplicitItemVariable, item);
        if (argExpr is LambdaExpr lambda)
        {
            if (lambda.Params.Count >= 1)
            {
                elemEnv.Define(lambda.Params[0].Name, item);
            }

            return (await EvalAsync(lambda.Body, elemEnv, cancellationToken: cancellationToken));
        }
        return (await EvalAsync(argExpr, elemEnv, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Argument-taking string transforms invoked in call position: <c>Name.Trim('Async')</c>
    /// (remove a trailing suffix) and <c>Name.Replace('old','new')</c> (substring replace).
    /// Returns false for any other member so the normal dispatch can continue.
    /// </summary>
    private async ValueTask<CopValue?> FindStringMethodAsync(CopString str, string member,
        IReadOnlyList<Expression> argExprs, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue result = CopNull.Instance;
        switch (member)
        {
            case "Trim" or "trim":
            {
                if (argExprs.Count != 1)
                    {
                        return null;
                    }

                    string suffix = (await (await ForceValueAsync((await EvalAsync(argExprs[0], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken));
                    string value = str.Value;
                if (suffix.Length > 0 && value.EndsWith(suffix, StringComparison.Ordinal))
                    {
                        value = value[..^suffix.Length];
                    }

                    result = new CopString(value);
                return result;
            }
            case "Replace" or "replace":
            {
                if (argExprs.Count != 2)
                    {
                        return null;
                    }

                    string oldValue = (await (await ForceValueAsync((await EvalAsync(argExprs[0], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken));
                    string newValue = (await (await ForceValueAsync((await EvalAsync(argExprs[1], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken));
                result = new CopString(oldValue.Length == 0 ? str.Value : str.Value.Replace(oldValue, newValue));
                return result;
            }
            default:
                return null;
        }
    }

    /// <summary>Folds reduced values with a string operator. '+' sums numbers or joins strings.</summary>
    private static async ValueTask<CopValue> ReduceValuesAsync(string op, List<CopValue> values,
        string separator, CancellationToken cancellationToken = default)
    {
        if (values.Count == 0)
        {
            return op == "+" ? new CopInt(0) : CopNull.Instance;
        }

        bool allNumeric = values.All(v => ToNumber(v) is not null);
        if (op == "+" && allNumeric)
        {
            double sum = values.Sum(v => ToNumber(v) ?? 0);
            return sum == Math.Floor(sum) ? new CopInt((int)sum) : new CopNumber(sum);
        }
        if (op == "*" && allNumeric)
        {
            double product = 1;
            foreach (CopValue v in values)
            {
                product *= ToNumber(v) ?? 0;
            }

            return product == Math.Floor(product) ? new CopInt((int)product) : new CopNumber(product);
        }
        return new CopString(string.Join(separator, (await values.ToAsyncEnumerable().Select(async (v, itemCancellationToken) => (await v.DisplayAsync(cancellationToken: itemCancellationToken))).ToListAsync(cancellationToken))));
    }

    /// <summary>
    /// Evaluates a per-item aggregate (any/all/none/count) over a collection. The single
    /// argument expression is evaluated once per element with <c>item</c> bound, so an inline
    /// expression (<c>item:contains('x')</c>), a bare predicate name, or a lambda all work
    /// uniformly. Returns null when the argument count is not exactly one (caller falls back).
    /// </summary>
    private async ValueTask<CopValue?> TryEvalAggregateAsync(string methodName, CopValue collection,
        IReadOnlyList<Expression> argExprs, bool negated, Environment env, CancellationToken cancellationToken = default)
    {
        if (argExprs.Count != 1)
        {
            return null;
        }

        int matched = 0;
        await foreach (CopValue item in CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken))
        {
            CopValue v = (await ForceValueAsync((await EvalElementExprAsync(argExprs[0], item, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
            if (v is ICopCallable callable)
            {
                v = (await callable.InvokeAsync([item], this, env, cancellationToken: cancellationToken));
            }

            if ((await v.IsTruthyAsync(cancellationToken)))
            {
                matched++;
                if (methodName == "any")
                {
                    return Negate(true);
                }

                if (methodName == "none")
                {
                    return Negate(false);
                }
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
    private static async ValueTask<int> CompareValuesAsync(CopValue a, CopValue b, CancellationToken cancellationToken = default)
    {
        (double? an, double? bn) = (ToNumber(a), ToNumber(b));
        if (an is not null && bn is not null)
        {
            return an.Value.CompareTo(bn.Value);
        }

        return string.Compare((await a.DisplayAsync(cancellationToken: cancellationToken)), (await b.DisplayAsync(cancellationToken: cancellationToken)), StringComparison.Ordinal);
    }

    private async ValueTask<CopValue?> TryGetMemberAsync(CopValue obj, string member, CancellationToken cancellationToken = default)
    {
        // Force thunks before member access
        if (obj is CopThunk thunk)
        {
            obj = (await thunk.ForceAsync(cancellationToken: cancellationToken));
        }

        return obj switch
        {
            CopObject co when co.HasField(member) => co.GetField(member),
            CopDynamicObject dyn when (await dyn.HasFieldAsync(member, cancellationToken: cancellationToken)) => (await dyn.GetFieldAsync(member, cancellationToken: cancellationToken)),
            CopProviderProxy proxy when proxy.HasField(member) => (await proxy.GetFieldAsync(member, cancellationToken: cancellationToken)),
            _ => null
        };
    }

    private async ValueTask<CopValue> EvalMemberAsync(MemberExpr mem, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue obj = (await EvalMemberObjectAsync(mem.Object, env, cancellationToken: cancellationToken));

        // Force thunks before member access
        if (obj is CopThunk thunk)
        {
            obj = (await thunk.ForceAsync(cancellationToken: cancellationToken));
        }

        // Named subset resolution: if obj is a predicate-as-collection, expand it
        obj = (await TryResolveNamedSubsetAsync(obj, env, cancellationToken: cancellationToken));

        CopValue result = obj switch
        {
            CopObject co => EvalObjectMember(co, mem.Member),
            CopDynamicObject dyn => (await dyn.GetFieldAsync(mem.Member, cancellationToken: cancellationToken)),
            CopProviderProxy proxy => (await proxy.GetFieldAsync(mem.Member, cancellationToken: cancellationToken)),
            CopList list => (await EvalListMemberAsync(list, mem.Member, mem.Line, cancellationToken: cancellationToken)),
            CopLazyCollection lazy => (await EvalLazyMemberAsync(lazy, mem.Member, mem.Line, cancellationToken: cancellationToken)),
            CopQueryable query => (await EvalLazyMemberAsync(new CopLazyCollection(query.EnumerateAsync), mem.Member, mem.Line, cancellationToken: cancellationToken)),
            CopString str => EvalStringMember(str, mem.Member, mem.Line),
            // Null propagation: accessing a member of a null/absent value yields null rather than
            // crashing, so checks over real-world data with missing fields (e.g. a C# type in the
            // global namespace whose `File.Namespace` is null) stay robust — `null.Length` is null,
            // and downstream predicates like `:greaterThan(0)` then simply evaluate to false.
            CopNull => CopNull.Instance,
            _ when mem.Member == "Type" => new CopString((await CopTypeNameOfAsync(obj, cancellationToken: cancellationToken))),
            _ => throw new CopEvaluationException(
                $"Cannot access member '{mem.Member}' on {obj.GetType().Name}",
                mem.Line, _filePath)
        };
        if (IsCollectionValue(obj))
        {
            result = TrackMemberProjection(obj, result, mem);
        }

        // Computed property fallback for trait conformance
        if (result == CopNull.Instance && TypeRegistry is not null)
        {
            string? typeName = obj switch
            {
                CopDynamicObject d => d.TypeName,
                CopObject o => o.TypeName,
                _ => null
            };
            if (typeName is not null)
            {
                Expression? computedExpr = TypeRegistry.GetComputedProperty(typeName, mem.Member);
                if (computedExpr is not null)
                {
                    // Evaluate computed expression with 'item' bound to the object
                    Environment computedEnv = new Environment(env);
                    computedEnv.Define(ImplicitItemVariable, obj);
                    result = (await EvalAsync(computedExpr, computedEnv, cancellationToken: cancellationToken));
                }
            }
        }

        return result;
    }

    private static CopValue TrackMemberProjection(CopValue input, CopValue result, MemberExpr member) =>
        QuerySource.TrackLocal(input, result, source => member with { Object = source },
            $"{member.Member} executes locally");

    /// <summary>
    /// If value is a predicate with 1 parameter matching a known collection,
    /// resolve it to the filtered collection (named subset pattern).
    /// </summary>
    private ValueTask<CopValue> TryResolveNamedSubsetAsync(CopValue value, Environment env,
        CancellationToken cancellationToken = default) =>
        value is CopFunction or CopFunctionGroup
            ? ResolveNamedSubsetAsync(value, env, cancellationToken)
            : ValueTask.FromResult(value);

    private async ValueTask<CopValue> ResolveNamedSubsetAsync(CopValue value, Environment env,
        CancellationToken cancellationToken)
    {
        if (value is CopFunction predFunc && predFunc.Declaration.Params.Count == 1)
        {
            string inputName = predFunc.Declaration.Params[0].Name;
            if (env.TryLookup(inputName, out CopValue? inputCollection))
            {
                // Force thunks before checking type
                if (inputCollection is CopThunk thunk)
                {
                    inputCollection = (await thunk.ForceAsync(cancellationToken: cancellationToken));
                }

                if (inputCollection is CopList || inputCollection is CopLazyCollection)
                {
                    CopValue captured = inputCollection;
                    return new CopLazyCollection(async (deferredCancellationToken) =>
                    {
                        IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(captured, cancellationToken: deferredCancellationToken);
                        List<CopValue> filtered = new List<CopValue>();
                        await foreach (CopValue item in items)
                        {
                            CopValue result = (await predFunc.InvokeAsync([item], this, env, cancellationToken: deferredCancellationToken));
                            if ((await result.IsTruthyAsync(deferredCancellationToken)))
                            {
                                filtered.Add(item);
                            }
                        }
                        return filtered;
                    });
                }
            }
        }
        else if (value is CopFunctionGroup group)
        {
            CopFunction? firstFunc = group.Functions.FirstOrDefault();
            if (firstFunc is not null && firstFunc.Declaration.Params.Count == 1)
            {
                string inputName = firstFunc.Declaration.Params[0].Name;
                if (env.TryLookup(inputName, out CopValue? inputCollection))
                {
                    // Force thunks before checking type
                    if (inputCollection is CopThunk thunk)
                    {
                        inputCollection = (await thunk.ForceAsync(cancellationToken: cancellationToken));
                    }

                    if (inputCollection is CopList || inputCollection is CopLazyCollection)
                    {
                        CopValue captured = inputCollection;
                        return new CopLazyCollection(async (deferredCancellationToken) =>
                        {
                            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(captured, cancellationToken: deferredCancellationToken);
                            List<CopValue> filtered = new List<CopValue>();
                            await foreach (CopValue item in items)
                            {
                                CopValue result = (await group.InvokeAsync([item], this, env, cancellationToken: deferredCancellationToken));
                                if ((await result.IsTruthyAsync(deferredCancellationToken)))
                                {
                                    filtered.Add(item);
                                }
                            }
                            return filtered;
                        });
                    }
                }
            }
        }
        return value;
    }

    private static async ValueTask<string> CopTypeNameOfAsync(CopValue value, CancellationToken cancellationToken = default) => value switch
    {
        CopThunk thunk => (await CopTypeNameOfAsync((await thunk.ForceAsync(cancellationToken: cancellationToken)), cancellationToken: cancellationToken)),
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
    private async ValueTask<CopValue> EvalMemberObjectAsync(Expression objExpr, Environment env, CancellationToken cancellationToken = default)
    {
        if (objExpr is IdentifierExpr id && id.Name.Length > 0 && char.IsUpper(id.Name[0]))
        {
            // Try direct lookup first
            if (env.TryLookup(id.Name, out CopValue? val))
            {
                return val;
            }
            // Type-qualified access: Folder.X → look up "folder" parameter
            string lower = char.ToLower(id.Name[0]) + id.Name[1..];
            if (env.TryLookup(lower, out CopValue? paramVal))
            {
                return paramVal;
            }
        }
        return (await EvalAsync(objExpr, env, cancellationToken: cancellationToken));
    }

    private async ValueTask<CopValue> EvalListMemberAsync(CopList list, string member, int line, CancellationToken cancellationToken = default) => member switch
    {
        "Count" or "count" or "Length" or "length" => new CopInt(list.Items.Count),
        "First" or "first" => list.Items.Count > 0 ? list.Items[0] : CopNull.Instance,
        "Last" or "last" => list.Items.Count > 0 ? list.Items[^1] : CopNull.Instance,
        // Collection projection: list.Name → map each item's Name field
        _ => (await ProjectCollectionAsync(list.Items, member, cancellationToken: cancellationToken))
    };

    /// <summary>Object member access including dynamic operations `.Keys`, `.Values`, `.Count`
    /// (issue #43). A real field of that name takes precedence over the operation.</summary>
    private static CopValue EvalObjectMember(CopObject obj, string member)
    {
        if (obj.HasField(member))
        {
            return obj.GetField(member);
        }

        return member switch
        {
            "Keys" or "keys" => new CopList(obj.Fields.Keys.Select(k => (CopValue)new CopString(k)).ToList()),
            "Values" or "values" => new CopList(obj.Fields.Values.ToList()),
            "Count" or "count" => new CopInt(obj.Fields.Count),
            _ => obj.GetField(member)
        };
    }

    private async ValueTask<CopValue> EvalLazyMemberAsync(CopLazyCollection lazy, string member, int line, CancellationToken cancellationToken = default) => member switch
    {
        "Count" or "count" or "Length" or "length" => new CopInt(
            (await lazy.GetItemsAsync(cancellationToken)).Count),
        "First" or "first" => (await lazy.EnumerateAsync(cancellationToken: cancellationToken).FirstOrDefaultAsync(cancellationToken: cancellationToken)) ?? CopNull.Instance,
        "Last" or "last" => (await lazy.GetItemsAsync(cancellationToken)) is { Count: > 0 } items
            ? items[^1] : CopNull.Instance,
        // Collection projection: collection.Name → map each item's Name field
        _ => (await ProjectCollectionAsync(await lazy.GetItemsAsync(cancellationToken),
            member, cancellationToken: cancellationToken))
    };

    private CopValue EvalStringMember(CopString str, string member, int line) => member switch
    {
        "Length" or "length" => new CopInt(str.Value.Length),
        "Lower" or "lower" => new CopString(str.Value.ToLowerInvariant()),
        "Upper" or "upper" => new CopString(str.Value.ToUpperInvariant()),
        "Normalized" or "normalized" => new CopString(NormalizeConvention(str.Value)),
        "Words" or "words" => new CopList(SplitWords(str.Value).Select(w => (CopValue)new CopString(w)).ToList()),
        "Type" => new CopString("string"),
        _ => throw new CopEvaluationException($"Unknown string member '{member}'", line, _filePath)
    };

    /// <summary>Convention-insensitive canonical form: lowercase with separators removed (Foo_Bar → foobar).</summary>
    private static string NormalizeConvention(string s)
    {
        StringBuilder sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c is not ('_' or '-' or ' '))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    /// <summary>Splits an identifier into lowercase words, breaking on camelCase, digits, and separators.</summary>
    private static List<string> SplitWords(string s)
    {
        List<string> words = new List<string>();
        StringBuilder cur = new System.Text.StringBuilder();
        void Flush() { if (cur.Length > 0) { words.Add(cur.ToString().ToLowerInvariant()); cur.Clear(); } }
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c is '_' or '-' or ' ') { Flush(); continue; }
            // Break before an uppercase letter that starts a new word (camelCase boundary).
            if (i > 0 && char.IsUpper(c) && (char.IsLower(s[i - 1]) || char.IsDigit(s[i - 1])))
            {
                Flush();
            }

            cur.Append(c);
        }
        Flush();
        return words;
    }

    /// <summary>
    /// Project a member across all items in a collection: items.Name → [item1.Name, item2.Name, ...]
    /// </summary>
    private static async ValueTask<CopValue> ProjectCollectionAsync(IReadOnlyList<CopValue> items, string member, CancellationToken cancellationToken = default)
    {
        List<CopValue> projected = new List<CopValue>(items.Count);
        foreach (CopValue item in items)
        {
            CopValue field = item switch
            {
                CopThunk th => (await th.ForceAsync(cancellationToken: cancellationToken)) switch
                {
                    CopDynamicObject d2 => (await d2.GetFieldAsync(member, cancellationToken: cancellationToken)),
                    CopObject o2 => o2.GetField(member),
                    _ => CopNull.Instance
                },
                CopDynamicObject dyn => (await dyn.GetFieldAsync(member, cancellationToken: cancellationToken)),
                CopObject obj => obj.GetField(member),
                _ => CopNull.Instance
            };
            if (field is CopNull)
            {
                continue;
            }
            // Flatten collection-valued projections so e.g. `Types.Methods` is the union of all
            // methods (not a list of per-type method lists). `Types.Methods:isLong` then dispatches
            // per Method instead of per collection (issue #50).
            switch (field)
            {
                case CopList fl: projected.AddRange(fl.Items); break;
                case CopLazyCollection flz: projected.AddRange((await flz.EnumerateAsync(cancellationToken: cancellationToken).ToListAsync(cancellationToken))); break;
                default: projected.Add(field); break;
            }
        }
        return new CopList(projected);
    }

    private async ValueTask<CopValue> EvalIndexAsync(IndexExpr idx, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue obj = (await ForceValueAsync((await EvalAsync(idx.Object, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
        CopValue index = (await ForceValueAsync((await EvalAsync(idx.Index, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));

        CopValue result = (obj, index) switch
        {
            (CopList list, CopInt i) => i.Value >= 0 && i.Value < list.Items.Count
                ? list.Items[i.Value]
                : CopNull.Instance,
            (CopLazyCollection lazy, CopInt i) => i.Value < 0 ? CopNull.Instance : (await lazy.EnumerateAsync(cancellationToken: cancellationToken).ElementAtOrDefaultAsync(i.Value, cancellationToken: cancellationToken)) ?? CopNull.Instance,
            (CopQueryable query, CopInt i) => i.Value < 0 ? CopNull.Instance : (await query.EnumerateAsync(cancellationToken: cancellationToken).ElementAtOrDefaultAsync(i.Value, cancellationToken: cancellationToken)) ?? CopNull.Instance,
            (CopString str, CopInt i) => i.Value >= 0 && i.Value < str.Value.Length
                ? new CopString(str.Value[i.Value].ToString())
                : CopNull.Instance,
            (CopObject co, CopString key) => co.GetField(key.Value),
            _ => throw new CopEvaluationException("Invalid index operation", idx.Line, _filePath)
        };
        return QuerySource.TrackLocal(obj, result, source => idx with { Object = source }, "indexing executes locally");
    }

    private async ValueTask<CopValue> EvalConditionalAsync(ConditionalExpr cond, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue rawCondition = (await ForceValueAsync((await EvalAsync(cond.Condition, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
        _traceLog?.Invoke($"[trace] EvalConditional: {ExpressionRenderer.Render(cond.Condition)}  (raw={DescribeValue(rawCondition)})");
        CopValue condition = (await CoerceCallableToBoolAsync(rawCondition, env, cancellationToken: cancellationToken));
        _traceLog?.Invoke($"[trace] EvalConditional: coerced={DescribeValue(condition)}, truthy={(await condition.IsTruthyAsync(cancellationToken))}");
        return (await condition.IsTruthyAsync(cancellationToken))
            ? (await EvalAsync(cond.Then, env, cancellationToken: cancellationToken))
            : (await EvalAsync(cond.Else, env, cancellationToken: cancellationToken));
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

    private async ValueTask<CopValue> EvalMatchAsync(MatchExpr match, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue discriminant = (await ForceValueAsync((await EvalAsync(match.Discriminant, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));

        foreach (MatchArm arm in match.Arms)
        {
            if (MatchPattern(arm.Pat, discriminant, env))
            {
                return (await EvalAsync(arm.Body, env, cancellationToken: cancellationToken));
            }
        }

        return CopNull.Instance;
    }

    private async ValueTask<CopValue> EvalListAsync(ListExpr list, Environment env,
        CancellationToken cancellationToken = default) =>
        new CopList(await EvalArgumentsAsync(list.Elements, env, cancellationToken));

    private async ValueTask<List<CopValue>> EvalArgumentsAsync(IReadOnlyList<Expression> expressions,
        Environment env, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<CopValue> values = new(expressions.Count);
        foreach (Expression expression in expressions)
        {
            values.Add(await EvalAsync(expression, env, cancellationToken));
        }
        return values;
    }

    private async ValueTask<CopValue> EvalObjectAsync(ObjectExpr obj, Environment env, CancellationToken cancellationToken = default)
    {
        Dictionary<string, CopValue> fields = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (FieldInit field in obj.Fields)
        {
            fields[field.Name] = (await EvalAsync(field.Value, env, cancellationToken: cancellationToken));
        }

        return new CopObject(fields) { TypeName = obj.TypeHint };
    }

    private async ValueTask<CopValue> EvalInterpolationAsync(InterpolatedStringExpr interp, Environment env, CancellationToken cancellationToken = default)
    {
        StringBuilder sb = new System.Text.StringBuilder();
        foreach (StringPart part in interp.Parts)
        {
            switch (part)
            {
                case TextPart tp:
                    sb.Append(tp.Text);
                    break;
                case ExpressionPart ep:
                    CopValue val = (await EvalAsync(ep.Expr, env, cancellationToken: cancellationToken));
                    if (ep.Format is not null)
                    {
                        sb.Append($"{{{(await val.DisplayAsync(cancellationToken: cancellationToken))}@{ep.Format}}}");
                    }
                    else
                    {
                        sb.Append((await val.DisplayAsync(cancellationToken: cancellationToken)));
                    }

                    break;
            }
        }
        return new CopString(sb.ToString());
    }

    private async ValueTask<CopValue> EvalFilterAsync(FilterExpr filter, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue? collection = null;
        if (filter.Collection is FilterExpr { Predicate: IdentifierExpr property, Negated: false } propertyAccess
            && !ResolvesToUserPredicate(property, env))
        {
            ResolvedQueryPredicate resolved = ResolvedQueryPredicate.Resolve(filter.Predicate, env, null);
            if (PredicateCompiler.CompileOrDefault(resolved.Expression,
                filter.Negated ? PredicatePolarity.Negative : PredicatePolarity.Positive,
                property.Name) is { } compound)
            {
                CopValue source = (await ForceValueAsync((await EvalAsync(propertyAccess.Collection, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
                if (source is CopQueryable compoundSource && compoundSource.CanPushFilter(compound))
                {
                    return compoundSource.WithFilter(compound,
                        new QueryStep(resolved.Expression, resolved.Definitions, filter.Negated, property.Name));
                }
                // Reuse the evaluated source: expressions may have observable side effects.
                Environment sourceEnvironment = env.Extend();
                sourceEnvironment.Define("$query-source", source);
                collection = (await ForceValueAsync((await EvalAsync(propertyAccess with { Collection = new IdentifierExpr("$query-source") }, sourceEnvironment, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
            }
        }

        collection ??= (await ForceValueAsync((await EvalAsync(filter.Collection, env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
        bool negated = filter.Negated;

        _traceLog?.Invoke($"[trace] EvalFilter: {ExpressionRenderer.Render(filter)}  (collection={DescribeValue(collection)})");

        // Collection combinators (concat/push/enqueue) used via colon pipe are collection-level for
        // ANY collection kind (list, lazy, queryable): resolve the FFI directly and invoke it once
        // with the whole collection, so `a:concat(b)` yields a+b rather than falling through to the
        // per-item filter loop (which would just return the left operand).
        if (IsCollectionValue(collection)
            && filter.Predicate is CallExpr combinerCall
            && combinerCall.Callee is IdentifierExpr combinerId
            && IsCollectionLevelBuiltin(combinerId.Name, combinerCall.Args.Count)
            && _ffi.Resolve(combinerId.Name) is { } combinerFn)
        {
            List<CopValue> combinerArgs = new List<CopValue> { collection };
            combinerArgs.AddRange((await combinerCall.Args.ToAsyncEnumerable().Select(async (a, itemCancellationToken) => (await ForceValueAsync((await EvalAsync(a, env, cancellationToken: itemCancellationToken)), cancellationToken: itemCancellationToken))).ToListAsync(cancellationToken)));
            CopValue combined = (await combinerFn.InvokeAsync(combinerArgs, this, env, cancellationToken: cancellationToken));
            return combined is CopQueryable ? combined : QuerySource.TrackLocal(collection, combined,
                source => filter with { Collection = source }, $"{combinerId.Name} executes locally");
        }

        // Queryable collection: try to compile predicate and accumulate filter (lazy pushdown)
        if (collection is CopQueryable queryable)
        {
            ResolvedQueryPredicate resolved = ResolvedQueryPredicate.Resolve(filter.Predicate, env, queryable.ItemType);
            FilterExpression? translated = PredicateCompiler.CompileBoundOrDefault(resolved.Expression, resolved.Environment, queryable.ItemType);
            if (translated is not null && queryable.CanPushFilter(translated))
            {
                return queryable.WithFilter(negated ? new Cop.Core.NotFilter(translated) : translated,
                    new QueryStep(resolved.Expression, resolved.Definitions, negated));
            }
            // An untranslated predicate name is not a provider field (issue #33).
            if (ResolvesToUserPredicate(filter.Predicate, env))
            {
                _traceLog?.Invoke($"[trace] EvalFilter: deferring local predicate '{ExpressionRenderer.Render(filter.Predicate)}'");
                collection = new CopLazyCollection(queryable.EnumerateAsync) { Source = queryable.Source };
            }
            else
            {
                // Try to compile the predicate AST to a FilterExpression for pushdown
                FilterExpression? compiled = PredicateCompiler.CompileOrDefault(resolved.Expression,
                    negated ? PredicatePolarity.Negative : PredicatePolarity.Positive);
                if (compiled is not null && queryable.CanPushFilter(compiled))
                {
                    _traceLog?.Invoke($"[trace] EvalFilter: compiled predicate '{ExpressionRenderer.Render(filter.Predicate)}' to FilterExpression, accumulating on queryable");
                    return queryable.WithFilter(compiled, new QueryStep(resolved.Expression, resolved.Definitions, negated));
                }

                // Check if this is a property access (map semantic) that could be part of a compound filter
                string? propName = PredicateCompiler.GetPropertyAccessOrDefault(filter.Predicate);
                if (propName is not null && queryable.HasScalarProperty(propName))
                {
                    // Return a queryable with property context — next filter in chain may compile to StringOp/Comparison
                    _traceLog?.Invoke($"[trace] EvalFilter: property access '{propName}' on queryable, creating property proxy");
                    return new CopQueryableProperty(queryable, propName);
                }

                // Keep unsupported expressions deferred and evaluate them locally.
                _traceLog?.Invoke($"[trace] EvalFilter: deferring local predicate '{ExpressionRenderer.Render(filter.Predicate)}'");
                collection = new CopLazyCollection(queryable.EnumerateAsync) { Source = queryable.Source };
            }
        }

        // Queryable property: compound pattern e.g., queryable:Name:startsWith('A')
        if (collection is CopQueryableProperty queryProp)
        {
            // Try to compile the predicate with property context
            ResolvedQueryPredicate resolved = ResolvedQueryPredicate.Resolve(filter.Predicate, env, queryProp.Source.ItemType);
            FilterExpression? compiled = PredicateCompiler.CompileOrDefault(resolved.Expression,
                negated ? PredicatePolarity.Negative : PredicatePolarity.Positive,
                queryProp.PropertyName);
            if (compiled is not null && queryProp.Source.CanPushFilter(compiled))
            {
                _traceLog?.Invoke($"[trace] EvalFilter: compiled compound filter {queryProp.PropertyName}:{ExpressionRenderer.Render(filter.Predicate)}");
                return queryProp.Source.WithFilter(compiled,
                    new QueryStep(resolved.Expression, resolved.Definitions, negated, queryProp.PropertyName));
            }

            // Package predicates run locally but must retain the original records, just like pushdown.
            _traceLog?.Invoke($"[trace] EvalFilter: cannot compile compound, materializing");
            return QuerySource.TrackLocal(queryProp.Source, new CopLazyCollection((deferredCancellationToken) => queryProp.Source.EnumerateAsync(cancellationToken: deferredCancellationToken).Where(async (item, itemCancellationToken) =>
            {
                CopValue field = (await GetFieldOnValueAsync(item, queryProp.PropertyName, cancellationToken: itemCancellationToken));
                bool matches = (await (await ForceValueAsync((await ApplyFilterPredicateValueAsync(filter.Predicate, field, env, cancellationToken: itemCancellationToken)), cancellationToken: itemCancellationToken)).IsTruthyAsync(itemCancellationToken));
                return negated ? !matches : matches;
            })), source => filter with
            {
                Collection = new FilterExpr(source, new IdentifierExpr(queryProp.PropertyName))
            }, "predicate is not supported by the provider query subset");
        }

        // If collection resolved to a predicate/function, resolve as a "named subset":
        // predicate Clients(Types) => ... used bare as `Clients:filter` means Types:Clients:filter
        collection = (await TryResolveNamedSubsetAsync(collection, env, cancellationToken: cancellationToken));

        // If collection is a scalar (not a list/collection), apply the predicate/function as a
        // VALUE PIPE: `value:func` returns `func(value)` (issue #44). Member-field and string
        // predicates still work because they yield their natural result (a bool for predicates).
        // Negation (`!value:pred`) coerces to a bool.
        if (collection is not CopList and not CopLazyCollection)
        {
            _traceLog?.Invoke($"[trace] EvalFilter: scalar branch, collection={(await collection.DisplayAsync(cancellationToken: cancellationToken)).Substring(0, Math.Min((await collection.DisplayAsync(cancellationToken: cancellationToken)).Length, 80))}");
            CopValue raw = (await ApplyFilterPredicateValueAsync(filter.Predicate, collection, env, cancellationToken: cancellationToken));
            if (negated)
            {
                return CopBool.Of(!(await raw.IsTruthyAsync(cancellationToken)));
            }

            return raw;
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
            && (await ShouldApplyAtCollectionLevelAsync(bareId.Name, collection, env, cancellationToken: cancellationToken)))
        {
            if (env.TryLookup(bareId.Name, out CopValue? bareBinding)
                && CollectionParamOverload(bareBinding) is { } collOverload)
            {
                CopValue r = (await collOverload.InvokeAsync([collection], this, env, cancellationToken: cancellationToken));
                return QuerySource.TrackLocal(collection, negated && r is CopBool rb ? CopBool.Of(!rb.IsTrue) : r,
                    source => filter with { Collection = source }, $"{bareId.Name} executes locally");
            }
            // Builtin `:empty` with no user collection overload in scope → emptiness test.
            bool isEmpty = (await IsEmptyAsync(collection, cancellationToken: cancellationToken));
            return QuerySource.TrackLocal(collection, CopBool.Of(negated ? !isEmpty : isEmpty),
                source => filter with { Collection = source }, "empty executes locally");
        }

        // Check for collection-level predicates (contains, any, all, none, count)
        // These operate on the collection as a whole, not per-item.
        CopValue? collResult = (await TryEvalCollectionPredicateAsync(filter.Predicate, collection, negated, env, cancellationToken: cancellationToken));
        if (collResult is not null)
        {
            return QuerySource.TrackLocal(collection, collResult, source => filter with { Collection = source },
                "collection predicate executes locally");
        }

        // A bare lowercase identifier predicate (`coll:isFoo`) that resolves to nothing — not a
        // defined predicate/function, not an FFI/enum, and not a field on the data — is almost
        // certainly a typo. Without this guard the per-item loop silently treats it as false for
        // every item and returns an empty result: a silent false-green. Fail loudly instead.
        if (filter.Predicate is IdentifierExpr unresolvedPred
            && unresolvedPred.Name.Length > 0 && char.IsLower(unresolvedPred.Name[0])
            && unresolvedPred.Name != "empty"
            && !(await IsResolvablePredicateNameAsync(unresolvedPred.Name, env, cancellationToken: cancellationToken)))
        {
            CopValue? firstItem = (await CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken).FirstOrDefaultAsync(cancellationToken: cancellationToken));
            if (firstItem is CopThunk ft)
            {
                firstItem = (await ft.ForceAsync(cancellationToken: cancellationToken));
            }

            bool itemHasField = firstItem switch
            {
                CopObject co => co.HasField(unresolvedPred.Name),
                CopDynamicObject dyn => (await dyn.HasFieldAsync(unresolvedPred.Name, cancellationToken: cancellationToken)),
                CopProviderProxy proxy => proxy.HasField(unresolvedPred.Name),
                _ => false
            };
            if (firstItem is not null && !itemHasField)
            {
                throw new CopEvaluationException(
                    $"Unknown predicate '{unresolvedPred.Name}': it is not a defined predicate, " +
                    "function, or a field on the items being filtered. Check the spelling or add an import.",
                    unresolvedPred.Line, _filePath);
            }
        }

        // For collections: apply predicate to each item.
        // If predicate returns a non-bool object (map semantic), collect those results.
        // Otherwise filter items by truthiness (filter semantic).
        string predName = filter.Predicate is IdentifierExpr pid ? pid.Name : filter.Predicate is CallExpr pcall && pcall.Callee is IdentifierExpr pcid ? pcid.Name : "?";
        return QuerySource.TrackLocal(collection, new CopLazyCollection(async (deferredCancellationToken) =>
        {
            IAsyncEnumerable<CopValue> items = CoerceToEnumerableAsync(collection, cancellationToken: deferredCancellationToken);
            List<CopValue> mapped = new List<CopValue>();
            bool isMap = false;
            bool firstItem = true;
            int itemCount = 0;

            await foreach (CopValue item in items)
            {
                itemCount++;
                try
                {
                    CopValue result = (await ApplyFilterPredicateValueAsync(filter.Predicate, item, env, cancellationToken: deferredCancellationToken));

                    // A predicate whose body is a sole bare predicate name (e.g. `=> isPublic`)
                    // evaluates to that predicate's function group instead of invoking it. Invoke
                    // it on the item so the filter sees a bool — otherwise map-mode is mis-detected
                    // below and the filter yields function groups, which crash downstream (e.g. in
                    // toError). Mirrors how `&&`/`||` coerce a bare predicate via CoerceCallableToBool.
                    if (result is ICopCallable)
                    {
                        Environment coerceEnv = env.Extend();
                        coerceEnv.Define(ImplicitItemVariable, item);
                        result = (await CoerceCallableToBoolAsync(result, coerceEnv, cancellationToken: deferredCancellationToken));
                    }

                    if (firstItem)
                    {
                        _traceLog?.Invoke($"[trace] EvalFilter({predName}): first result type={result.GetType().Name}, value={(await result.DisplayAsync(cancellationToken: deferredCancellationToken)).Substring(0, Math.Min((await result.DisplayAsync(cancellationToken: deferredCancellationToken)).Length, 50))}, item={(await item.DisplayAsync(cancellationToken: deferredCancellationToken)).Substring(0, Math.Min((await item.DisplayAsync(cancellationToken: deferredCancellationToken)).Length, 50))}");
                        // Determine mode from first result:
                        // If it's a non-bool structured value, this is a map operation
                        isMap = result is not CopBool && result is not CopNull && (await result.IsTruthyAsync(deferredCancellationToken));
                        firstItem = false;
                    }

                    if (isMap)
                    {
                        // Map mode: collect non-null results
                        if (result is not CopNull && (await result.IsTruthyAsync(deferredCancellationToken)))
                        {
                            mapped.Add(result);
                        }
                    }
                    else
                    {
                        // Filter mode: keep items where predicate is truthy (respecting negation)
                        bool passes = (await result.IsTruthyAsync(deferredCancellationToken));
                        if (negated)
                        {
                            passes = !passes;
                        }

                        if (passes)
                        {
                            mapped.Add(item);
                        }
                    }
                }
                catch (CopEvaluationException ex)
                {
                    throw new CopEvaluationException(
                        $"Predicate '{predName}' failed on item {itemCount}: {ex.Message}");
                }
                catch (Exception ex) when (ex is not OutOfMemoryException
                    and not OperationCanceledException)
                {
                    throw new CopEvaluationException(
                        $"Predicate '{predName}' failed on item {itemCount}: {ex.Message}");
                }
            }

            _traceLog?.Invoke($"[trace] EvalFilter({predName}): enumerated {itemCount} items, mapped {mapped.Count} results");
            return mapped;
        }), source => filter with { Collection = source }, "predicate is not supported by the provider query subset");
    }

    /// <summary>
    /// Check if a filter predicate is a collection-level membership test (contains).
    /// Returns null if the predicate should be handled per-item.
    /// Only intercepts predicates that are unambiguously collection-level operations.
    /// </summary>
    private async ValueTask<CopValue?> TryEvalCollectionPredicateAsync(Expression predicateExpr, CopValue collection, bool negated, Environment env, CancellationToken cancellationToken = default)
    {
        if (predicateExpr is not CallExpr call)
        {
            return null;
        }

        string? methodName = call.Callee switch
        {
            IdentifierExpr id => id.Name,
            _ => null
        };

        if (methodName is null || call.Args.Count == 0)
        {
            return null;
        }

        // Per-item aggregates (any/all/none/count) accept an inline element expression
        // (e.g. items:any(item:contains('x'))), a bare predicate name, or a lambda —
        // evaluate the argument once per element with `item` bound. Without this, an inline
        // expression isn't a callable, so the colon form silently fell through to the per-item
        // filter loop and returned a collection instead of a bool.
        if (methodName is "any" or "all" or "none" or "count")
        {
            CopValue? agg = (await TryEvalAggregateAsync(methodName, collection, call.Args, negated, env, cancellationToken: cancellationToken));
            if (agg is not null)
            {
                return agg;
            }
        }

        // Generic dispatch: collection-level when any arg is a lambda/callable (e.g.
        // Types:all(isPublic)) OR the resolved function's first parameter is a collection
        // (e.g. References:containsAny(['cop']) → containsAny(items:[string], values:[string])).
        // Without the latter, `coll:containsAny([...])` dispatched per item and failed with
        // "'containsAny' parameter 'items' expects [string], got string" (issue #30).
        if (HasCallableArg(call.Args, env)
            || (await FirstParamIsCollectionFunctionAsync(methodName, call.Args.Count, collection, env, cancellationToken: cancellationToken)))
        {
            List<CopValue> evaluatedArgs = await EvalArgumentsAsync(call.Args, env, cancellationToken);
            List<CopValue> allArgs = new List<CopValue> { collection };
            allArgs.AddRange(evaluatedArgs);

            ICopCallable? method = null;
            if (env.TryLookup(methodName, out CopValue? fn) && fn is ICopCallable c)
            {
                method = c;
            }
            else if (_ffi.Resolve(methodName) is ICopCallable f)
            {
                method = f;
            }

            if (method is not null)
            {
                CopValue result = (await method.InvokeAsync(allArgs, this, env, cancellationToken: cancellationToken));
                if (negated && result is CopBool b)
                {
                    return CopBool.Of(!b.IsTrue);
                }

                return result;
            }
        }

        // Fallback: contains(value) for collection membership test
        if (methodName == "contains")
        {
            List<CopValue> items = (await CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken).ToListAsync(cancellationToken: cancellationToken));
            string searchVal = (await (await EvalAsync(call.Args[0], env, cancellationToken: cancellationToken)).DisplayAsync(cancellationToken: cancellationToken));
            bool found = (await items.ToAsyncEnumerable().AnyAsync(async (item, itemCancellationToken) => string.Equals((await item.DisplayAsync(cancellationToken: itemCancellationToken)), searchVal, StringComparison.OrdinalIgnoreCase), cancellationToken: cancellationToken));
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
        foreach (Expression arg in args)
        {
            if (arg is LambdaExpr)
            {
                return true;
            }

            if (arg is IdentifierExpr id)
            {
                if (env.TryLookup(id.Name, out CopValue? val) && val is ICopCallable)
                {
                    return true;
                }

                if (_ffi.Resolve(id.Name) is ICopCallable)
                {
                    return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// When a filtered call such as <c>coll:greaterThan(1)</c> supplies exactly as many explicit
    /// arguments as the callable declares parameters, the function uses a free <c>item</c> variable
    /// for the subject (e.g. <c>function greaterThan(limit) =&gt; item &gt; limit</c>). In that case
    /// invoke it with just the explicit args while binding <c>item</c> to the subject, instead of
    /// prepending the subject as an extra positional argument (which overflows arity). Returns null
    /// when the call is not this free-item shape, so the caller falls back to subject-as-first-arg.
    ///
    /// A function whose FIRST parameter is the item-type parameter (e.g. <c>toWarning(Statement,
    /// message)</c>) is NOT free-item — its item is the first positional parameter, filled by the
    /// subject. Excluding those is essential so an N-explicit-arg call resolves to the
    /// (N+1)-arity item-param overload rather than colliding with an N-arity item-param overload
    /// (e.g. <c>:toWarning('msg', nameof(x))</c> must pick the 3-arg overload, not the 2-arg one).
    /// </summary>
    private async ValueTask<CopValue?> TryInvokeFreeItemFilterCallAsync(ICopCallable callable, IReadOnlyList<CopValue> args, CopValue subject, CancellationToken cancellationToken = default)
    {
        switch (callable)
        {
            case CopFunction f when f.Arity == args.Count && !IsItemParameterFirst(f):
                return (await CallUserFunctionAsync(f, args, itemOverride: subject, cancellationToken: cancellationToken));
            case CopFunctionGroup g:
                CopFunction? overload = g.Functions.FirstOrDefault(o => o.Arity == args.Count && !IsItemParameterFirst(o));
                return overload is not null ? (await CallUserFunctionAsync(overload, args, itemOverride: subject, cancellationToken: cancellationToken)) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// Apply a filter predicate expression to a subject value, returning the raw CopValue result
    /// (not just bool). Used by EvalFilter to distinguish filter vs map semantics.
    /// </summary>
    private async ValueTask<CopValue> ApplyFilterPredicateValueAsync(Expression predicateExpr, CopValue subject, Environment env, CancellationToken cancellationToken = default)
    {
        switch (predicateExpr)
        {
            case IdentifierExpr id:
                if (env.TryLookup(id.Name, out CopValue? fn) && (await ForceValueAsync(fn, cancellationToken: cancellationToken)) is ICopCallable callable)
                {
                    return (await callable.InvokeAsync([subject], this, env, cancellationToken: cancellationToken));
                }

                CopExternalFunction? ffiFn = _ffi.Resolve(id.Name);
                if (ffiFn is ICopCallable ffiCallable)
                {
                    return (await ffiCallable.InvokeAsync([subject], this, env, cancellationToken: cancellationToken));
                }

                if (subject is CopDynamicObject dyn && (await dyn.HasFieldAsync(id.Name, cancellationToken: cancellationToken)))
                {
                    return (await dyn.GetFieldAsync(id.Name, cancellationToken: cancellationToken));
                }

                if (subject is CopObject obj && obj.HasField(id.Name))
                {
                    return obj.GetField(id.Name);
                }

                if (id.Name == "empty")
                {
                    return CopBool.Of((await IsEmptyAsync(subject, cancellationToken: cancellationToken)));
                }

                return CopBool.False;

            case CallExpr call:
                string? methodName = call.Callee switch
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
                        CopValue listVal = (await ForceValueAsync((await EvalAsync(call.Args[0], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
                        if (listVal is CopList list)
                        {
                            return CopBool.Of(EvalInMembership(subject, list));
                        }
                    }

                    // Evaluate args with item bound to subject (for template interpolation)
                    Environment argEnv = env.Extend();
                    argEnv.Define(ImplicitItemVariable, subject);
                    List<CopValue> args = await EvalArgumentsAsync(call.Args, argEnv, cancellationToken);
                    List<CopValue> allArgs = new List<CopValue> { subject };
                    allArgs.AddRange(args);

                    if (env.TryLookup(methodName, out CopValue? mfn) && (await ForceValueAsync(mfn, cancellationToken: cancellationToken)) is ICopCallable mc)
                    {
                        CopValue? freeItem = (await TryInvokeFreeItemFilterCallAsync(mc, args, subject, cancellationToken: cancellationToken));
                        return freeItem ?? (await mc.InvokeAsync(allArgs, this, env, cancellationToken: cancellationToken));
                    }
                    CopExternalFunction? mffi = _ffi.Resolve(methodName);
                    if (mffi is ICopCallable mfc)
                    {
                        return (await mfc.InvokeAsync(allArgs, this, env, cancellationToken: cancellationToken));
                    }
                }
                Environment itemEnv = env.Extend();
                itemEnv.Define(ImplicitItemVariable, subject);
                return (await EvalAsync(call, itemEnv, cancellationToken: cancellationToken));

            case MemberExpr mem2:
                return (await EvalMemberOnSubjectAsync(mem2, subject, env, cancellationToken: cancellationToken));

            default:
                CopValue val = (await EvalAsync(predicateExpr, env, cancellationToken: cancellationToken));
                if (val is ICopCallable predCallable)
                {
                    return (await predCallable.InvokeAsync([subject], this, env, cancellationToken: cancellationToken));
                }

                return val;
        }
    }

    /// <summary>
    /// Apply a filter predicate expression to a subject value.
    /// For `:isPublic` → calls isPublic(subject)
    /// For `:startsWith('A')` → calls startsWith(subject, 'A')
    /// For `:empty` → tests subject.IsTruthy (inverted: empty means !truthy for collections)
    /// </summary>
    private async ValueTask<bool> ApplyFilterPredicateAsync(Expression predicateExpr, CopValue subject, Environment env, CancellationToken cancellationToken = default)
    {
        switch (predicateExpr)
        {
            case IdentifierExpr id:
                // :predicateName → call predicateName(subject)
                if (env.TryLookup(id.Name, out CopValue? fn) && fn is ICopCallable callable)
                {
                    return (await (await callable.InvokeAsync([subject], this, env, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken));
                }

                CopExternalFunction? ffiFn = _ffi.Resolve(id.Name);
                if (ffiFn is ICopCallable ffiCallable)
                {
                    return (await (await ffiCallable.InvokeAsync([subject], this, env, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken));
                }
                // Check if it's a bool property on the subject
                if (subject is CopDynamicObject dyn && (await dyn.HasFieldAsync(id.Name, cancellationToken: cancellationToken)))
                {
                    return (await (await dyn.GetFieldAsync(id.Name, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken));
                }

                if (subject is CopObject obj && obj.HasField(id.Name))
                {
                    return (await obj.GetField(id.Name).IsTruthyAsync(cancellationToken));
                }
                // "empty" pseudo-predicate: collection with 0 items
                if (id.Name == "empty")
                {
                    return (await IsEmptyAsync(subject, cancellationToken: cancellationToken));
                }

                return false;

            case CallExpr call:
                // :method(args) → call method(subject, args...)
                string? methodName = call.Callee switch
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
                        CopValue listVal = (await ForceValueAsync((await EvalAsync(call.Args[0], env, cancellationToken: cancellationToken)), cancellationToken: cancellationToken));
                        if (listVal is CopList list)
                        {
                            return EvalInMembership(subject, list);
                        }
                    }

                    Environment argEnv = env.Extend();
                    argEnv.Define(ImplicitItemVariable, subject);
                    List<CopValue> args = await EvalArgumentsAsync(call.Args, argEnv, cancellationToken);
                    List<CopValue> allArgs = new List<CopValue> { subject };
                    allArgs.AddRange(args);

                    if (env.TryLookup(methodName, out CopValue? mfn) && mfn is ICopCallable mc)
                    {
                        CopValue? freeItem = (await TryInvokeFreeItemFilterCallAsync(mc, args, subject, cancellationToken: cancellationToken));
                        return (await (freeItem ?? (await mc.InvokeAsync(allArgs, this, env, cancellationToken: cancellationToken))).IsTruthyAsync(cancellationToken));
                    }
                    CopExternalFunction? mffi = _ffi.Resolve(methodName);
                    if (mffi is ICopCallable mfc)
                    {
                        return (await (await mfc.InvokeAsync(allArgs, this, env, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken));
                    }
                }
                // Fallback: evaluate the call expression in a context where item is bound
                Environment itemEnv = env.Extend();
                itemEnv.Define(ImplicitItemVariable, subject);
                return (await (await EvalAsync(call, itemEnv, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken));

            case MemberExpr mem2:
                // :Type.Property → access subject.Property (qualified filter)
                // The member expression path represents a property chain on the subject
                return (await (await EvalMemberOnSubjectAsync(mem2, subject, env, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken));

            default:
                // Evaluate predicate expression normally and use its truthiness
                CopValue val = (await EvalAsync(predicateExpr, env, cancellationToken: cancellationToken));
                return (await ApplyPredicateAsync(val, subject, env, cancellationToken: cancellationToken));
        }
    }

    private async ValueTask<CopValue> EvalMemberOnSubjectAsync(MemberExpr mem, CopValue subject, Environment env, CancellationToken cancellationToken = default)
    {
        // For chains like Type.Methods, resolve from the subject
        if (mem.Object is IdentifierExpr)
        {
            // The identifier is the type qualifier (e.g., "Type" in "Type.Methods")
            // Access the member on the subject directly
            return subject switch
            {
                CopDynamicObject dyn when (await dyn.HasFieldAsync(mem.Member, cancellationToken: cancellationToken)) => (await dyn.GetFieldAsync(mem.Member, cancellationToken: cancellationToken)),
                CopObject obj when obj.HasField(mem.Member) => obj.GetField(mem.Member),
                _ => CopNull.Instance
            };
        }
        // Nested: evaluate the object part then access member
        CopValue obj2 = (await EvalAsync(mem.Object, env, cancellationToken: cancellationToken));
        return obj2 switch
        {
            CopDynamicObject dyn => (await dyn.GetFieldAsync(mem.Member, cancellationToken: cancellationToken)),
            CopObject co => co.GetField(mem.Member),
            _ => CopNull.Instance
        };
    }

    private static async ValueTask<bool> IsEmptyAsync(CopValue value, CancellationToken cancellationToken = default) => value switch
    {
        CopThunk thunk => (await IsEmptyAsync((await thunk.ForceAsync(cancellationToken: cancellationToken)), cancellationToken: cancellationToken)),
        CopList list => list.Items.Count == 0,
        CopLazyCollection lazy => !(await lazy.EnumerateAsync(cancellationToken: cancellationToken).AnyAsync(cancellationToken: cancellationToken)),
        CopQueryable query => !(await query.EnumerateAsync(cancellationToken: cancellationToken).AnyAsync(cancellationToken: cancellationToken)),
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
    private async ValueTask<bool> ShouldApplyAtCollectionLevelAsync(string name, CopValue collection, Environment env, CancellationToken cancellationToken = default)
    {
        bool isBuiltinEmpty = name == "empty";
        List<CopFunction> overloads = env.TryLookup(name, out CopValue? binding) ? OverloadsOf(binding) : [];
        bool hasCollectionParam = overloads.Any(o =>
            o.Declaration.Params.Count == 1 && o.Declaration.Params[0].Type is { IsCollection: true });
        if (!hasCollectionParam && !isBuiltinEmpty)
        {
            return false;
        }

        List<CopFunction> scalarOverloads = overloads
            .Where(o => o.Declaration.Params.Count == 1 && o.Declaration.Params[0].Type is { IsCollection: false })
            .ToList();
        if (scalarOverloads.Count == 0)
        {
            return true; // no per-item interpretation exists
        }

        string? itemType = (await FirstItemTypeNameAsync(collection, cancellationToken: cancellationToken));
        if (itemType is null)
        {
            return false; // empty collection + a scalar overload → assume per-item
        }

        TypeRegistry? registry = TypeRegistry;
        foreach (CopFunction? o in scalarOverloads)
        {
            TypeRef pt = o.Declaration.Params[0].Type!;
            if (string.Equals(pt.Name, itemType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (registry is not null && (registry.IsSubtypeOf(itemType, pt.Name) || registry.ConformsTo(itemType, pt.Name)))
            {
                return false;
            }
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
        if (name is null || !env.TryLookup(name, out CopValue? v))
        {
            return false;
        }

        return v switch
        {
            CopFunction f => f.Declaration.Body is not IntrinsicBody,
            CopFunctionGroup g => g.Functions.Any(o => o.Declaration.Body is not IntrinsicBody),
            _ => false
        };
    }

    /// <summary>True if a bare predicate name resolves to something CALLABLE: a defined predicate or
    /// function (env or a let bound to a callable), or an FFI builtin. A non-callable binding (an enum
    /// member value, a let-bound number/string) does NOT count — otherwise <c>coll:someEnumValue</c>
    /// would bypass the typo guard and then silently filter every item out. Used so typo'd predicates
    /// fail loudly instead of silently filtering everything out.
    /// </summary>
    private async ValueTask<bool> IsResolvablePredicateNameAsync(string name, Environment env, CancellationToken cancellationToken = default)
    {
        if (env.TryLookup(name, out CopValue? bound))
        {
            return (await ForceValueAsync(bound, cancellationToken: cancellationToken)) is ICopCallable;
        }

        return _ffi.Resolve(name) is not null;
    }

    /// <summary>
    /// True if `name` resolves to a user function of arity (1 + argCount) whose FIRST parameter is
    /// a collection type — e.g. `containsAny(items:[string], values:[string])`. Such a
    /// `coll:name(args)` call is collection-level: invoke once with the whole collection as the
    /// first argument. If a same-arity overload's scalar first parameter matches the item type,
    /// per-item dispatch is preferred instead.
    /// </summary>
    private async ValueTask<bool> FirstParamIsCollectionFunctionAsync(string name, int argCount, CopValue collection, Environment env, CancellationToken cancellationToken = default)
    {
        if (!env.TryLookup(name, out CopValue? binding))
        {
            return false;
        }

        List<CopFunction> overloads = OverloadsOf(binding);
        bool hasCollectionFirst = overloads.Any(o =>
            o.Declaration.Params.Count == argCount + 1 && o.Declaration.Params[0].Type is { IsCollection: true });
        if (!hasCollectionFirst)
        {
            return false;
        }

        string? itemType = (await FirstItemTypeNameAsync(collection, cancellationToken: cancellationToken));
        if (itemType is null)
        {
            return true;
        }

        TypeRegistry? registry = TypeRegistry;
        foreach (CopFunction o in overloads)
        {
            if (o.Declaration.Params.Count != argCount + 1)
            {
                continue;
            }

            TypeRef? pt = o.Declaration.Params[0].Type;
            if (pt is null || pt.IsCollection)
            {
                continue;
            }

            if (string.Equals(pt.Name, itemType, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (registry is not null && (registry.IsSubtypeOf(itemType, pt.Name) || registry.ConformsTo(itemType, pt.Name)))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Built-in collection combinators (`concat`, `push`, `enqueue`) take the collection as their
    /// first argument and a single extra argument, so the colon-pipe form (`a:concat(b)`) is a
    /// collection-level call. They are FFI builtins (no user declaration), so the declaration-based
    /// <see cref="FirstParamIsCollectionFunction"/> can't recognise them — hence this explicit list.
    /// </summary>
    private static readonly HashSet<string> s_collectionCombinators = IntrinsicRegistry.CollectionCombinatorNames();

    private static bool IsCollectionLevelBuiltin(string name, int argCount) =>
        argCount == 1 && s_collectionCombinators.Contains(name);

    private static CopFunction? CollectionParamOverload(CopValue binding)
        => OverloadsOf(binding).FirstOrDefault(o =>
            o.Declaration.Params.Count == 1 && o.Declaration.Params[0].Type is { IsCollection: true });

    /// <summary>Type name of the first item in a collection, or null if empty/unknown.</summary>
    private async ValueTask<string?> FirstItemTypeNameAsync(CopValue collection, CancellationToken cancellationToken = default)
    {
        if (collection is CopQueryable { ItemType: not null } query)
        {
            return query.ItemType;
        }

        CopValue? first = (await CoerceToEnumerableAsync(collection, cancellationToken: cancellationToken).FirstOrDefaultAsync(cancellationToken: cancellationToken));
        if (first is CopThunk t)
        {
            first = (await t.ForceAsync(cancellationToken: cancellationToken));
        }

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
        string subjectStr = subject is CopString cs ? cs.Value
            : subject is CopNull ? ""
            : subject?.Display() ?? "";
        foreach (CopValue item in list.Items)
        {
            string itemStr = item is CopString s ? s.Value
                : item is CopNull ? ""
                : item?.Display() ?? "";
            if (subjectStr.Equals(itemStr, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static async ValueTask<CopValue> GetFieldOnValueAsync(CopValue value, string fieldName, CancellationToken cancellationToken = default) => value switch
    {
        CopDynamicObject dyn when (await dyn.HasFieldAsync(fieldName, cancellationToken: cancellationToken)) => (await dyn.GetFieldAsync(fieldName, cancellationToken: cancellationToken)),
        CopObject obj when obj.HasField(fieldName) => obj.GetField(fieldName),
        _ => CopNull.Instance
    };

    // ========================================================================
    // Call Dispatch (public for ICopCallable implementations)
    // ========================================================================

    /// <summary>
    /// Call a user-defined function with given arguments.
    /// </summary>
    public async ValueTask<CopValue> CallUserFunctionAsync(CopFunction func, IReadOnlyList<CopValue> args, CopValue? itemOverride = null, CancellationToken cancellationToken = default)
    {
        // Validate arguments before dispatch (arity + parameter types)
        await TypeValidator.ValidateArgumentsAsync(func.Declaration, args, TypeRegistry, cancellationToken: cancellationToken);

        // First-parameter guard (e.g. `predicate isMissingNamespace(Type:isCSharp)`): a guarded
        // predicate only applies to arguments its guard accepts; for others it is simply false and
        // does NOT run its body. Multi-overload guard dispatch lives in CopFunctionGroup; this
        // covers a single overload bound directly as a CopFunction so the guard isn't ignored
        // (issue #35 — otherwise `isMissingNamespace(Type:isCSharp)` ran on non-C# types too).
        if (args.Count > 0 && func.Declaration.IsPredicate
            && func.Declaration.Params.Count > 0
            && func.Declaration.Params[0].Type?.Constraint is { } guardName)
        {
            if (func.Closure.TryLookup(guardName, out CopValue? guardVal) && guardVal is ICopCallable guard
                && !(await (await guard.InvokeAsync([args[0]], this, func.Closure, cancellationToken: cancellationToken)).IsTruthyAsync(cancellationToken)))
            {
                return CopBool.False;
            }
        }

        Environment funcEnv = func.Closure.Extend();

        // Check if last parameter is a param array (collection-typed)
        int paramCount = func.Declaration.Params.Count;
        bool hasParamArray = paramCount > 0 &&
            func.Declaration.Params[^1].Type is { IsCollection: true } &&
            args.Count >= paramCount;

        // Don't use param-array collation when a single list arg is passed to match the collection param exactly
        if (hasParamArray && args.Count == paramCount)
        {
            CopValue lastArg = args[^1];
            if (lastArg is CopThunk t)
            {
                lastArg = (await t.ForceAsync(cancellationToken: cancellationToken));
            }

            if (lastArg is CopList or CopLazyCollection or CopQueryable)
            {
                hasParamArray = false;
            }
        }

        // Bind parameters
        if (hasParamArray)
        {
            // Bind all params except the last normally
            for (int i = 0; i < paramCount - 1 && i < args.Count; i++)
            {
                funcEnv.Define(func.Declaration.Params[i].Name, args[i]);
            }

            // Collect remaining args into the last param as a CopList
            List<CopValue> paramArrayItems = new List<CopValue>();
            for (int i = paramCount - 1; i < args.Count; i++)
            {
                paramArrayItems.Add(args[i]);
            }

            funcEnv.Define(func.Declaration.Params[^1].Name, new CopList(paramArrayItems));
        }
        else
        {
            for (int i = 0; i < paramCount && i < args.Count; i++)
            {
                funcEnv.Define(func.Declaration.Params[i].Name, args[i]);
            }

            // If fewer args than params, bind remaining to null
            for (int i = args.Count; i < paramCount; i++)
            {
                funcEnv.Define(func.Declaration.Params[i].Name, CopNull.Instance);
            }
        }

        // Bind "item" to the context object. In a filter such as `coll:greaterThan(1)` the explicit
        // args fill the declared params and `item` is the per-element subject (itemOverride); the
        // usual convention otherwise is that `item` aliases the first argument (issue #34).
        if (itemOverride is not null)
        {
            funcEnv.Define(ImplicitItemVariable, itemOverride);
        }
        else if (args.Count > 0)
        {
            funcEnv.Define(ImplicitItemVariable, args[0]);
        }

        // Evaluate body
        CopValue result = func.Declaration.Body switch
        {
            ExpressionBody eb => (await EvalAsync(eb.Expr, funcEnv, cancellationToken: cancellationToken)),
            MappingBody mb => (await EvalMappingBodyAsync(mb, funcEnv, cancellationToken: cancellationToken)),
            BlockBody bb => (await EvalBlockBodyAsync(bb, funcEnv, cancellationToken: cancellationToken)),
            IntrinsicBody => (await CallIntrinsicViaFFIAsync(func.Declaration.Name, args, funcEnv, cancellationToken: cancellationToken)),
            _ => CopNull.Instance
        };

        // Validate return type after execution
        await TypeValidator.ValidateReturnAsync(func.Declaration, result, cancellationToken: cancellationToken);

        return result;
    }

    /// <summary>
    /// Dispatch an intrinsic-body function to the FFI registry.
    /// </summary>
    private async ValueTask<CopValue> CallIntrinsicViaFFIAsync(string name, IReadOnlyList<CopValue> args, Environment env, CancellationToken cancellationToken = default)
    {
        CopExternalFunction? ffiFunc = _ffi.Resolve(name);
        if (ffiFunc is ICopCallable callable)
        {
            return (await callable.InvokeAsync(args, this, env, cancellationToken: cancellationToken));
        }

        throw new CopEvaluationException(
            $"Intrinsic function '{name}' has no implementation registered in FFI",
            0, _filePath);
    }

    /// <summary>
    /// Call a lambda with given arguments.
    /// </summary>
    public async ValueTask<CopValue> CallLambdaAsync(CopLambda lambda, IReadOnlyList<CopValue> args, CancellationToken cancellationToken = default)
    {
        Environment lamEnv = lambda.Closure.Extend();

        for (int i = 0; i < lambda.Expr.Params.Count && i < args.Count; i++)
        {
            lamEnv.Define(lambda.Expr.Params[i].Name, args[i]);
        }

        return (await EvalAsync(lambda.Expr.Body, lamEnv, cancellationToken: cancellationToken));
    }

    private async ValueTask<CopValue> EvalMappingBodyAsync(MappingBody body, Environment env, CancellationToken cancellationToken = default)
    {
        Dictionary<string, CopValue> fields = new Dictionary<string, CopValue>(StringComparer.Ordinal);
        foreach (FieldMapping mapping in body.Mappings)
        {
            fields[mapping.FieldName] = (await EvalAsync(mapping.Value, env, cancellationToken: cancellationToken));
        }

        return new CopObject(fields);
    }

    private async ValueTask<CopValue> EvalBlockBodyAsync(BlockBody body, Environment env, CancellationToken cancellationToken = default)
    {
        CopValue result = CopNull.Instance;
        foreach (Statement stmt in body.Statements)
        {
            if (stmt is ExpressionStatement es)
            {
                result = (await EvalAsync(es.Expr, env, cancellationToken: cancellationToken));
            }
            else
            {
                await ExecStatementAsync(stmt, env, cancellationToken: cancellationToken);
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
    public static async ValueTask<CopValue> ForceValueAsync(CopValue value, CancellationToken cancellationToken = default) =>
        value is CopThunk thunk ? (await thunk.ForceAsync(cancellationToken: cancellationToken)) : value;

    private async ValueTask<bool> ApplyPredicateAsync(CopValue predicate, CopValue item, Environment env, CancellationToken cancellationToken = default)
    {
        if (predicate is ICopCallable callable)
        {
            CopValue result = (await callable.InvokeAsync([item], this, env, cancellationToken: cancellationToken));
            return (await result.IsTruthyAsync(cancellationToken));
        }

        // If predicate is just a string name, look it up and call
        if (predicate is CopString name && env.TryLookup(name.Value, out CopValue? fn) && fn is ICopCallable c)
        {
            CopValue result = (await c.InvokeAsync([item], this, env, cancellationToken: cancellationToken));
            return (await result.IsTruthyAsync(cancellationToken));
        }

        // Truthiness test as fallback
        return (await predicate.IsTruthyAsync(cancellationToken));
    }

    private IAsyncEnumerable<CopValue> CoerceToEnumerableAsync(CopValue value,
        CancellationToken cancellationToken = default) =>
        CollectionValues.EnumerateAsync(value, cancellationToken);

    private bool MatchPattern(Pattern pattern, CopValue value, Environment env)
    {
        return pattern switch
        {
            WildcardPattern => true,
            LiteralPattern lp => MatchLiteral(EvalLiteral(new LiteralExpr(lp.Value)), value),
            IdentifierPattern ip => BindPatternVar(ip, value, env),
            _ => false
        };
    }

    /// <summary>Pattern equality for a match arm. String patterns compare case-insensitively
    /// (documented), everything else uses normal value equality.</summary>
    private static bool MatchLiteral(CopValue pattern, CopValue value)
    {
        if (pattern is CopString ps && value is CopString vs)
        {
            return string.Equals(ps.Value, vs.Value, StringComparison.OrdinalIgnoreCase);
        }

        return ValuesEqual(pattern, value);
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
    private async ValueTask<CopValue> CoerceCallableToBoolAsync(CopValue value, Environment env, CancellationToken cancellationToken = default)
    {
        if (value is ICopCallable callable && env.TryLookup(ImplicitItemVariable, out CopValue? item))
        {
            return (await callable.InvokeAsync([item], this, env, cancellationToken: cancellationToken));
        }

        return value;
    }

    private static bool ValuesEqual(CopValue a, CopValue b)
    {
        return (a, b) switch
        {
            (CopNull, CopNull) => true,
            (CopBool ba, CopBool bb) => ba.IsTrue == bb.IsTrue,
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
        (double? ln, double? rn) = (ToNumber(l), ToNumber(r));
        if (ln is null || rn is null)
        {
            return null;
        }

        double result = op(ln.Value, rn.Value);

        // Return int if both inputs were int and result is integral
        if (l is CopInt && r is CopInt && result == Math.Floor(result) && result is >= int.MinValue and <= int.MaxValue)
        {
            return new CopInt((int)result);
        }

        return new CopNumber(result);
    }

    private async ValueTask<CopValue> StringConcatAsync(CopValue l, CopValue r, int line, CancellationToken cancellationToken = default)
    {
        if (l is CopString || r is CopString)
        {
            return new CopString((await l.DisplayAsync(cancellationToken: cancellationToken)) + (await r.DisplayAsync(cancellationToken: cancellationToken)));
        }

        throw new CopEvaluationException("Cannot add non-numeric, non-string values", line, _filePath);
    }

    private async ValueTask<CopValue?> CollectionConcatAsync(CopValue l, CopValue r, CancellationToken cancellationToken = default)
    {
        // Force thunks before collection concat
        if (l is CopThunk lt)
        {
            l = (await lt.ForceAsync(cancellationToken: cancellationToken));
        }

        if (r is CopThunk rt)
        {
            r = (await rt.ForceAsync(cancellationToken: cancellationToken));
        }

        if ((l is CopQueryable || r is CopQueryable) && IsCollectionValue(l) && IsCollectionValue(r))
        {
            return CopQueryable.Concat(l, r);
        }

        IAsyncEnumerable<CopValue>? left = AsEnumerable(l);
        IAsyncEnumerable<CopValue>? right = AsEnumerable(r);
        if (left is not null && right is not null)
        {
            return new CopList(await left.Concat(right).ToListAsync(cancellationToken));
        }
        // Append/prepend a scalar to a list — documented: `[1 2] + 3` → `[1 2 3]` (issue #45).
        if (left is not null && right is null && r is not CopNull)
        {
            return new CopList(await left.Append(r).ToListAsync(cancellationToken));
        }

        if (left is null && right is not null && l is not CopNull)
        {
            return new CopList(await right.Prepend(l).ToListAsync(cancellationToken));
        }

        return null;

        IAsyncEnumerable<CopValue>? AsEnumerable(CopValue v) => v switch
        {
            CopList list => list.Items.ToAsyncEnumerable(),
            CopLazyCollection lazy => lazy.EnumerateAsync(cancellationToken: cancellationToken),
            _ => null
        };
    }

    /// <summary>
    /// `list - list` removes every left element equal to any right element; `list - scalar`
    /// removes matching scalars. Returns null when the left is not a collection so the caller
    /// falls through to the numeric/throw path. Mirrors <see cref="CollectionConcatAsync"/>.
    /// </summary>
    private async ValueTask<CopValue?> CollectionDifferenceAsync(CopValue l, CopValue r, CancellationToken cancellationToken = default)
    {
        if (l is CopThunk lt)
        {
            l = (await lt.ForceAsync(cancellationToken: cancellationToken));
        }

        if (r is CopThunk rt)
        {
            r = (await rt.ForceAsync(cancellationToken: cancellationToken));
        }

        IAsyncEnumerable<CopValue>? left = AsEnumerable(l);
        if (left is null)
        {
            return null;
        }

        List<CopValue> leftItems = await left.ToListAsync(cancellationToken);
        IAsyncEnumerable<CopValue>? right = AsEnumerable(r);
        List<CopValue>? rightItems = right is null ? null
            : await right.ToListAsync(cancellationToken);
        if (rightItems is null)
        {
            // list - scalar → drop every element equal to the scalar (null right is a no-op).
            if (r is CopNull)
            {
                return new CopList(leftItems);
            }

            return new CopList((await leftItems.ToAsyncEnumerable().Where(async (x, itemCancellationToken) => !(await ElementsEqualAsync(x, r, cancellationToken: itemCancellationToken))).ToListAsync(cancellationToken: cancellationToken)));
        }
        // list - list → drop every left element equal to any right element.
        return new CopList((await leftItems.ToAsyncEnumerable().Where(async (x, itemCancellationToken) => !(await rightItems.ToAsyncEnumerable().AnyAsync(async (y, itemCancellationToken2) => (await ElementsEqualAsync(x, y, cancellationToken: itemCancellationToken2)), cancellationToken: itemCancellationToken))).ToListAsync(cancellationToken: cancellationToken)));

        IAsyncEnumerable<CopValue>? AsEnumerable(CopValue v) => v switch
        {
            CopList list => list.Items.ToAsyncEnumerable(),
            CopLazyCollection lazy => lazy.EnumerateAsync(cancellationToken: cancellationToken),
            _ => null
        };
    }

    /// <summary>
    /// Element equality for collection difference: two <see cref="CopObject"/>s (e.g. Violations)
    /// are equal when they share a type name and have field-by-field equal values; everything
    /// else defers to <see cref="ValuesEqual"/> (scalars by value, other objects by reference).
    /// </summary>
    private static async ValueTask<bool> ElementsEqualAsync(CopValue a, CopValue b, CancellationToken cancellationToken = default)
    {
        if (a is CopThunk at)
        {
            a = (await at.ForceAsync(cancellationToken: cancellationToken));
        }

        if (b is CopThunk bt)
        {
            b = (await bt.ForceAsync(cancellationToken: cancellationToken));
        }

        if (a is CopObject oa && b is CopObject ob)
        {
            if (oa.TypeName != ob.TypeName || oa.Fields.Count != ob.Fields.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, CopValue> kv in oa.Fields)
            {
                if (!ob.Fields.TryGetValue(kv.Key, out CopValue? bv) || !(await ElementsEqualAsync(kv.Value, bv, cancellationToken: cancellationToken)))
                {
                    return false;
                }
            }
            return true;
        }
        return ValuesEqual(a, b);
    }

    private CopValue CompareOp(CopValue l, CopValue r, Func<int, bool> predicate, int line)
    {
        (double? ln, double? rn) = (ToNumber(l), ToNumber(r));
        if (ln is not null && rn is not null)
        {
            return CopBool.Of(predicate(ln.Value.CompareTo(rn.Value)));
        }

        if (l is CopString ls && r is CopString rs)
        {
            return CopBool.Of(predicate(string.Compare(ls.Value, rs.Value, StringComparison.Ordinal)));
        }

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
        if (_globalEnv.TryLookup(name, out CopValue? existing))
        {
            if (existing is CopFunctionGroup group)
            {
                group.Add(func);
                return;
            }
            if (existing is CopFunction existingFunc)
            {
                CopFunctionGroup newGroup = new CopFunctionGroup(name);
                newGroup.Add(existingFunc);
                newGroup.Add(func);
                _globalEnv.Define(name, newGroup);
                return;
            }
        }
        _globalEnv.Define(name, func);
    }
}
