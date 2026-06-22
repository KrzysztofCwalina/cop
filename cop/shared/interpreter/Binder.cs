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

    public Binder(string? filePath = null, IReadOnlyList<Symbol>? externalSymbols = null)
    {
        _filePath = filePath;
        _externalSymbols = externalSymbols ?? [];
    }

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
            ReportDuplicate(decl.Name, decl.Line);

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
            ReportDuplicate(decl.Name, decl.Line);

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
            ReportDuplicate(decl.Name, decl.Line);

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
            // Overloading rules:
            // 1. Predicates: always allowed (dispatched by input type/guard)
            // 2. Functions: allowed if different arity (parameter count)
            // 3. Commands: never allowed
            var existing = _currentScope.ResolveLocal(decl.Name);
            if (existing is FunctionSymbol existingFn
                && existingFn.CallableKind == CallableKind.Predicate
                && callableKind == CallableKind.Predicate)
            {
                // Predicate overloading — always OK
            }
            else if (decl.IsPredicate
                && existing is FunctionSymbol existingPred
                && existingPred.Declaration?.IsPredicate == true)
            {
                // Narrowing-predicate overloading by parameter type. A narrowing predicate
                // (`predicate asX(T) : XType => ...`) has a non-bool return type and no
                // parameter guard, so it is classified as a Function above — but the
                // `predicate` keyword means it dispatches by parameter type like any other
                // predicate, so overloading it across parameter types is allowed.
            }
            else if (existing is FunctionSymbol existingFunc
                && callableKind == CallableKind.Function
                && existingFunc.CallableKind == CallableKind.Function
                && existingFunc.Parameters.Count != symbol.Parameters.Count)
            {
                // Function overloading by arity — OK
            }
            else
            {
                ReportDuplicate(decl.Name, decl.Line);
            }
        }

        _result.RecordResolution(decl, symbol);
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
                    _result.RecordResolution(id, symbol);
                // Note: unresolved identifiers are NOT errors in Cop because
                // they may be runtime-provided (dynamic provider fields, 
                // external module exports, or short predicate names).
                // The evaluator handles missing bindings at runtime.
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
                BindExpression(ce.Callee);
                foreach (var arg in ce.Args)
                    BindExpression(arg);
                break;

            case MemberExpr me:
                BindExpression(me.Object);
                // Member names are resolved dynamically (provider fields, type properties)
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
                BindExpression(filter.Predicate);
                break;
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

        // Validate property types
        foreach (var prop in decl.Properties)
            ValidateTypeRef(prop.Type);
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
