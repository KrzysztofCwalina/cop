namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;

/// <summary>
/// The Binder walks an AST module, builds scopes, resolves identifiers to symbols,
/// and produces a BindingResult. It does NOT evaluate — only resolves names and
/// reports diagnostics for unresolved or duplicate declarations.
///
/// Design: three-pass binding within a module.
///   Pass 1: Register all top-level declarations (types, functions, lets, enums, commands)
///           so that forward references work.
///   Pass 2: Walk function/command bodies and expressions, resolving identifiers
///           against the scope chain.
///   Pass 3: Validate all type references, function call arity, and report errors
///           for unresolved symbols.
/// </summary>
public sealed class Binder
{
    private readonly string? _filePath;
    private BindingResult _result = null!;
    private Scope _currentScope = null!;

    /// <summary>
    /// Parameters of the function currently being validated (Pass 3).
    /// Used to resolve member-access type information for enum comparison checks.
    /// </summary>
    private IReadOnlyList<Parameter>? _currentFunctionParams;

    /// <summary>
    /// Optional external symbols to pre-populate the global scope with
    /// (intrinsics, runtime-provided functions, etc.).
    /// </summary>
    private readonly IReadOnlyList<Symbol> _externalSymbols;

    /// <summary>
    /// Names of all top-level declarations across every file of the same program
    /// (the cop-checks/ pattern, where main.cop references lets defined in sibling files).
    /// Used only as a fallback so the undefined-identifier check does not flag a name that
    /// resolves in another file of the same program. Not added to the scope, so it never
    /// triggers duplicate-declaration diagnostics.
    /// </summary>
    private readonly IReadOnlySet<string> _programNames;

    /// <summary>
    /// When true, a bare identifier used in a value position that resolves to nothing is
    /// reported as an error (it would fatal at runtime with "Undefined variable"). Verify sets
    /// this; the run path leaves it false because the evaluator reports the same error at
    /// runtime with a source snippet.
    /// </summary>
    private readonly bool _reportUndefinedIdentifiers;

    public Binder(string? filePath = null, IReadOnlyList<Symbol>? externalSymbols = null,
        IReadOnlySet<string>? programNames = null, bool reportUndefinedIdentifiers = false)
    {
        _filePath = filePath;
        _externalSymbols = externalSymbols ?? [];
        _programNames = programNames ?? new HashSet<string>(StringComparer.Ordinal);
        _reportUndefinedIdentifiers = reportUndefinedIdentifiers;
    }

    /// <summary>
    /// Names that are always implicitly available in a value position even without a scope
    /// declaration (e.g. the per-item variable in filters, per-item transforms, and foreach).
    /// </summary>
    private static bool IsImplicitlyAvailable(string name) =>
        name is "item";

    /// <summary>
    /// Bind a parsed module, producing a BindingResult with resolved symbols and diagnostics.
    /// </summary>
    public BindingResult Bind(ModuleNode module)
    {
        var globalScope = new Scope(label: "global");
        _result = new BindingResult(module, globalScope);
        _currentScope = globalScope;

        // Pre-populate with external/intrinsic symbols
        foreach (var ext in _externalSymbols)
            globalScope.Declare(ext);

        // Pass 1: register all top-level declarations
        foreach (var decl in module.Declarations)
            RegisterDeclaration(decl);

        // Pass 2: bind bodies (resolve identifiers within function/command bodies)
        foreach (var decl in module.Declarations)
            BindDeclarationBody(decl);

        // Pass 3: validate type references, arity, and unresolved symbols
        foreach (var decl in module.Declarations)
            ValidateDeclaration(decl);

        return _result;
    }

    // ========================================================================
    // Pass 1: Declaration Registration
    // ========================================================================

    private void RegisterDeclaration(Declaration decl)
    {
        switch (decl)
        {
            case TypeDecl td:
                RegisterType(td);
                break;
            case EnumDecl ed:
                RegisterEnum(ed);
                break;
            case FlagsDecl fd:
                RegisterFlags(fd);
                break;
            case FunctionDecl funcDecl:
                RegisterFunction(funcDecl);
                break;
            case LetDecl ld:
                RegisterLet(ld);
                break;
            case CommandDecl cd:
                RegisterCommand(cd);
                break;
            case ImportDecl:
                // Imports are handled separately (module resolution)
                break;
        }
    }

    private void RegisterType(TypeDecl decl)
    {
        var properties = decl.Properties.Select(p =>
            new PropertySymbol(p.Name, p.Type, p.IsOptional)).ToList();

        var symbol = new TypeSymbol(decl.Name, decl.BaseType, properties)
        {
            IsExported = decl.IsExported,
            DeclarationLine = decl.Line
        };

        if (!_currentScope.Declare(symbol))
        {
            // Allow a trait-conformance overload: `type X : Trait = { ... }` declared alongside
            // the base `type X = { ... }` (e.g. a TextFilePosition conformance used for violations).
            // The conformance form carries a BaseType; the runtime merges it onto the existing
            // type, so the binder must not flag it as a duplicate declaration.
            var existing = _currentScope.ResolveLocal(decl.Name);
            if (decl.BaseType is not null && existing is TypeSymbol)
            {
                // trait-conformance overload — OK
            }
            else if (IsExternalStub(existing))
            {
                // imported stub — a package's own source verified alongside a self-importing
                // sample (issue #51); not a real duplicate.
            }
            else
            {
                ReportDuplicate(decl.Name, decl.Line);
            }
        }

        _result.RecordResolution(decl, symbol);
    }

    private void RegisterEnum(EnumDecl decl)
    {
        var members = decl.Members.Select(m =>
            new EnumMemberSymbol(m, decl.Name)).ToList();

        var symbol = new EnumSymbol(decl.Name, decl.MemberType, members)
        {
            IsExported = decl.IsExported,
            DeclarationLine = decl.Line
        };

        if (!_currentScope.Declare(symbol))
        {
            if (!IsExternalStub(_currentScope.ResolveLocal(decl.Name)))
                ReportDuplicate(decl.Name, decl.Line);
        }

        // Enum members are injected into module scope (Cop convention)
        foreach (var member in members)
            _currentScope.Declare(member);

        _result.RecordResolution(decl, symbol);
    }

    private void RegisterFlags(FlagsDecl decl)
    {
        // Flags are treated as enums with bitwise semantics
        var members = decl.Members.Select(m =>
            new EnumMemberSymbol(m, decl.Name)).ToList();

        var symbol = new EnumSymbol(decl.Name, null, members)
        {
            IsExported = decl.IsExported,
            DeclarationLine = decl.Line
        };

        if (!_currentScope.Declare(symbol))
        {
            if (!IsExternalStub(_currentScope.ResolveLocal(decl.Name)))
                ReportDuplicate(decl.Name, decl.Line);
        }

        foreach (var member in members)
            _currentScope.Declare(member);

        _result.RecordResolution(decl, symbol);
    }

    private void RegisterFunction(FunctionDecl decl)
    {
        var parameters = decl.Params.Select((p, i) =>
            new ParameterSymbol(p.Name, p.Type, i)).ToList();

        var callableKind = decl.Body is BlockBody
            ? CallableKind.Command
            : decl.ReturnType?.Name == "bool" || decl.Guard is not null
                ? CallableKind.Predicate
                : CallableKind.Function;

        var symbol = new FunctionSymbol(decl.Name, callableKind, parameters, decl.ReturnType)
        {
            NarrowingType = decl.Guard is not null ? decl.ReturnType : null,
            Declaration = decl,
            IsExported = decl.IsExported,
            DeclarationLine = decl.Line
        };

        if (!_currentScope.Declare(symbol))
        {
            var existing = _currentScope.ResolveLocal(decl.Name);
            if (!IsAllowedFunctionOverload(existing, symbol, decl, callableKind))
                ReportDuplicate(decl.Name, decl.Line);
        }

        _result.RecordResolution(decl, symbol);
    }

    private static bool IsAllowedFunctionOverload(
        Symbol? existing,
        FunctionSymbol candidate,
        FunctionDecl candidateDecl,
        CallableKind candidateKind)
    {
        if (existing is not FunctionSymbol existingFn)
            return false;

        // A builtin/intrinsic stub injected by the verify harness (CallableKind.External, carrying
        // no real source declaration) is authoritatively replaced by the package's own in-source
        // declaration of that intrinsic — that is not a duplicate. A signature-bearing imported
        // callable (Function/Predicate) is still subject to the overload rules below.
        if (existingFn.CallableKind == CallableKind.External)
            return true;

        // A self-import injects a signature-less stub (no AST declaration, empty parameter list)
        // for each of the package's own exports. The arity-difference rule below already tolerates
        // a real local declaration that takes parameters (its arity differs from the 0-arity stub),
        // but a 0-parameter export such as `parse()` collides exactly with its stub. Allow that —
        // it is the package re-declaring itself, not a true duplicate (issue #51). A genuine attempt
        // to duplicate an imported callable that carries a real signature is still handled by the
        // signature-aware rules below.
        if (IsImportedCallableWithoutDeclaration(existingFn)
            && existingFn.Parameters.Count == 0 && candidateDecl.Params.Count == 0)
            return true;

        // Overloading rules:
        // 1. Predicates: allowed when their parameter signatures differ. This includes
        //    narrowing predicates (`predicate asX(T) : XType => ...`), which are classified
        //    as functions but still use predicate dispatch semantics.
        // 2. Imported package exports may be represented as generic Function symbols with
        //    no AST declaration; a local predicate with a distinct signature can still
        //    overload them, but an identical signature remains a duplicate.
        // 3. Functions: allowed if different arity (parameter count).
        // 4. Commands: never allowed.
        var candidateIsPredicate = candidateDecl.IsPredicate || candidateKind == CallableKind.Predicate;
        var existingIsPredicate = existingFn.CallableKind == CallableKind.Predicate
            || existingFn.Declaration?.IsPredicate == true;

        if (candidateIsPredicate && (existingIsPredicate || IsImportedCallableWithoutDeclaration(existingFn)))
            return !HaveSameParameterSignature(existingFn.Parameters, candidate.Parameters);

        return candidateKind == CallableKind.Function
            && existingFn.CallableKind == CallableKind.Function
            && !HaveSameParameterSignature(existingFn.Parameters, candidate.Parameters);
    }

    private static bool IsImportedCallableWithoutDeclaration(FunctionSymbol symbol) =>
        symbol.Declaration is null
        && (symbol.CallableKind == CallableKind.Function || symbol.CallableKind == CallableKind.External);

    /// <summary>
    /// True when a colliding symbol is an external stub injected by the import/verify harness
    /// (it carries no real source line) rather than a genuine in-source declaration. This happens
    /// when a package's own source is verified alongside a sample that imports that same package:
    /// the package's real types/enums/flags coincide with the injected import stub. That is not a
    /// duplicate declaration (issue #51).
    /// </summary>
    private static bool IsExternalStub(Symbol? existing) =>
        existing is not null && existing.DeclarationLine == 0;

    private static bool HaveSameParameterSignature(
        IReadOnlyList<ParameterSymbol> left,
        IReadOnlyList<ParameterSymbol> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!HaveSameType(left[i].DeclaredType, right[i].DeclaredType))
                return false;
        }

        return true;
    }

    private static bool HaveSameType(TypeRef? left, TypeRef? right)
    {
        if (left is null || right is null)
            return left is null && right is null;

        return left.Name == right.Name
            && left.IsCollection == right.IsCollection
            && left.Constraint == right.Constraint;
    }

    private void RegisterLet(LetDecl decl)
    {
        var symbol = new VariableSymbol(decl.Name, decl.TypeAnnotation, isReadOnly: true)
        {
            IsExported = decl.IsExported,
            DeclarationLine = decl.Line
        };

        if (!_currentScope.Declare(symbol))
        {
            // Allow let to shadow an imported function (common pattern: let codebase = codebase(...))
            var existing = _currentScope.ResolveLocal(decl.Name);
            if (existing is FunctionSymbol)
                _currentScope.DeclareOrReplace(symbol);
            else
                ReportDuplicate(decl.Name, decl.Line);
        }

        _result.RecordResolution(decl, symbol);
    }

    private void RegisterCommand(CommandDecl decl)
    {
        var parameters = (decl.Parameters ?? []).Select((p, i) =>
            new ParameterSymbol(p, null, i)).ToList();

        var symbol = new FunctionSymbol(decl.Name, CallableKind.Command, parameters)
        {
            IsExported = decl.IsExported,
            DeclarationLine = decl.Line
        };

        if (!_currentScope.Declare(symbol))
            ReportDuplicate(decl.Name, decl.Line);

        _result.RecordResolution(decl, symbol);
    }

    // ========================================================================
    // Pass 2: Body Binding (name resolution within expressions)
    // ========================================================================

    private void BindDeclarationBody(Declaration decl)
    {
        switch (decl)
        {
            case FunctionDecl funcDecl:
                BindFunctionBody(funcDecl);
                break;
            case CommandDecl cmdDecl:
                BindCommandBody(cmdDecl);
                break;
            case LetDecl letDecl:
                BindExpression(letDecl.Value);
                break;
        }
    }

    private void BindFunctionBody(FunctionDecl decl)
    {
        var funcScope = _currentScope.CreateChild($"function:{decl.Name}");
        _result.RecordScope(decl, funcScope);

        // Add parameters to function scope
        foreach (var param in decl.Params)
        {
            var paramSymbol = new ParameterSymbol(param.Name, param.Type, 0);
            funcScope.Declare(paramSymbol);
        }

        var previousScope = _currentScope;
        _currentScope = funcScope;

        // Bind guard if present
        if (decl.Guard is not null)
            BindExpression(decl.Guard);

        // Bind body
        switch (decl.Body)
        {
            case ExpressionBody eb:
                BindExpression(eb.Expr);
                break;
            case MappingBody mb:
                foreach (var mapping in mb.Mappings)
                    BindExpression(mapping.Value);
                break;
            case BlockBody bb:
                // Commands (and brace-bodied functions) desugar to a FunctionDecl with a
                // BlockBody. Without binding its statements, Pass 2 never resolved identifiers in
                // a command body, so an undefined reference such as `command main = CHECK(missing)`
                // was silently ignored by verify (Pass 3 validates the same body for arity).
                foreach (var stmt in bb.Statements)
                    BindStatement(stmt);
                break;
        }

        _currentScope = previousScope;
    }

    private void BindCommandBody(CommandDecl decl)
    {
        var cmdScope = _currentScope.CreateChild($"command:{decl.Name}");
        _result.RecordScope(decl, cmdScope);

        // Add command parameters to scope
        if (decl.Parameters is not null)
        {
            foreach (var param in decl.Parameters)
            {
                var paramSymbol = new ParameterSymbol(param, null, 0);
                cmdScope.Declare(paramSymbol);
            }
        }

        var previousScope = _currentScope;
        _currentScope = cmdScope;

        foreach (var stmt in decl.Body)
            BindStatement(stmt);

        _currentScope = previousScope;
    }

    // ========================================================================
    // Statement Binding
    // ========================================================================

    private void BindStatement(Statement stmt)
    {
        switch (stmt)
        {
            case LetStatement ls:
                BindExpression(ls.Value);
                var varSymbol = new VariableSymbol(ls.Name, ls.TypeAnnotation, isReadOnly: true)
                {
                    DeclarationLine = ls.Line
                };
                _currentScope.Declare(varSymbol);
                _result.RecordResolution(ls, varSymbol);
                break;

            case ForEachStatement fs:
                BindExpression(fs.Collection);
                var loopScope = _currentScope.CreateChild("foreach");
                var iterVar = new VariableSymbol(fs.Variable, null, isReadOnly: true)
                {
                    DeclarationLine = fs.Line
                };
                loopScope.Declare(iterVar);
                _result.RecordResolution(fs, iterVar);

                var prev = _currentScope;
                _currentScope = loopScope;
                foreach (var s in fs.Body)
                    BindStatement(s);
                _currentScope = prev;
                break;

            case ExpressionStatement es:
                BindExpression(es.Expr);
                break;

            case PipelineStatement ps:
                BindExpression(ps.Source);
                foreach (var stage in ps.Stages)
                    BindExpression(stage.Expr);
                break;
        }
    }

    // ========================================================================
    // Expression Binding
    // ========================================================================

    private void BindExpression(Expression expr)
    {
        switch (expr)
        {
            case IdentifierExpr id:
                var symbol = _currentScope.Resolve(id.Name);
                if (symbol is not null)
                {
                    _result.RecordResolution(id, symbol);
                }
                else if (_reportUndefinedIdentifiers
                    && !IsImplicitlyAvailable(id.Name)
                    && !_programNames.Contains(id.Name))
                {
                    // A bare identifier in a value position that resolves to nothing anywhere in
                    // the program is a real error — it fatals at runtime with "Undefined variable".
                    // Dynamic positions (call callees, member-access roots, filter predicates) are
                    // routed through BindCalleeOrDynamic below and never reach here, so short
                    // predicate names and provider/module roots are not flagged.
                    _result.ReportDiagnostic(
                        DiagnosticSeverity.Error,
                        $"Undefined variable '{id.Name}'",
                        id.Line,
                        _filePath);
                }
                break;

            case LiteralExpr:
                // No binding needed for literals
                break;

            case BinaryExpr be:
                BindExpression(be.Left);
                BindExpression(be.Right);
                break;

            case UnaryExpr ue:
                BindExpression(ue.Operand);
                break;

            case CallExpr ce:
                // The callee (an intrinsic like CHECK/print, a user function, or a short
                // predicate used as a function) is resolved dynamically — don't flag it.
                BindCalleeOrDynamic(ce.Callee);
                foreach (var arg in ce.Args)
                    BindExpression(arg);
                break;

            case MemberExpr me:
                // The root of a member chain (e.g. `codebase` in `codebase.Types`, a module
                // like `csharp`, `item`, or a provider root) is resolved dynamically; member
                // names themselves are resolved dynamically (provider fields, type properties).
                BindCalleeOrDynamic(me.Object);
                break;

            case IndexExpr ie:
                BindExpression(ie.Object);
                BindExpression(ie.Index);
                break;

            case LambdaExpr le:
                var lambdaScope = _currentScope.CreateChild("lambda");
                foreach (var p in le.Params)
                {
                    var ps = new ParameterSymbol(p.Name, p.Type, 0);
                    lambdaScope.Declare(ps);
                }
                var outer = _currentScope;
                _currentScope = lambdaScope;
                BindExpression(le.Body);
                _currentScope = outer;
                break;

            case ConditionalExpr cond:
                BindExpression(cond.Condition);
                BindExpression(cond.Then);
                BindExpression(cond.Else);
                break;

            case MatchExpr match:
                BindExpression(match.Discriminant);
                foreach (var arm in match.Arms)
                    BindExpression(arm.Body);
                break;

            case ListExpr list:
                foreach (var elem in list.Elements)
                    BindExpression(elem);
                break;

            case ObjectExpr obj:
                foreach (var field in obj.Fields)
                    BindExpression(field.Value);
                break;

            case InterpolatedStringExpr interp:
                foreach (var part in interp.Parts)
                {
                    if (part is ExpressionPart ep)
                        BindExpression(ep.Expr);
                }
                break;

            case FilterExpr filter:
                BindExpression(filter.Collection);
                // A bare predicate name (`:isPublic`) is resolved dynamically by the evaluator,
                // which reports unknown predicates itself — don't flag it as an undefined value.
                BindCalleeOrDynamic(filter.Predicate);
                break;

            case ForEachExpr fe:
                BindExpression(fe.Loop.Collection);
                var feScope = _currentScope.CreateChild("foreach");
                feScope.Declare(new VariableSymbol(fe.Loop.Variable, null, isReadOnly: true)
                {
                    DeclarationLine = fe.Loop.Line
                });
                var feOuter = _currentScope;
                _currentScope = feScope;
                foreach (var s in fe.Loop.Body)
                    BindStatement(s);
                _currentScope = feOuter;
                break;
        }
    }

    /// <summary>
    /// Binds an expression that appears in a "dynamic" position — a call callee, a member-access
    /// root, or a filter predicate — where a bare identifier may legitimately be resolved at
    /// runtime (intrinsics, provider/module roots, short predicate names). Such a bare identifier
    /// is recorded if known but never reported as undefined. Non-identifier expressions are bound
    /// normally so their inner value references are still validated.
    /// </summary>
    private void BindCalleeOrDynamic(Expression expr)
    {
        if (expr is IdentifierExpr id)
        {
            var symbol = _currentScope.Resolve(id.Name);
            if (symbol is not null)
                _result.RecordResolution(id, symbol);
        }
        else
        {
            BindExpression(expr);
        }
    }

    // ========================================================================
    // Diagnostics
    // ========================================================================

    private void ReportDuplicate(string name, int line)
    {
        _result.ReportDiagnostic(
            DiagnosticSeverity.Error,
            $"Duplicate declaration '{name}'",
            line,
            _filePath);
    }

    // ========================================================================
    // Pass 3: Validation (type references, arity, unresolved symbols)
    // ========================================================================

    private void ValidateDeclaration(Declaration decl)
    {
        switch (decl)
        {
            case TypeDecl td:
                ValidateTypeDecl(td);
                break;
            case FunctionDecl fd:
                ValidateFunctionDecl(fd);
                break;
            case LetDecl ld:
                ValidateTypeRef(ld.TypeAnnotation);
                ValidateExpression(ld.Value);
                break;
            case CommandDecl cd:
                foreach (var stmt in cd.Body)
                    ValidateStatement(stmt);
                break;
        }
    }

    private void ValidateTypeDecl(TypeDecl decl)
    {
        // Validate base type
        if (decl.BaseType is not null)
            ValidateTypeName(decl.BaseType, decl.Line);

        // Detect circular inheritance (e.g. `type A : B` with `type B : A`, or `type A : A`).
        DetectInheritanceCycle(decl);

        // Validate property types
        foreach (var prop in decl.Properties)
            ValidateTypeRef(prop.Type);
    }

    private void DetectInheritanceCycle(TypeDecl decl)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { decl.Name };
        var current = decl.BaseType;
        while (current is not null)
        {
            if (!seen.Add(current))
            {
                _result.ReportDiagnostic(
                    DiagnosticSeverity.Error,
                    $"Circular type inheritance: '{decl.Name}' eventually inherits from itself (via '{current}')",
                    decl.Line,
                    _filePath);
                return;
            }
            // Walk to the next base in the chain. An unresolved base (already reported by
            // ValidateTypeName) simply ends the walk.
            current = (_currentScope.Resolve(current) as TypeSymbol)?.BaseTypeName;
        }
    }

    private void ValidateFunctionDecl(FunctionDecl decl)
    {
        // Validate parameter types
        foreach (var param in decl.Params)
            ValidateTypeRef(param.Type);

        // Validate return type
        ValidateTypeRef(decl.ReturnType);

        // Set parameter context for enum-vs-string comparison checks
        var previousParams = _currentFunctionParams;
        _currentFunctionParams = decl.Params;

        // Validate body expressions
        switch (decl.Body)
        {
            case ExpressionBody eb:
                ValidateExpression(eb.Expr);
                break;
            case MappingBody mb:
                foreach (var mapping in mb.Mappings)
                    ValidateExpression(mapping.Value);
                break;
            case BlockBody bb:
                // Commands and brace-bodied functions carry a BlockBody. Without validating it,
                // Pass 3 checks (arity, type references, enum-vs-string comparisons) never ran on a
                // command body — e.g. `command main = print(f(1, 2, 3))` silently passed verify.
                foreach (var stmt in bb.Statements)
                    ValidateStatement(stmt);
                break;
        }

        _currentFunctionParams = previousParams;
    }

    private void ValidateStatement(Statement stmt)
    {
        switch (stmt)
        {
            case LetStatement ls:
                ValidateTypeRef(ls.TypeAnnotation);
                ValidateExpression(ls.Value);
                break;
            case ForEachStatement fs:
                ValidateExpression(fs.Collection);
                foreach (var s in fs.Body)
                    ValidateStatement(s);
                break;
            case ExpressionStatement es:
                ValidateExpression(es.Expr);
                break;
            case PipelineStatement ps:
                ValidateExpression(ps.Source);
                foreach (var stage in ps.Stages)
                    ValidateExpression(stage.Expr);
                break;
        }
    }

    private void ValidateExpression(Expression? expr)
    {
        if (expr is null) return;

        switch (expr)
        {
            case CallExpr ce:
                ValidateCallExpr(ce);
                break;
            case BinaryExpr be:
                ValidateExpression(be.Left);
                ValidateExpression(be.Right);
                ValidateEnumComparison(be);
                break;
            case UnaryExpr ue:
                ValidateExpression(ue.Operand);
                break;
            case MemberExpr me:
                ValidateExpression(me.Object);
                break;
            case IndexExpr ie:
                ValidateExpression(ie.Object);
                ValidateExpression(ie.Index);
                break;
            case LambdaExpr le:
                foreach (var p in le.Params)
                    ValidateTypeRef(p.Type);
                ValidateExpression(le.Body);
                break;
            case ConditionalExpr cond:
                ValidateExpression(cond.Condition);
                ValidateExpression(cond.Then);
                ValidateExpression(cond.Else);
                break;
            case MatchExpr match:
                ValidateExpression(match.Discriminant);
                foreach (var arm in match.Arms)
                    ValidateExpression(arm.Body);
                break;
            case ListExpr list:
                foreach (var elem in list.Elements)
                    ValidateExpression(elem);
                break;
            case ObjectExpr obj:
                foreach (var field in obj.Fields)
                    ValidateExpression(field.Value);
                break;
            case InterpolatedStringExpr interp:
                foreach (var part in interp.Parts)
                {
                    if (part is ExpressionPart ep)
                        ValidateExpression(ep.Expr);
                }
                break;
            case FilterExpr filter:
                ValidateExpression(filter.Collection);
                ValidateExpression(filter.Predicate);
                break;
            case ForEachExpr fe:
                ValidateExpression(fe.Loop.Collection);
                foreach (var s in fe.Loop.Body)
                    ValidateStatement(s);
                break;
        }
    }

    private void ValidateCallExpr(CallExpr ce)
    {
        // Validate argument expressions recursively
        ValidateExpression(ce.Callee);
        foreach (var arg in ce.Args)
            ValidateExpression(arg);

        // Check arity if callee resolves to a known function
        if (ce.Callee is IdentifierExpr id)
        {
            var symbol = _currentScope.Resolve(id.Name);
            if (symbol is FunctionSymbol func && func.Parameters.Count > 0)
            {
                // Only check arity when we have a clear mismatch.
                // Cop allows overloading (same name, different params), so only flag
                // when too many args are passed (too few may be partial application).
                if (ce.Args.Count > func.Parameters.Count)
                {
                    _result.ReportDiagnostic(
                        DiagnosticSeverity.Error,
                        $"Function '{id.Name}' expects {func.Parameters.Count} argument(s) but got {ce.Args.Count}",
                        ce.Line,
                        _filePath);
                }
            }
            else if (symbol is EnumSymbol && ce.Args.Count != 1)
            {
                // Enum constructors accept exactly one string argument
                _result.ReportDiagnostic(
                    DiagnosticSeverity.Error,
                    $"Enum constructor '{id.Name}' expects exactly 1 argument, got {ce.Args.Count}",
                    ce.Line,
                    _filePath);
            }
        }
    }

    /// <summary>
    /// Validates a TypeRef resolves to a known type or enum in scope.
    /// </summary>
    private void ValidateTypeRef(TypeRef? typeRef)
    {
        if (typeRef is null) return;
        ValidateTypeName(typeRef.Name, typeRef.Line);
    }

    /// <summary>
    /// Validates that a type name resolves to a declared type or enum symbol in scope.
    /// </summary>
    private void ValidateTypeName(string name, int line)
    {
        // Single uppercase letters are generic type parameter placeholders (A-Z)
        if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z') return;

        // 'computed' is the synthetic type of a computed property (`name => expr`) used in
        // conformance/struct bodies; it has no declared type to resolve.
        if (name == "computed") return;

        // Function types like '(T) => bool' or '(A, T) => A' are always valid structurally
        if (name.Contains("=>")) return;

        // Core primitive types are always valid
        if (name is "string" or "int" or "float" or "bool" or "byte" or "bytes" or "object") return;

        var symbol = _currentScope.Resolve(name);
        if (symbol is TypeSymbol or EnumSymbol)
            return;

        // Named subset / collection reference patterns:
        // - Plural collection names (e.g., 'Types' → 'Type' is a known type)
        // - Numbered variants (e.g., 'Region2' → 'Region' is a known type)
        if (symbol is null && name.Length > 1 && char.IsUpper(name[0]))
        {
            // Try stripping trailing 's' (Types → Type, Statements → Statement...)
            if (name.EndsWith('s'))
            {
                var singular = name[..^1];
                if (_currentScope.Resolve(singular) is TypeSymbol)
                    return;
                // Handle 'ies' → 'y' (e.g., Entries → Entry) — not common but safe
            }
            // Try stripping trailing digits (Region2 → Region)
            var baseName = name.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
            if (baseName.Length < name.Length && _currentScope.Resolve(baseName) is TypeSymbol)
                return;
        }

        if (symbol is null)
        {
            _result.ReportDiagnostic(
                DiagnosticSeverity.Error,
                $"Unknown type '{name}'",
                line,
                _filePath);
        }
        else if (symbol is not TypeSymbol and not EnumSymbol)
        {
            _result.ReportDiagnostic(
                DiagnosticSeverity.Error,
                $"'{name}' is not a type (it is a {symbol.Kind})",
                line,
                _filePath);
        }
    }

    // ========================================================================
    // Enum comparison validation
    // ========================================================================

    /// <summary>
    /// Checks whether a binary == or != expression compares an enum-typed property
    /// to a raw string literal. If so, emits an error — the user should use an enum
    /// member (e.g., Class) or explicit cast (e.g., TypeKind('class')).
    /// </summary>
    private void ValidateEnumComparison(BinaryExpr be)
    {
        if (be.Op is not (BinaryOp.Equal or BinaryOp.NotEqual)) return;

        // Check both directions: member == literal, or literal == member
        TryReportEnumStringMismatch(be.Left, be.Right, be.Line);
        TryReportEnumStringMismatch(be.Right, be.Left, be.Line);
    }

    private void TryReportEnumStringMismatch(Expression possibleMember, Expression possibleLiteral, int line)
    {
        // The literal side must be a string literal
        if (possibleLiteral is not LiteralExpr { Value: string })
            return;

        // The other side must be a member access (e.g., Type.Kind)
        if (possibleMember is not MemberExpr me)
            return;

        // Try to resolve the property type of the member access
        var enumTypeName = TryResolvePropertyEnumType(me);
        if (enumTypeName is null)
            return;

        // We found an enum-typed property being compared to a string literal
        _result.ReportDiagnostic(
            DiagnosticSeverity.Error,
            $"Cannot compare '{me.Member}' ({enumTypeName}) to a string literal. Use an enum member or explicit cast: {enumTypeName}('value')",
            line,
            _filePath);
    }

    /// <summary>
    /// Given a member expression like Type.Kind, tries to resolve the declared property type.
    /// Returns the enum type name if the property is enum-typed, null otherwise.
    /// </summary>
    private string? TryResolvePropertyEnumType(MemberExpr me)
    {
        // Resolve the object's type name
        string? objectTypeName = null;

        if (me.Object is IdentifierExpr id)
        {
            // Check if it's a function parameter with a type annotation
            if (_currentFunctionParams is not null)
            {
                foreach (var param in _currentFunctionParams)
                {
                    if (param.Name == id.Name && param.Type is not null)
                    {
                        objectTypeName = param.Type.Name;
                        break;
                    }
                }
            }

            // If not a parameter, check scope for variable with type annotation
            if (objectTypeName is null)
            {
                var symbol = _currentScope.Resolve(id.Name);
                if (symbol is VariableSymbol vs && vs.DeclaredType is not null)
                    objectTypeName = vs.DeclaredType.Name;
                else if (symbol is ParameterSymbol ps && ps.DeclaredType is not null)
                    objectTypeName = ps.DeclaredType.Name;
            }
        }

        if (objectTypeName is null) return null;

        // Look up the type symbol to find the property
        var typeSymbol = _currentScope.Resolve(objectTypeName);
        if (typeSymbol is not TypeSymbol ts) return null;

        // Find the property by name
        foreach (var prop in ts.Properties)
        {
            if (prop.Name == me.Member && prop.DeclaredType is not null)
            {
                // Check if the property type is an enum
                var propTypeSymbol = _currentScope.Resolve(prop.DeclaredType.Name);
                if (propTypeSymbol is EnumSymbol)
                    return prop.DeclaredType.Name;
            }
        }

        return null;
    }
}
