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

    public Environment GlobalEnvironment => _globalEnv;
    public ForeignFunctionRegistry FFI => _ffi;

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
                throw new CopEvaluationException($"Command '{name}' not found", filePath: _filePath);
        }

        if (value is CopFunction func && func.Declaration is not null)
        {
            return CallUserFunction(func, []);
        }

        if (value is ICopCallable callable)
            return callable.Invoke([], this, _globalEnv);

        throw new CopEvaluationException($"'{name}' is not callable", filePath: _filePath);
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
                _globalEnv.Define(fd.Name, func);
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
                break;

            case FlagsDecl fd2:
                foreach (var member in fd2.Members)
                    _globalEnv.Define(member, new CopString(member));
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
        var items = CoerceToEnumerable(collection);

        foreach (var item in items)
        {
            var iterEnv = env.Extend();
            iterEnv.Define("item", item);
            iterEnv.Define(stmt.Variable, item);

            foreach (var bodyStmt in stmt.Body)
                ExecStatement(bodyStmt, iterEnv);
        }
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
            var left = Eval(bin.Left, env);
            return left.IsTruthy ? Eval(bin.Right, env) : left;
        }
        if (bin.Op == BinaryOp.Or)
        {
            var left = Eval(bin.Left, env);
            return left.IsTruthy ? left : Eval(bin.Right, env);
        }

        var l = Eval(bin.Left, env);
        var r = Eval(bin.Right, env);

        return bin.Op switch
        {
            BinaryOp.Add => NumericOp(l, r, (a, b) => a + b, bin.Line)
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
        var operand = Eval(un.Operand, env);
        return un.Op switch
        {
            UnaryOp.Not => CopBool.Of(!operand.IsTruthy),
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
        var callee = Eval(call.Callee, env);
        var args = call.Args.Select(a => Eval(a, env)).ToList();

        if (callee is ICopCallable callable)
            return callable.Invoke(args, this, env);

        throw new CopEvaluationException(
            $"Value of type {callee.GetType().Name} is not callable",
            call.Line, _filePath);
    }

    private CopValue EvalMember(MemberExpr mem, Environment env)
    {
        var obj = Eval(mem.Object, env);

        return obj switch
        {
            CopObject co => co.GetField(mem.Member),
            CopDynamicObject dyn => dyn.GetField(mem.Member),
            CopList list => EvalListMember(list, mem.Member, mem.Line),
            CopLazyCollection lazy => EvalLazyMember(lazy, mem.Member, mem.Line),
            CopString str => EvalStringMember(str, mem.Member, mem.Line),
            _ => throw new CopEvaluationException(
                $"Cannot access member '{mem.Member}' on {obj.GetType().Name}",
                mem.Line, _filePath)
        };
    }

    private CopValue EvalListMember(CopList list, string member, int line) => member switch
    {
        "Count" or "count" or "Length" or "length" => new CopInt(list.Items.Count),
        "First" or "first" => list.Items.Count > 0 ? list.Items[0] : CopNull.Instance,
        "Last" or "last" => list.Items.Count > 0 ? list.Items[^1] : CopNull.Instance,
        _ => throw new CopEvaluationException($"Unknown list member '{member}'", line, _filePath)
    };

    private CopValue EvalLazyMember(CopLazyCollection lazy, string member, int line) => member switch
    {
        "Count" or "count" or "Length" or "length" => new CopInt(lazy.Enumerate().Count()),
        "First" or "first" => lazy.Enumerate().FirstOrDefault() ?? CopNull.Instance,
        _ => throw new CopEvaluationException($"Unknown collection member '{member}'", line, _filePath)
    };

    private CopValue EvalStringMember(CopString str, string member, int line) => member switch
    {
        "Length" or "length" => new CopInt(str.Value.Length),
        _ => throw new CopEvaluationException($"Unknown string member '{member}'", line, _filePath)
    };

    private CopValue EvalIndex(IndexExpr idx, Environment env)
    {
        var obj = Eval(idx.Object, env);
        var index = Eval(idx.Index, env);

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
        var condition = Eval(cond.Condition, env);
        return condition.IsTruthy
            ? Eval(cond.Then, env)
            : Eval(cond.Else, env);
    }

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
        return new CopObject(fields);
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
                    sb.Append(val.Display());
                    break;
            }
        }
        return new CopString(sb.ToString());
    }

    private CopValue EvalFilter(FilterExpr filter, Environment env)
    {
        var collection = Eval(filter.Collection, env);
        var predicate = Eval(filter.Predicate, env);
        bool negated = filter.Negated;

        // Return a lazy filtered collection
        return new CopLazyCollection(() =>
        {
            var items = CoerceToEnumerable(collection);
            return items.Where(item =>
            {
                var result = ApplyPredicate(predicate, item, env);
                return negated ? !result : result;
            });
        });
    }

    // ========================================================================
    // Call Dispatch (public for ICopCallable implementations)
    // ========================================================================

    /// <summary>
    /// Call a user-defined function with given arguments.
    /// </summary>
    public CopValue CallUserFunction(CopFunction func, IReadOnlyList<CopValue> args)
    {
        var funcEnv = func.Closure.Extend();

        // Bind parameters
        for (int i = 0; i < func.Declaration.Params.Count && i < args.Count; i++)
            funcEnv.Define(func.Declaration.Params[i].Name, args[i]);

        // If fewer args than params, bind remaining to null
        for (int i = args.Count; i < func.Declaration.Params.Count; i++)
            funcEnv.Define(func.Declaration.Params[i].Name, CopNull.Instance);

        // Evaluate body
        return func.Declaration.Body switch
        {
            ExpressionBody eb => Eval(eb.Expr, funcEnv),
            MappingBody mb => EvalMappingBody(mb, funcEnv),
            BlockBody bb => EvalBlockBody(bb, funcEnv),
            IntrinsicBody => throw new CopEvaluationException(
                $"Intrinsic function '{func.Declaration.Name}' has no implementation registered",
                func.Declaration.Line, _filePath),
            _ => CopNull.Instance
        };
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
