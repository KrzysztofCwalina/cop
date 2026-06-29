namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// Static type-checking pass for <c>cop verify</c>. It infers the type of expressions where it
/// can do so with confidence and reports an error when a function-call argument's inferred type
/// is definitely incompatible with the callee's declared parameter type.
///
/// Design principle — <b>unknown means no error</b>. cop intentionally allows runtime-provided
/// names (dynamic provider fields, short predicate names, external exports), so the checker only
/// flags a problem when BOTH the argument's actual type and the parameter's expected type are
/// known and concrete and genuinely incompatible (accounting for subtyping and trait conformance).
/// Anything it cannot infer is treated as compatible, guaranteeing no false positives on the
/// existing corpus.
/// </summary>
public sealed class TypeChecker
{
    /// <summary>An inferred type: a type name plus whether it is a collection. Null = unknown.</summary>
    private sealed record IT(string Name, bool IsCollection);

    /// <summary>A callable signature (one overload).</summary>
    private sealed record Sig(string Name, IReadOnlyList<TypeRef?> Params, bool LastIsVariadic, TypeRef? Return, bool IsPredicate);

    // Merged type model (unioned across every declaration, local + imported).
    private readonly Dictionary<string, HashSet<string>> _bases = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, TypeRef>> _props = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<Sig>> _funcs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(TypeRef? Input, TypeRef Output)>> _narrowings = new(StringComparer.Ordinal); // predicate name -> narrowing overloads
    private readonly HashSet<string> _enums = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownTypes = new(StringComparer.Ordinal);

    // Builtin pseudo-predicates with special filter semantics that may shadow a same-named
    // declared function (e.g. `:empty` is collection-emptiness, not the files package's
    // `empty(Folder)`). Filter applications using these names are not type-checked. Derived from
    // the single IntrinsicRegistry so it can never drift from the language's actual built-ins.
    private static readonly HashSet<string> BuiltinFilterNames =
        IntrinsicRegistry.NameSet(o => o.IsBuiltinFilter);

    private readonly List<CopDiagnostic> _diagnostics = [];
    private string _file = "";
    private string? _src;

    // Per-declaration local variable types (parameters + let statements). null value = known-unknown.
    private readonly Dictionary<string, IT?> _locals = new(StringComparer.Ordinal);

    // Top-level `let` bindings visible across the whole program (one cop-checks/ folder).
    private readonly Dictionary<string, IT?> _topLevelLets = new(StringComparer.Ordinal);

    /// <summary>
    /// Type-checks <paramref name="filesToCheck"/>, using every module in
    /// <paramref name="allModules"/> (local + imported) to build the type/function model.
    /// </summary>
    public static IReadOnlyList<CopDiagnostic> Check(
        IEnumerable<ModuleNode> allModules,
        IEnumerable<(ModuleNode Module, string FilePath, string Source)> filesToCheck)
    {
        var checker = new TypeChecker();
        var files = filesToCheck.ToList();
        checker.BuildModel(allModules, files.Select(f => f.Module));
        foreach (var (module, filePath, source) in files)
            checker.CheckModule(module, filePath, source);
        return checker._diagnostics;
    }

    /// <summary>
    /// Indexes every module and resolves top-level <c>let</c> types (two rounds, so a let may
    /// reference one declared later, e.g. checks referencing <c>codebase</c>). Shared by
    /// <see cref="Check"/> and the <see cref="SemanticModel"/> facade so <c>cop verify</c> and the
    /// editor use ONE type model and ONE inference engine.
    /// </summary>
    private void BuildModel(IEnumerable<ModuleNode> allModules, IEnumerable<ModuleNode> letScope)
    {
        foreach (var module in allModules)
            IndexModule(module);
        var scope = letScope.ToList();
        for (int round = 0; round < 2; round++)
            foreach (var module in scope)
                foreach (var decl in module.Declarations)
                    if (decl is LetDecl ld)
                        _topLevelLets[ld.Name] =
                            ld.TypeAnnotation is not null ? ToIT(ld.TypeAnnotation) : InferExpr(ld.Value);
    }

    // ── Model building ──────────────────────────────────────────────────────

    private void IndexModule(ModuleNode module)
    {
        foreach (var decl in module.Declarations)
        {
            switch (decl)
            {
                case TypeDecl td:
                    _knownTypes.Add(td.Name);
                    var bases = _bases.TryGetValue(td.Name, out var b) ? b : (_bases[td.Name] = new(StringComparer.Ordinal));
                    if (!string.IsNullOrEmpty(td.BaseType)) bases.Add(td.BaseType);
                    if (td.Traits is not null) foreach (var t in td.Traits) bases.Add(t);
                    var props = _props.TryGetValue(td.Name, out var p) ? p : (_props[td.Name] = new(StringComparer.Ordinal));
                    foreach (var pd in td.Properties)
                        props[pd.Name] = pd.Type;
                    break;

                case EnumDecl ed:
                    _enums.Add(ed.Name);
                    _knownTypes.Add(ed.Name);
                    break;

                case FlagsDecl fd:
                    _enums.Add(fd.Name);
                    _knownTypes.Add(fd.Name);
                    break;

                case FunctionDecl fn:
                    var sigs = _funcs.TryGetValue(fn.Name, out var s) ? s : (_funcs[fn.Name] = []);
                    var ps = fn.Params.Select(pp => pp.Type).ToList();
                    bool variadic = ps.Count > 0 && ps[^1] is { IsCollection: true };
                    sigs.Add(new Sig(fn.Name, ps, variadic, fn.ReturnType, fn.IsPredicate));
                    // A narrowing predicate (`predicate p(T) : NarrowType => ...`) narrows the
                    // element type when used as a filter. A plain boolean predicate (ReturnType
                    // "bool" or none) does NOT narrow — it preserves the element type. Narrowing
                    // predicates are overloaded by input type (asCSharp(Type)/(Method)/(Statement)),
                    // so we record each (input, output) overload.
                    if (fn.IsPredicate && fn.ReturnType is not null
                        && !string.Equals(fn.ReturnType.Name, "bool", StringComparison.Ordinal)
                        && fn.Params.Count == 1)
                    {
                        var list = _narrowings.TryGetValue(fn.Name, out var nl) ? nl : (_narrowings[fn.Name] = []);
                        list.Add((fn.Params[0].Type, fn.ReturnType));
                    }
                    break;
            }
        }
    }

    // ── Checking ────────────────────────────────────────────────────────────

    private void CheckModule(ModuleNode module, string filePath, string source)
    {
        _file = filePath;
        _src = source;
        foreach (var decl in module.Declarations)
            CheckDeclaration(decl);
    }

    private void CheckDeclaration(Declaration decl)
    {
        _locals.Clear();
        switch (decl)
        {
            case FunctionDecl fn:
                foreach (var prm in fn.Params)
                    _locals[prm.Name] = prm.Type is null ? null : ToIT(prm.Type);
                if (fn.Guard is not null) WalkExpr(fn.Guard);
                CheckBody(fn.Body);
                break;
            case LetDecl ld:
                WalkExpr(ld.Value);
                break;
            case CommandDecl cmd:
                foreach (var st in cmd.Body) CheckStatement(st);
                break;
        }
    }

    private void CheckBody(FunctionBody body)
    {
        switch (body)
        {
            case ExpressionBody eb: WalkExpr(eb.Expr); break;
            case BlockBody bb: foreach (var st in bb.Statements) CheckStatement(st); break;
        }
    }

    private void CheckStatement(Statement st)
    {
        switch (st)
        {
            case LetStatement ls:
                WalkExpr(ls.Value);
                _locals[ls.Name] = ls.TypeAnnotation is not null ? ToIT(ls.TypeAnnotation) : InferExpr(ls.Value);
                break;
            case ExpressionStatement es: WalkExpr(es.Expr); break;
            case ForEachStatement fe:
                WalkExpr(fe.Collection);
                foreach (var s in fe.Body) CheckStatement(s);
                break;
        }
    }

    /// <summary>Recursively walks an expression, type-checking every call/filter inside it.</summary>
    private void WalkExpr(Expression? expr)
    {
        switch (expr)
        {
            case null: return;
            case BinaryExpr be: WalkExpr(be.Left); WalkExpr(be.Right); break;
            case UnaryExpr ue: WalkExpr(ue.Operand); break;
            case CallExpr ce:
                WalkExpr(ce.Callee);
                foreach (var a in ce.Args) WalkExpr(a);
                CheckDirectCall(ce);
                break;
            case MemberExpr me: WalkExpr(me.Object); break;
            case IndexExpr ie: WalkExpr(ie.Object); WalkExpr(ie.Index); break;
            case ConditionalExpr c: WalkExpr(c.Condition); WalkExpr(c.Then); WalkExpr(c.Else); break;
            case MatchExpr m: WalkExpr(m.Discriminant); foreach (var arm in m.Arms) WalkExpr(arm.Body); break;
            case ListExpr l: foreach (var el in l.Elements) WalkExpr(el); break;
            case ObjectExpr o: foreach (var f in o.Fields) WalkExpr(f.Value); break;
            case InterpolatedStringExpr s:
                foreach (var part in s.Parts) if (part is ExpressionPart ep) WalkExpr(ep.Expr);
                break;
            case FilterExpr fe:
                WalkExpr(fe.Collection);
                CheckFilter(fe);
                break;
            case ForEachExpr fee:
                WalkExpr(fee.Loop.Collection);
                foreach (var s in fee.Loop.Body) CheckStatement(s);
                break;
        }
    }

    /// <summary>Checks a direct call <c>f(args)</c> against the declared signature(s) of f.</summary>
    private void CheckDirectCall(CallExpr call)
    {
        if (call.Callee is not IdentifierExpr id) return;
        if (!_funcs.TryGetValue(id.Name, out var overloads)) return;
        var argTypes = call.Args.Select(InferExpr).ToList();
        CheckAgainstOverloads(id.Name, overloads, argTypes, call.Line);
    }

    /// <summary>
    /// Checks a colon filter/application <c>collection:pred</c> or <c>collection:method(args)</c>.
    /// The collection's element type becomes the implicit first argument.
    /// </summary>
    private void CheckFilter(FilterExpr filter)
    {
        var elem = ElementType(InferExpr(filter.Collection));

        switch (filter.Predicate)
        {
            case IdentifierExpr pid:
                // collection:predicate  → predicate(element). Skip builtin pseudo-predicates
                // (e.g. `:empty`) whose runtime semantics differ from any same-named declared
                // function.
                if (!BuiltinFilterNames.Contains(pid.Name) && _funcs.TryGetValue(pid.Name, out var preds))
                    CheckAgainstOverloads(pid.Name, preds, [elem], filter.Line);
                break;
            case CallExpr pcall when pcall.Callee is IdentifierExpr mid:
                // collection:method(args) → method(element, args...)
                foreach (var a in pcall.Args) WalkExpr(a);
                if (!BuiltinFilterNames.Contains(mid.Name) && _funcs.TryGetValue(mid.Name, out var methods))
                {
                    var args = new List<IT?> { elem };
                    args.AddRange(pcall.Args.Select(InferExpr));
                    CheckAgainstOverloads(mid.Name, methods, args, filter.Line);
                }
                break;
        }
    }

    /// <summary>
    /// Reports an error only when NO declared overload can accept the given argument types AND at
    /// least one argument is a confident, concrete mismatch (so partial/unknown info never errors).
    /// </summary>
    private void CheckAgainstOverloads(string name, List<Sig> overloads, List<IT?> args, int line)
    {
        // If any overload plausibly accepts the args, accept (overload resolution is permissive).
        foreach (var sig in overloads)
            if (Accepts(sig, args, out _))
                return;

        // No overload accepts. Find the most specific concrete mismatch to report (single-overload
        // case gives the clearest message; with multiple overloads we only report if every overload
        // fails for the SAME definite reason on the same argument).
        if (overloads.Count == 1)
        {
            Accepts(overloads[0], args, out var mismatch);
            if (mismatch is not null)
                Report(mismatch, line);
        }
    }

    /// <summary>
    /// True if <paramref name="sig"/> can accept <paramref name="args"/>. When it cannot AND the
    /// reason is a confident concrete type mismatch, <paramref name="mismatch"/> describes it;
    /// when the failure is only due to unknown/uninferred info, returns true (no error).
    /// </summary>
    private bool Accepts(Sig sig, List<IT?> args, out string? mismatch)
    {
        mismatch = null;
        int fixedCount = sig.LastIsVariadic ? sig.Params.Count - 1 : sig.Params.Count;

        // Arity: too few fixed args, or too many when not variadic — but only treat as a hard
        // mismatch when we are confident (variadic/param-array semantics make this fuzzy, so we
        // do NOT flag arity here to stay conservative; argument-type checking is the focus).
        if (!sig.LastIsVariadic && args.Count > sig.Params.Count) return true;  // skip (lenient)
        if (args.Count < fixedCount) return true;                                // skip (lenient)

        for (int i = 0; i < args.Count; i++)
        {
            var expected = i < sig.Params.Count ? sig.Params[i]
                         : (sig.LastIsVariadic ? sig.Params[^1] : null);
            var actual = args[i];
            if (!Assignable(actual, expected, out var why))
            {
                mismatch = $"'{sig.Name}' argument {i + 1} expects {Describe(expected)}, got {Describe(actual)}{why}";
                return false;
            }
        }
        return true;
    }

    // ── Compatibility ───────────────────────────────────────────────────────

    /// <summary>
    /// Returns true if a value of inferred type <paramref name="actual"/> is assignable to a
    /// parameter declared as <paramref name="expected"/>. Conservative: any uncertainty ⇒ true.
    /// </summary>
    private bool Assignable(IT? actual, TypeRef? expected, out string why)
    {
        why = "";
        if (expected is null) return true;                      // untyped parameter
        if (actual is null) return true;                        // unknown argument type
        var exp = expected.Name;
        if (GenericInference.IsTypeVariable(exp)) return true;  // generic parameter
        var expl = exp.ToLowerInvariant();
        if (expl is "object" or "any") return true;             // top type
        if (exp.Contains("=>") || expl is "lambda" or "function" or "predicate" or "collection" or "provider")
            return true;                                        // callable / opaque

        // Collection expectations: a collection arg is fine; a scalar arg we skip (cop may wrap).
        if (expected.IsCollection)
            return true;

        // Scalar expected, collection actual: the runtime cannot anchor/coerce a collection into a
        // single value — this is a real error (e.g. a toError anchor that is a collection).
        if (actual.IsCollection)
        {
            why = " (a collection, not a single value)";
            return false;
        }

        var act = actual.Name;
        if (string.Equals(act, exp, StringComparison.OrdinalIgnoreCase)) return true;

        bool expIsPrimitive = IsPrimitive(expl);
        bool actIsPrimitive = IsPrimitive(act.ToLowerInvariant());

        // Enum members are strings at runtime; treat enum and string as mutually compatible.
        if ((_enums.Contains(exp) || expl == "string") && (_enums.Contains(act) || act.Equals("string", StringComparison.OrdinalIgnoreCase)))
            return true;

        if (expIsPrimitive)
        {
            if (!actIsPrimitive) return true;                   // named/unknown actual vs primitive — be lenient
            // both primitive and not equal: int↔float widening is allowed; everything else mismatches.
            if (expl == "float" && (act.Equals("int", StringComparison.OrdinalIgnoreCase) || act.Equals("float", StringComparison.OrdinalIgnoreCase))) return true;
            why = "";
            return false;
        }

        // Expected a named (record/trait) type.
        if (actIsPrimitive)
        {
            // A primitive where a named record type is expected is a real mismatch
            // (e.g. toError(123, ...) where a TextFilePosition is required).
            return false;
        }

        // Both named: assignable if actual is the same type, a subtype, or conforms to the trait.
        if (IsSubtypeOrConforms(act, exp)) return true;

        // If we don't actually know the actual type (not in our model), stay lenient.
        if (!_knownTypes.Contains(act)) return true;

        return false;
    }

    private bool IsSubtypeOrConforms(string actual, string expected)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(actual);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!seen.Add(t)) continue;
            if (string.Equals(t, expected, StringComparison.OrdinalIgnoreCase)) return true;
            if (_bases.TryGetValue(t, out var bases))
                foreach (var b in bases) stack.Push(b);
        }
        return false;
    }

    private static bool IsPrimitive(string lower) =>
        lower is "string" or "int" or "float" or "bool" or "byte" or "bytes";

    // ── Inference ───────────────────────────────────────────────────────────

    /// <summary>Infers the type of an expression, or null when it cannot be determined confidently.</summary>
    private IT? InferExpr(Expression? expr)
    {
        switch (expr)
        {
            case LiteralExpr lit:
                return lit.Value switch
                {
                    int => new IT("int", false),
                    long => new IT("int", false),
                    double => new IT("float", false),
                    float => new IT("float", false),
                    bool => new IT("bool", false),
                    string => new IT("string", false),
                    _ => null
                };

            case InterpolatedStringExpr: return new IT("string", false);

            case ListExpr list:
                if (list.Elements.Count == 0) return new IT("object", true);
                var first = InferExpr(list.Elements[0]);
                return first is null ? new IT("object", true) : new IT(first.Name, true);

            case IdentifierExpr id:
                if (_locals.TryGetValue(id.Name, out var lt)) return lt;
                if (_topLevelLets.TryGetValue(id.Name, out var tl)) return tl;
                return null;

            case MemberExpr me:
                return InferMember(me);

            case CallExpr ce:
                return InferCall(ce);

            case FilterExpr fe:
                return InferFilter(fe);

            case BinaryExpr be:
                if (IsBooleanOp(be.Op)) return new IT("bool", false);
                // `+` concatenates lists or adds scalars. For violation checks the common shape is
                // `let all = a + b + c` where each part is `[Violation]`; the result is that same
                // collection type. Inferring it (instead of giving up) is what lets the editor show
                // `[Violation]` on hover instead of "unknown", and is consistent for `cop verify`.
                return be.Op == BinaryOp.Add ? InferAddition(be.Left, be.Right) : null;

            case UnaryExpr ue:
                return ue.Op == UnaryOp.Not ? new IT("bool", false) : null;

            case ObjectExpr oe:
                return oe.TypeHint is not null ? new IT(oe.TypeHint, false) : new IT("object", false);

            case ConditionalExpr c:
                var t = InferExpr(c.Then);
                var e = InferExpr(c.Else);
                return t is not null && e is not null && t == e ? t : null;

            default: return null;
        }
    }

    /// <summary>
    /// Infers the type of <c>left + right</c>. <c>+</c> concatenates collections (e.g. unioning
    /// violation lists) or adds scalars. When both sides agree, that type is the result; when only
    /// one side is a known collection, the result is that collection (concatenation preserves it).
    /// </summary>
    private IT? InferAddition(Expression left, Expression right)
    {
        var l = InferExpr(left);
        var r = InferExpr(right);
        if (l is not null && r is not null)
        {
            if (l == r) return l;
            if (l.IsCollection && r.IsCollection && string.Equals(l.Name, r.Name, StringComparison.Ordinal))
                return l;
        }
        if (l is { IsCollection: true }) return l;
        if (r is { IsCollection: true }) return r;
        return null;
    }

    private IT? InferMember(MemberExpr me)
    {
        var obj = InferExpr(me.Object);
        if (obj is null) return null;
        if (obj.IsCollection)
        {
            // A few well-known collection members; everything else is unknown.
            return me.Member is "Count" ? new IT("int", false) : null;
        }
        var pt = LookupProperty(obj.Name, me.Member);
        return pt is null ? null : ToIT(pt);
    }

    private TypeRef? LookupProperty(string typeName, string member)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(typeName);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!seen.Add(t)) continue;
            if (_props.TryGetValue(t, out var props) && props.TryGetValue(member, out var pt))
                return pt;
            if (_bases.TryGetValue(t, out var bases))
                foreach (var b in bases) stack.Push(b);
        }
        return null;
    }

    private IT? InferCall(CallExpr call)
    {
        if (call.Callee is not IdentifierExpr id) return null;
        var rt = GetReturnType(id.Name);
        return rt is null || GenericInference.IsTypeVariable(rt.Name) ? null : ToIT(rt);
    }

    /// <summary>Return type of a function when it has exactly one (unambiguous) overload.</summary>
    private TypeRef? GetReturnType(string name) =>
        _funcs.TryGetValue(name, out var s) && s.Count == 1 ? s[0].Return : null;

    private IT? InferFilter(FilterExpr filter)
    {
        var coll = InferExpr(filter.Collection);
        var elem = ElementType(coll);

        switch (filter.Predicate)
        {
            case IdentifierExpr pid:
                // Narrowing predicate narrows the element.
                if (_narrowings.TryGetValue(pid.Name, out var narrowOverloads))
                {
                    var output = ResolveNarrowing(narrowOverloads, elem);
                    if (output is not null) return new IT(output.Name, true);
                }
                // A declared boolean predicate is a filter — it preserves the element type.
                if (_funcs.ContainsKey(pid.Name))
                    return coll is { IsCollection: true } ? coll : (elem is null ? null : new IT(elem.Name, true));
                // A property name is a projection: collection:Prop maps each element to its Prop.
                if (elem is not null)
                {
                    var proj = LookupProperty(elem.Name, pid.Name);
                    if (proj is not null) return new IT(proj.Name, true);
                }
                return null; // unknown predicate/projection — element type is undetermined
            case CallExpr pcall when pcall.Callee is IdentifierExpr mid:
                // collection:method(...) maps each element to method's return type.
                var rt = GetReturnType(mid.Name);
                return rt is null ? null : new IT(rt.Name, true);
            default:
                return coll;
        }
    }

    /// <summary>Picks the narrowing overload whose declared input matches the element type.</summary>
    private TypeRef? ResolveNarrowing(List<(TypeRef? Input, TypeRef Output)> overloads, IT? elem)
    {
        if (elem is null)
            return overloads.Count == 1 ? overloads[0].Output : null; // ambiguous when unknown
        foreach (var (input, output) in overloads)
        {
            if (input is null) return output;
            if (string.Equals(input.Name, elem.Name, StringComparison.OrdinalIgnoreCase)
                || IsSubtypeOrConforms(elem.Name, input.Name))
                return output;
        }
        return null; // no matching overload — preserve the element type
    }

    private static IT? ElementType(IT? collection) =>
        collection is null ? null : new IT(collection.Name, false);

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static IT ToIT(TypeRef t) => new(t.Name, t.IsCollection);

    private static bool IsBooleanOp(BinaryOp op) => op is
        BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.LessThan or BinaryOp.LessOrEqual or
        BinaryOp.GreaterThan or BinaryOp.GreaterOrEqual or BinaryOp.And or BinaryOp.Or;

    private string Describe(IT? t) =>
        t is null ? "an unknown value" : t.IsCollection ? $"a collection of {t.Name}" : t.Name;

    private string Describe(TypeRef? t) =>
        t is null ? "any" : t.IsCollection ? $"a collection of {t.Name}" : t.Name;

    private void Report(string message, int line)
    {
        var sourceLine = _src is not null ? ParseException.GetSourceLine(_src, line) : null;
        _diagnostics.Add(new CopDiagnostic(CopDiagnosticSeverity.Error, message, _file, line, SourceLine: sourceLine));
    }

    // ── SemanticModel facade hooks ───────────────────────────────────────────
    // Editor hover/completion run the REAL type model + inference (the same code that powers
    // `cop verify`), exposed here so tooling never reimplements the compiler.

    /// <summary>Builds a checker populated with the merged type model, for editor queries.</summary>
    internal static TypeChecker ForModel(IEnumerable<ModuleNode> modules)
    {
        var checker = new TypeChecker();
        var mods = modules.ToList();
        checker.BuildModel(mods, mods);
        return checker;
    }

    /// <summary>Infers the type of an expression with an explicit local scope (e.g. the implicit
    /// <c>item</c> or a predicate parameter), using the real inference engine.</summary>
    internal TypeInfo? InferWithLocals(Expression? expr, IReadOnlyDictionary<string, TypeInfo>? locals)
    {
        _locals.Clear();
        if (locals is not null)
            foreach (var kv in locals)
                _locals[kv.Key] = new IT(kv.Value.Name, kv.Value.IsCollection);
        var it = InferExpr(expr);
        return it is null ? null : new TypeInfo(it.Name, it.IsCollection);
    }

    /// <summary>Finds a property by name, walking base types/traits; returns its declaring type.</summary>
    internal PropertyInfo? FindProperty(string typeName, string member)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(typeName);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!seen.Add(t)) continue;
            if (_props.TryGetValue(t, out var props) && props.TryGetValue(member, out var pt))
                return new PropertyInfo(member, pt.IsCollection ? $"[{pt.Name}]" : pt.Name, t);
            if (_bases.TryGetValue(t, out var bases))
                foreach (var b in bases) stack.Push(b);
        }
        return null;
    }

    /// <summary>Enumerates all properties of a type, including inherited ones (deduped by name).</summary>
    internal IReadOnlyList<PropertyInfo> AllProperties(string typeName)
    {
        var result = new List<PropertyInfo>();
        var seenProp = new HashSet<string>(StringComparer.Ordinal);
        var seenType = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(typeName);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            if (!seenType.Add(t)) continue;
            if (_props.TryGetValue(t, out var props))
                foreach (var kv in props)
                    if (seenProp.Add(kv.Key))
                        result.Add(new PropertyInfo(kv.Key, kv.Value.IsCollection ? $"[{kv.Value.Name}]" : kv.Value.Name, t));
            if (_bases.TryGetValue(t, out var bases))
                foreach (var b in bases) stack.Push(b);
        }
        return result;
    }

    /// <summary>Signature info for a declared predicate or function (first overload).</summary>
    internal CallableInfo? Callable(string name)
    {
        if (!_funcs.TryGetValue(name, out var sigs) || sigs.Count == 0) return null;
        var sig = sigs[0];
        var pars = sig.Params.Select(p => p is null ? null : (p.IsCollection ? $"[{p.Name}]" : p.Name)).ToList();
        string? ret = sig.Return is null ? null : (sig.Return.IsCollection ? $"[{sig.Return.Name}]" : sig.Return.Name);
        return new CallableInfo(name, sig.IsPredicate, pars, ret, _narrowings.ContainsKey(name));
    }

    internal TypeInfo? TopLevelLet(string name, out bool found)
    {
        if (_topLevelLets.TryGetValue(name, out var it))
        {
            found = true;
            return it is null ? null : new TypeInfo(it.Name, it.IsCollection);
        }
        found = false;
        return null;
    }

    internal IReadOnlyCollection<string> LetNames() => _topLevelLets.Keys;
    internal IReadOnlyCollection<string> KnownTypes() => _knownTypes;
    internal IReadOnlyCollection<string> CallableNames() => _funcs.Keys;
    internal bool IsEnumName(string name) => _enums.Contains(name);
    internal bool IsKnownType(string name) => _knownTypes.Contains(name);
    internal bool IsSubtypeOf(string sub, string super) => IsSubtypeOrConforms(sub, super);
}
