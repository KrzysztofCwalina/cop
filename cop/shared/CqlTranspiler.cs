using System.Text;

namespace Cop.Lang;

/// <summary>
/// Transpiles cop ScriptFile ASTs into CodeQL .ql query files.
/// Operates on the parsed AST only — no provider data is loaded or evaluated.
/// </summary>
public class CqlTranspiler
{
    // Code provider collections that have CodeQL equivalents
    private static readonly HashSet<string> SupportedCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Types", "Statements", "Calls", "Methods",
        "Code.Types", "Code.Statements", "Code.Calls", "Code.Methods"
    };

    // Collections from the code provider that we do NOT support transpiling
    private static readonly HashSet<string> UnsupportedCodeCollections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Lines", "Files", "Api", "Members", "Regions", "Projects",
        "Code.Lines", "Code.Files", "Code.Api", "Code.Members", "Code.Regions", "Code.Projects"
    };

    // Cop Statement.Kind → CodeQL class name (C# pack)
    private static readonly Dictionary<string, string> StatementKindToCqlClass = new(StringComparer.OrdinalIgnoreCase)
    {
        ["call"] = "MethodAccess",
        ["declaration"] = "LocalVariableDeclStmt",
        ["import"] = "UsingDirective",
        ["attribute"] = "Attribute",
        ["throw"] = "ThrowStmt",
        ["return"] = "ReturnStmt",
        ["using"] = "UsingStmt",
        ["foreach"] = "ForeachStmt",
        ["try"] = "TryStmt",
        ["catch"] = "CatchClause",
        ["if"] = "IfStmt",
        ["while"] = "WhileStmt",
        ["for"] = "ForStmt",
        ["switch"] = "SwitchStmt",
        ["await"] = "AwaitExpr",
    };

    // Cop Modifier flag names → CodeQL predicate names
    private static readonly Dictionary<string, string> ModifierToCqlPredicate = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Public"] = "isPublic",
        ["Private"] = "isPrivate",
        ["Protected"] = "isProtected",
        ["Internal"] = "isInternal",
        ["Static"] = "isStatic",
        ["Sealed"] = "isSealed",
        ["Abstract"] = "isAbstract",
        ["Virtual"] = "isVirtual",
        ["Async"] = "isAsync",
        ["Override"] = "isOverride",
        ["Readonly"] = "isReadonly",
        ["Const"] = "isConst",
    };

    private readonly ScriptFile _mainFile;
    private readonly List<ScriptFile> _importedFiles;
    private readonly Dictionary<string, List<PredicateDefinition>> _allPredicates;
    private readonly List<string> _errors = new();

    public CqlTranspiler(ScriptFile mainFile, List<ScriptFile> importedFiles)
    {
        _mainFile = mainFile;
        _importedFiles = importedFiles;

        // Build lookup of all predicates (local + imported)
        _allPredicates = new Dictionary<string, List<PredicateDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in importedFiles.Append(mainFile))
        {
            foreach (var pred in file.Predicates)
            {
                if (!_allPredicates.TryGetValue(pred.Name, out var list))
                {
                    list = new List<PredicateDefinition>();
                    _allPredicates[pred.Name] = list;
                }
                list.Add(pred);
            }
        }
    }

    /// <summary>
    /// Transpiles the main .cop file into CodeQL .ql files.
    /// Returns a list of (fileName, qlContent) pairs, or populates errors.
    /// </summary>
    public CqlTranspileResult Transpile()
    {
        var outputs = new List<CqlQueryFile>();
        _errors.Clear();

        // Phase 1: Validate — scan all collection references for unsupported providers
        ValidateCollectionReferences();
        if (_errors.Count > 0)
            return new CqlTranspileResult(outputs, _errors.ToList());

        // Phase 2: Transpile each exported let binding that looks like a check
        foreach (var let in _mainFile.LetDeclarations)
        {
            if (!let.IsExported) continue;
            if (let.IsValueBinding) continue;

            var query = TryTranspileLet(let);
            if (query is not null)
                outputs.Add(query);
        }

        // Phase 3: Transpile exported commands that reference collection-based checks
        foreach (var cmd in _mainFile.Commands)
        {
            if (!cmd.IsExported) continue;
            if (cmd.IsCommand) continue; // skip RUN commands
            if (string.IsNullOrEmpty(cmd.Collection)) continue;

            var query = TryTranspileCommand(cmd);
            if (query is not null)
                outputs.Add(query);
        }

        // Phase 4: Transpile commands that delegate to an action like CHECK(letRef)
        // Pattern: command CHECK-TODOS = CHECK(todos) where CHECK is an imported command and todos is a local let
        foreach (var cmd in _mainFile.Commands)
        {
            if (!cmd.IsCommand) continue;
            if (string.IsNullOrEmpty(cmd.ActionName)) continue;
            if (string.IsNullOrEmpty(cmd.Collection)) continue;

            // See if the collection name references a local let binding
            var let = _mainFile.LetDeclarations.FirstOrDefault(l =>
                string.Equals(l.Name, cmd.Collection, StringComparison.OrdinalIgnoreCase));
            if (let is null || let.IsValueBinding) continue;

            var query = TryTranspileLet(let, overrideName: cmd.Name, overrideDoc: cmd.DocComment ?? let.DocComment);
            if (query is not null)
                outputs.Add(query);
        }

        return new CqlTranspileResult(outputs, _errors.ToList());
    }

    private void ValidateCollectionReferences()
    {
        var allLets = _mainFile.LetDeclarations;
        foreach (var let in allLets)
        {
            if (let.IsValueBinding) continue;
            var collName = NormalizeCollectionName(let.BaseCollection);
            if (!IsCodeCollection(collName))
            {
                _errors.Add($"Error: Collection '{let.BaseCollection}' in let '{let.Name}' (line {let.Line}) cannot be transpiled to CodeQL. Only Code provider collections are supported.");
            }
        }

        foreach (var cmd in _mainFile.Commands)
        {
            if (cmd.IsCommand || string.IsNullOrEmpty(cmd.Collection)) continue;
            var collName = NormalizeCollectionName(cmd.Collection);
            if (!IsCodeCollection(collName))
            {
                _errors.Add($"Error: Collection '{cmd.Collection}' in check '{cmd.Name}' (line {cmd.Line}) cannot be transpiled to CodeQL. Only Code provider collections are supported.");
            }
        }
    }

    private static bool IsCodeCollection(string name)
    {
        return SupportedCollections.Contains(name) || UnsupportedCodeCollections.Contains(name);
    }

    private static string NormalizeCollectionName(string name)
    {
        // Strip namespace prefix: "Code.Types" → "Types", "csharp.Types" → "Types"
        var dot = name.LastIndexOf('.');
        if (dot >= 0)
            return name[(dot + 1)..];
        return name;
    }

    private CqlQueryFile? TryTranspileLet(LetDeclaration let, string? overrideName = null, string? overrideDoc = null)
    {
        var collName = NormalizeCollectionName(let.BaseCollection);

        if (UnsupportedCodeCollections.Contains(collName))
        {
            _errors.Add($"Error: Collection '{let.BaseCollection}' in let '{let.Name}' (line {let.Line}) has no CodeQL equivalent.");
            return null;
        }

        // Detect language filter and severity from the filter chain
        string? language = null;
        string? severity = null;
        string? message = null;
        var predicateFilters = new List<Expression>();

        foreach (var filter in let.Filters)
        {
            // Language filter: bare identifier "csharp", "python", "javascript"
            if (filter is IdentifierExpr langId && IsLanguageFilter(langId.Name))
            {
                language = langId.Name;
                continue;
            }

            // toError/toWarning/toInfo function → extract severity and message
            if (filter is CallExpr { Target: null } fc && fc.Name is "toError" or "toWarning" or "toInfo")
            {
                severity = fc.Name switch
                {
                    "toError" => "error",
                    "toWarning" => "warning",
                    "toInfo" => "recommendation",
                    _ => null
                };
                if (fc.Args.Count > 0 && fc.Args[0] is LiteralExpr msgLit && msgLit.Value is string msgStr)
                    message = msgStr;
                continue;
            }

            predicateFilters.Add(filter);
        }

        // Determine the CodeQL base type
        var (cqlType, cqlVar, extraWhere) = ResolveCollectionType(collName, predicateFilters);
        if (cqlType is null)
        {
            _errors.Add($"Error: Cannot determine CodeQL type for collection '{let.BaseCollection}' in let '{let.Name}' (line {let.Line}).");
            return null;
        }

        // Build where clauses from remaining predicate filters
        var whereClauses = new List<string>();
        if (extraWhere is not null)
            whereClauses.Add(extraWhere);

        foreach (var filter in predicateFilters)
        {
            var clause = TryTranspileFilter(filter, cqlVar, collName);
            if (clause is null)
            {
                _errors.Add($"Error: Cannot transpile filter in let '{let.Name}' (line {let.Line}): {QueryFingerprint.Serialize(filter)}");
                return null;
            }
            whereClauses.Add(clause);
        }

        // Build the .ql file
        var sb = new StringBuilder();
        var effectiveName = overrideName ?? let.Name;
        var queryName = SanitizeIdentifier(effectiveName);
        var docComment = overrideDoc ?? let.DocComment ?? effectiveName;

        // Metadata
        sb.AppendLine("/**");
        sb.AppendLine($" * @name {docComment}");
        sb.AppendLine($" * @description {docComment}");
        sb.AppendLine($" * @kind problem");
        if (severity is not null)
            sb.AppendLine($" * @problem.severity {severity}");
        sb.AppendLine($" * @id cop/{queryName}");
        sb.AppendLine(" */");
        sb.AppendLine();

        // Import
        var langPack = language ?? "csharp"; // default to C# if no language filter
        sb.AppendLine($"import {langPack}");
        sb.AppendLine();

        // From
        sb.AppendLine($"from {cqlType} {cqlVar}");

        // Where
        if (whereClauses.Count > 0)
        {
            sb.Append("where ");
            sb.AppendLine(string.Join("\n  and ", whereClauses));
        }

        // Select
        var selectMsg = message is not null
            ? TranspileMessageTemplate(message, cqlVar)
            : $"\"{docComment}\"";
        sb.AppendLine($"select {cqlVar}, {selectMsg}");

        return new CqlQueryFile($"{queryName}.ql", sb.ToString());
    }

    private CqlQueryFile? TryTranspileCommand(CommandBlock cmd)
    {
        // Commands with collections are similar to let bindings
        var collName = NormalizeCollectionName(cmd.Collection!);

        if (UnsupportedCodeCollections.Contains(collName))
        {
            _errors.Add($"Error: Collection '{cmd.Collection}' in check '{cmd.Name}' (line {cmd.Line}) has no CodeQL equivalent.");
            return null;
        }

        string? language = null;
        var predicateFilters = new List<Expression>();

        foreach (var filter in cmd.Filters)
        {
            if (filter is IdentifierExpr langId && IsLanguageFilter(langId.Name))
            {
                language = langId.Name;
                continue;
            }
            predicateFilters.Add(filter);
        }

        var (cqlType, cqlVar, extraWhere) = ResolveCollectionType(collName, predicateFilters);
        if (cqlType is null)
        {
            _errors.Add($"Error: Cannot determine CodeQL type for collection '{cmd.Collection}' in check '{cmd.Name}' (line {cmd.Line}).");
            return null;
        }

        var whereClauses = new List<string>();
        if (extraWhere is not null)
            whereClauses.Add(extraWhere);

        foreach (var filter in predicateFilters)
        {
            var clause = TryTranspileFilter(filter, cqlVar, collName);
            if (clause is null)
            {
                _errors.Add($"Error: Cannot transpile filter in check '{cmd.Name}' (line {cmd.Line}): {QueryFingerprint.Serialize(filter)}");
                return null;
            }
            whereClauses.Add(clause);
        }

        var sb = new StringBuilder();
        var queryName = SanitizeIdentifier(cmd.Name);

        sb.AppendLine("/**");
        sb.AppendLine($" * @name {cmd.DocComment ?? cmd.Name}");
        sb.AppendLine($" * @description {cmd.DocComment ?? cmd.Name}");
        sb.AppendLine($" * @kind problem");
        sb.AppendLine($" * @id cop/{queryName}");
        sb.AppendLine(" */");
        sb.AppendLine();
        sb.AppendLine($"import {language ?? "csharp"}");
        sb.AppendLine();
        sb.AppendLine($"from {cqlType} {cqlVar}");
        if (whereClauses.Count > 0)
        {
            sb.Append("where ");
            sb.AppendLine(string.Join("\n  and ", whereClauses));
        }

        var selectMsg = !string.IsNullOrEmpty(cmd.MessageTemplate)
            ? TranspileMessageTemplate(cmd.MessageTemplate, cqlVar)
            : $"\"{cmd.Name}\"";
        sb.AppendLine($"select {cqlVar}, {selectMsg}");

        return new CqlQueryFile($"{queryName}.ql", sb.ToString());
    }

    /// <summary>
    /// Resolves a cop collection + filters into a CodeQL type, variable name, and optional extra where clause.
    /// For Statements/Calls, inspects filters for Kind narrowing to determine the precise CodeQL type.
    /// </summary>
    private (string? CqlType, string CqlVar, string? ExtraWhere) ResolveCollectionType(
        string collName, List<Expression> filters)
    {
        switch (collName)
        {
            case "Types":
                // Look for Kind filter to narrow
                var kindValue = ExtractKindFilter(filters);
                if (kindValue is not null)
                {
                    return kindValue.ToLowerInvariant() switch
                    {
                        "class" => ("Class", "c", null),
                        "struct" => ("Struct", "s", null),
                        "interface" => ("Interface", "i", null),
                        "enum" => ("Enum", "e", null),
                        _ => ("RefType", "t", null)
                    };
                }
                // No Kind filter — use top-level RefType which covers classes and interfaces
                return ("RefType", "t", null);

            case "Statements" or "Calls":
                // Must inspect Kind filter to determine CodeQL type
                var stmtKind = ExtractKindFilter(filters);
                if (stmtKind is not null && StatementKindToCqlClass.TryGetValue(stmtKind, out var cqlClass))
                {
                    var varName = cqlClass[0].ToString().ToLowerInvariant();
                    return (cqlClass, varName, null);
                }
                if (collName == "Calls")
                    return ("MethodAccess", "ma", null);
                // Generic Statements without Kind narrowing — use Stmt
                return ("Stmt", "s", null);

            case "Methods":
                return ("Method", "m", null);

            default:
                return (null, "x", null);
        }
    }

    /// <summary>
    /// Extracts a Kind equality filter value from the filter list and removes it.
    /// Looks for patterns like: Kind == 'call', Statement.Kind == 'call', isCall,
    /// and also inspects the bodies of referenced user predicates.
    /// </summary>
    private string? ExtractKindFilter(List<Expression> filters)
    {
        for (int i = 0; i < filters.Count; i++)
        {
            var f = filters[i];

            // Binary: Kind == 'call' or Statement.Kind == 'call'
            if (f is BinaryExpr bin && bin.Operator == "==")
            {
                var kindProp = ExtractPropertyName(bin.Left, "Kind") ?? ExtractPropertyName(bin.Right, "Kind");
                var literal = (bin.Left as LiteralExpr)?.Value as string ?? (bin.Right as LiteralExpr)?.Value as string;
                if (kindProp is not null && literal is not null)
                {
                    filters.RemoveAt(i);
                    return literal;
                }
            }

            // Predicate call: Kind:equals('call')
            if (f is CallExpr pc && pc.Name is "equals" or "eq"
                && pc.Target is IdentifierExpr propId && propId.Name == "Kind"
                && pc.Args.Count == 1 && pc.Args[0] is LiteralExpr lit && lit.Value is string sv)
            {
                filters.RemoveAt(i);
                return sv;
            }

            // Narrowing predicate: isCall, isDeclaration
            if (f is IdentifierExpr id)
            {
                var narrowKind = id.Name switch
                {
                    "isCall" => "call",
                    "isDeclaration" => "declaration",
                    _ => null
                };
                if (narrowKind is not null)
                {
                    filters.RemoveAt(i);
                    return narrowKind;
                }

                // Check if it's a user predicate whose body contains a Kind check
                if (_allPredicates.TryGetValue(id.Name, out var predDefs))
                {
                    var kindFromPred = ExtractKindFromPredicateBody(predDefs[0].Body);
                    if (kindFromPred is not null)
                    {
                        // Don't remove the filter — the predicate has other conditions too.
                        // The Kind part will be handled as instanceof in transpilation.
                        return kindFromPred;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Extracts a Kind value from a predicate body expression.
    /// Looks for Kind == 'value' or Statement.Kind == 'value' in AND chains.
    /// </summary>
    private static string? ExtractKindFromPredicateBody(Expression body)
    {
        // Direct: Kind == 'call' or Statement.Kind == 'call'
        if (body is BinaryExpr bin && bin.Operator == "==")
        {
            var kindProp = ExtractPropertyName(bin.Left, "Kind") ?? ExtractPropertyName(bin.Right, "Kind");
            var literal = (bin.Left as LiteralExpr)?.Value as string ?? (bin.Right as LiteralExpr)?.Value as string;
            if (kindProp is not null && literal is not null)
                return literal;
        }

        // AND chain: Kind == 'call' && other conditions
        if (body is BinaryExpr andBin && andBin.Operator == "&&")
        {
            return ExtractKindFromPredicateBody(andBin.Left)
                ?? ExtractKindFromPredicateBody(andBin.Right);
        }

        return null;
    }

    private static string? ExtractPropertyName(Expression expr, string expectedProp)
    {
        if (expr is IdentifierExpr id && string.Equals(id.Name, expectedProp, StringComparison.OrdinalIgnoreCase))
            return id.Name;
        if (expr is MemberAccessExpr ma && string.Equals(ma.Member, expectedProp, StringComparison.OrdinalIgnoreCase))
            return ma.Member;
        return null;
    }

    /// <summary>
    /// Transpiles a single filter expression to a CodeQL where-clause fragment.
    /// Returns null if the filter cannot be transpiled (strict mode).
    /// </summary>
    private string? TryTranspileFilter(Expression filter, string cqlVar, string collName)
    {
        switch (filter)
        {
            // Bare identifier — could be a bool property or user predicate
            case IdentifierExpr id:
                return TryTranspileIdentifier(id.Name, negated: false, cqlVar, collName);

            // Negated identifier: !Public, !isAbstract
            case UnaryExpr { Operator: "!" or "not", Operand: IdentifierExpr id }:
                return TryTranspileIdentifier(id.Name, negated: true, cqlVar, collName);

            // Call expression: Name:startsWith('X'), Modifiers:isSet(Public)
            case CallExpr pc when pc.Target is not null:
                return TryTranspilePredicateCall(pc, cqlVar, collName);

            // Binary expression: Name == 'Foo', Line > 10, && / ||
            case BinaryExpr bin:
                return TryTranspileBinary(bin, cqlVar, collName);

            // Standalone CallExpr in filter chain (e.g., toError/toWarning)
            case CallExpr fc:
                // toError/toWarning/toInfo are handled at the let level, skip here
                if (fc.Name is "toError" or "toWarning" or "toInfo")
                    return null; // already extracted
                return null;

            // Negation of complex expression
            case UnaryExpr { Operator: "!" or "not" } un:
                var inner = TryTranspileFilter(un.Operand, cqlVar, collName);
                return inner is not null ? $"not ({inner})" : null;

            default:
                return null;
        }
    }

    private string? TryTranspileIdentifier(string name, bool negated, string cqlVar, string collName)
    {
        // Check if it's a known user predicate
        if (_allPredicates.TryGetValue(name, out var predDefs))
        {
            var inlined = TryInlinePredicateAsCql(predDefs[0], cqlVar, collName);
            if (inlined is not null)
                return negated ? $"not ({inlined})" : inlined;
            return null;
        }

        // Bool property on the type (e.g., Documented, Generic)
        var accessor = GetPropertyAccessor(name, cqlVar, collName);
        if (accessor is not null)
        {
            return negated ? $"not {accessor}" : accessor;
        }

        return null;
    }

    private string? TryTranspilePredicateCall(CallExpr pc, string cqlVar, string collName)
    {
        // Target.PredicateName(args) — target is property reference
        var propName = GetTargetPropertyName(pc.Target!);
        if (propName is null) return null;

        var prefix = pc.Negated ? "not " : "";

        // Modifier flags: Modifiers:isSet(Public)
        if (propName == "Modifiers" && pc.Name is "isSet" or "isClear")
        {
            return TryTranspileModifierCheck(pc, cqlVar, collName);
        }

        // Collection predicates first (before string ops, since 'contains' overlaps)
        // BaseTypes:contains('X'), Parameters:any(pred), Keywords:contains('var')
        if (pc.Name == "contains" && pc.Args.Count == 1 && pc.Args[0] is LiteralExpr containsLit && containsLit.Value is string containsVal)
        {
            // Try as collection first
            var collResult = TryTranspileCollectionContains(propName, containsVal, cqlVar, collName, pc.Negated);
            if (collResult is not null) return collResult;
            // Fall through to string contains if not a collection
        }

        if (pc.Name == "any" && pc.Args.Count == 1)
        {
            return TryTranspileCollectionAny(propName, pc.Args[0], cqlVar, collName, pc.Negated);
        }

        // String operations: Name:startsWith('X')
        if (pc.Args.Count == 1 && pc.Args[0] is LiteralExpr lit && lit.Value is string strVal)
        {
            var accessor = GetPropertyAccessor(propName, cqlVar, collName);
            if (accessor is null) return null;

            var result = pc.Name switch
            {
                "startsWith" or "sw" => $"{accessor}.toLowerCase().matches(\"{EscapeCqlString(strVal.ToLowerInvariant())}%\")",
                "endsWith" or "ew" => $"{accessor}.toLowerCase().matches(\"%{EscapeCqlString(strVal.ToLowerInvariant())}\")",
                "contains" or "ct" => $"{accessor}.toLowerCase().matches(\"%{EscapeCqlString(strVal.ToLowerInvariant())}%\")",
                "equals" or "eq" => $"{accessor}.toLowerCase() = \"{EscapeCqlString(strVal.ToLowerInvariant())}\"",
                "sameAs" or "sm" => $"{accessor} = \"{EscapeCqlString(strVal)}\"", // case-sensitive
                "matches" or "rx" => $"{accessor}.regexpMatch(\"{EscapeCqlRegex(strVal)}\")",
                _ => null
            };
            if (result is null) return null;
            return prefix + result;
        }

        // Numeric operations: Size:greaterThan(100)
        if (pc.Args.Count == 1 && pc.Args[0] is LiteralExpr numLit && numLit.Value is int or long or double)
        {
            var accessor = GetPropertyAccessor(propName, cqlVar, collName);
            if (accessor is null) return null;
            var numStr = numLit.Value.ToString();

            var result = pc.Name switch
            {
                "greaterThan" or "gt" => $"{accessor} > {numStr}",
                "lessThan" or "lt" => $"{accessor} < {numStr}",
                "greaterOrEqual" or "ge" => $"{accessor} >= {numStr}",
                "lessOrEqual" or "le" => $"{accessor} <= {numStr}",
                "equals" or "eq" => $"{accessor} = {numStr}",
                _ => null
            };
            if (result is null) return null;
            return prefix + result;
        }

        // List-argument predicates: Extension:in(['.cs', '.vb'])
        if (pc.Name == "in" && pc.Args.Count == 1 && pc.Args[0] is ListLiteralExpr listLit)
        {
            var accessor = GetPropertyAccessor(propName, cqlVar, collName);
            if (accessor is null) return null;

            var values = new List<string>();
            foreach (var elem in listLit.Elements)
            {
                if (elem is not LiteralExpr elemLit || elemLit.Value is not string sv)
                    return null;
                values.Add($"{accessor}.toLowerCase() = \"{EscapeCqlString(sv.ToLowerInvariant())}\"");
            }
            var disj = $"({string.Join(" or ", values)})";
            return pc.Negated ? $"not {disj}" : disj;
        }

        return null;
    }

    private string? TryTranspileModifierCheck(CallExpr pc, string cqlVar, string collName)
    {
        if (pc.Args.Count != 1) return null;

        // The argument is the flag name (identifier or literal)
        string? flagName = pc.Args[0] switch
        {
            IdentifierExpr id => id.Name,
            LiteralExpr lit when lit.Value is string s => s,
            LiteralExpr lit when lit.Value is int or long => null, // numeric mask — can't directly map
            _ => null
        };

        if (flagName is null || !ModifierToCqlPredicate.TryGetValue(flagName, out var cqlPred))
            return null;

        var result = $"{cqlVar}.{cqlPred}()";
        if (pc.Name == "isClear" || pc.Negated)
            result = $"not {cqlVar}.{cqlPred}()";
        if (pc.Name == "isClear" && pc.Negated)
            result = $"{cqlVar}.{cqlPred}()"; // double negation

        return result;
    }

    private string? TryTranspileBinary(BinaryExpr bin, string cqlVar, string collName)
    {
        // Logical AND/OR
        if (bin.Operator == "&&")
        {
            var left = TryTranspileFilter(bin.Left, cqlVar, collName);
            var right = TryTranspileFilter(bin.Right, cqlVar, collName);
            if (left is null || right is null) return null;
            return $"({left})\n  and ({right})";
        }

        if (bin.Operator == "||")
        {
            var left = TryTranspileFilter(bin.Left, cqlVar, collName);
            var right = TryTranspileFilter(bin.Right, cqlVar, collName);
            if (left is null || right is null) return null;
            return $"({left})\n  or ({right})";
        }

        // Property comparison: Name == 'Foo', Line > 10
        var (propName, op, literal) = ExtractComparisonParts(bin, cqlVar);
        if (propName is null || literal is null) return null;

        // Special handling: Kind == 'value' → CodeQL instanceof
        if (string.Equals(propName, "Kind", StringComparison.OrdinalIgnoreCase)
            && op == "==" && literal is string kindVal
            && StatementKindToCqlClass.TryGetValue(kindVal, out var cqlClass))
        {
            return $"{cqlVar} instanceof {cqlClass}";
        }

        var accessor = GetPropertyAccessor(propName, cqlVar, collName);
        if (accessor is null) return null;

        if (literal is string strLit)
        {
            var cqlOp = op switch
            {
                "==" => $"{accessor}.toLowerCase() = \"{EscapeCqlString(strLit.ToLowerInvariant())}\"",
                "!=" => $"not {accessor}.toLowerCase() = \"{EscapeCqlString(strLit.ToLowerInvariant())}\"",
                _ => null
            };
            return cqlOp;
        }

        if (literal is int or long or double or float)
        {
            var numStr = literal.ToString();
            return op switch
            {
                "==" => $"{accessor} = {numStr}",
                "!=" => $"not {accessor} = {numStr}",
                ">" => $"{accessor} > {numStr}",
                "<" => $"{accessor} < {numStr}",
                ">=" => $"{accessor} >= {numStr}",
                "<=" => $"{accessor} <= {numStr}",
                _ => null
            };
        }

        if (literal is bool boolVal)
        {
            return op switch
            {
                "==" when boolVal => accessor,
                "==" when !boolVal => $"not {accessor}",
                "!=" when boolVal => $"not {accessor}",
                "!=" when !boolVal => accessor,
                _ => null
            };
        }

        return null;
    }

    private static (string? PropName, string Op, object? Literal) ExtractComparisonParts(BinaryExpr bin, string cqlVar)
    {
        // Left is property, right is literal
        var propName = GetTargetPropertyName(bin.Left);
        if (propName is not null && bin.Right is LiteralExpr rightLit)
            return (propName, bin.Operator, rightLit.Value);

        // Right is property, left is literal — flip operator
        propName = GetTargetPropertyName(bin.Right);
        if (propName is not null && bin.Left is LiteralExpr leftLit)
        {
            var flipped = bin.Operator switch
            {
                ">" => "<",
                "<" => ">",
                ">=" => "<=",
                "<=" => ">=",
                _ => bin.Operator
            };
            return (propName, flipped, leftLit.Value);
        }

        return (null, bin.Operator, null);
    }

    /// <summary>
    /// Tries to inline a cop predicate definition as a CodeQL where-clause fragment.
    /// </summary>
    private string? TryInlinePredicateAsCql(PredicateDefinition pred, string cqlVar, string collName)
    {
        return TryTranspileFilter(pred.Body, cqlVar, collName);
    }

    private string? TryTranspileCollectionContains(string propName, string value, string cqlVar, string collName, bool negated)
    {
        // BaseTypes:contains('X') → exists(string base | base = t.getASupertype().getName() | base.toLowerCase() = "x")
        // Keywords:contains('X') → similar exists pattern
        // MethodNames:contains('X') → exists(Method m | m = t.getAMethod() | m.getName().toLowerCase() = "x")

        var accessor = GetCollectionAccessor(propName, cqlVar, collName);
        if (accessor is null) return null;

        var lowerVal = EscapeCqlString(value.ToLowerInvariant());
        var result = $"exists({accessor.ElementType} _elem | _elem = {accessor.Accessor} | _elem.toLowerCase() = \"{lowerVal}\")";
        return negated ? $"not " + result : result;
    }

    private string? TryTranspileCollectionAny(string propName, Expression predExpr, string cqlVar, string collName, bool negated)
    {
        // Parameters:any(hasXxx) → exists(Parameter p | p = m.getAParameter() | hasXxx_cql(p))
        var accessor = GetCollectionAccessor(propName, cqlVar, collName);
        if (accessor is null) return null;

        var elemVar = "_" + accessor.ElementType.ToLowerInvariant()[0];

        // The predExpr is either a predicate name or an inline expression
        string? innerClause;
        if (predExpr is IdentifierExpr predId && _allPredicates.TryGetValue(predId.Name, out var predDefs))
        {
            innerClause = TryInlinePredicateAsCql(predDefs[0], elemVar, propName);
        }
        else
        {
            innerClause = TryTranspileFilter(predExpr, elemVar, propName);
        }

        if (innerClause is null) return null;

        var result = $"exists({accessor.ElementType} {elemVar} | {elemVar} = {accessor.Accessor} | {innerClause})";
        return negated ? $"not " + result : result;
    }

    /// <summary>
    /// Maps a cop property name to a CodeQL accessor expression.
    /// </summary>
    private static string? GetPropertyAccessor(string propName, string cqlVar, string collName)
    {
        // Normalize: if propName is "Statement.MemberName", extract "MemberName"
        var dotIdx = propName.IndexOf('.');
        if (dotIdx >= 0)
            propName = propName[(dotIdx + 1)..];

        return propName switch
        {
            "Name" => $"{cqlVar}.getName()",
            "Kind" => null, // Handled by type narrowing, not as property
            "MemberName" => collName is "Statements" or "Calls"
                ? $"{cqlVar}.getMethod().getName()"
                : $"{cqlVar}.getName()",
            "TypeName" => collName is "Statements" or "Calls"
                ? $"{cqlVar}.getMethod().getDeclaringType().getName()"
                : $"{cqlVar}.getDeclaringType().getName()",
            "Line" => $"{cqlVar}.getLocation().getStartLine()",
            "Documented" => $"{cqlVar}.getDoc().getJavadoc() instanceof Javadoc", // approximation
            "File" => $"{cqlVar}.getFile()",
            "Path" => $"{cqlVar}.getFile().getRelativePath()",
            "Source" => $"{cqlVar}.toString()",
            "ReturnType" => $"{cqlVar}.getReturnType()",
            "Modifiers" => null, // Handled by isSet/isClear, not as raw value
            "Generic" => $"{cqlVar}.isGeneric()",
            "Variadic" => $"{cqlVar}.isVarargs()",
            "Defaulted" => $"exists({cqlVar}.getDefaultValue())",
            "HasGetter" => $"exists({cqlVar}.getGetter())",
            "HasSetter" => $"exists({cqlVar}.getSetter())",
            "Namespace" => $"{cqlVar}.getNamespace().getName()",
            "Condition" => $"{cqlVar}.getCondition().toString()",
            "Expression" => $"{cqlVar}.toString()",
            "Signature" => $"{cqlVar}.getSignature()",
            _ => null
        };
    }

    /// <summary>
    /// Maps a cop collection property to a CodeQL accessor for collection operations (any, contains).
    /// </summary>
    private static CollectionAccessorInfo? GetCollectionAccessor(string propName, string cqlVar, string collName)
    {
        return propName switch
        {
            "BaseTypes" => new("string", $"{cqlVar}.getASupertype().getName()"),
            "Methods" => new("Method", $"{cqlVar}.getAMethod()"),
            "MethodNames" => new("string", $"{cqlVar}.getAMethod().getName()"),
            "Constructors" => new("Constructor", $"{cqlVar}.getAConstructor()"),
            "Parameters" => new("Parameter", $"{cqlVar}.getAParameter()"),
            "Fields" => new("Field", $"{cqlVar}.getAField()"),
            "Properties" => new("Property", $"{cqlVar}.getAProperty()"),
            "Events" => new("Event", $"{cqlVar}.getAnEvent()"),
            "NestedTypes" => new("RefType", $"{cqlVar}.getANestedType()"),
            "Statements" => new("Stmt", $"{cqlVar}.getBody().getAChild()"),
            "Decorators" => new("Attribute", $"{cqlVar}.getAnAttribute()"),
            "Keywords" => new("string", $"{cqlVar}.getAKeyword()"), // approximation
            "Arguments" => new("Expr", $"{cqlVar}.getAnArgument()"),
            "Children" => new("Stmt", $"{cqlVar}.getAChild()"),
            "Ancestors" => new("Stmt", $"{cqlVar}.getAnAncestor()"),
            "EnumValues" => new("EnumConstant", $"{cqlVar}.getAnEnumConstant()"),
            "GenericArguments" => new("Type", $"{cqlVar}.getATypeArgument()"),
            _ => null
        };
    }

    private record CollectionAccessorInfo(string ElementType, string Accessor);

    /// <summary>
    /// Extracts a property name from an expression target (IdentifierExpr or MemberAccessExpr).
    /// </summary>
    private static string? GetTargetPropertyName(Expression expr)
    {
        return expr switch
        {
            IdentifierExpr id => id.Name,
            MemberAccessExpr ma => ma.Member,
            _ => null
        };
    }

    /// <summary>
    /// Transpiles a cop message template like '{item.Name} has issue' into a CodeQL select expression.
    /// </summary>
    private string TranspileMessageTemplate(string template, string cqlVar)
    {
        var sb = new StringBuilder();
        var i = 0;
        var parts = new List<string>();

        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                var end = template.IndexOf('}', i);
                if (end < 0)
                {
                    sb.Append(template[i..]);
                    break;
                }

                // Flush text before placeholder
                if (sb.Length > 0)
                {
                    parts.Add($"\"{EscapeCqlString(sb.ToString())}\"");
                    sb.Clear();
                }

                var placeholder = template[(i + 1)..end];
                // Strip style annotation: {text@style} → text
                var atIdx = placeholder.IndexOf('@');
                if (atIdx >= 0)
                    placeholder = placeholder[..atIdx];

                // Resolve placeholder to CodeQL expression
                // Common: item.Name, item.File.Path, item.Line
                var cqlExpr = ResolvePlaceholder(placeholder, cqlVar);
                parts.Add(cqlExpr);

                i = end + 1;
            }
            else
            {
                sb.Append(template[i]);
                i++;
            }
        }

        if (sb.Length > 0)
            parts.Add($"\"{EscapeCqlString(sb.ToString())}\"");

        if (parts.Count == 0)
            return "\"\"";
        if (parts.Count == 1)
            return parts[0];

        return string.Join(" + ", parts);
    }

    private static string ResolvePlaceholder(string placeholder, string cqlVar)
    {
        // Strip "item." prefix
        if (placeholder.StartsWith("item.", StringComparison.OrdinalIgnoreCase))
            placeholder = placeholder[5..];

        // Chain: File.Path → cqlVar.getFile().getRelativePath()
        return placeholder switch
        {
            "Name" => $"{cqlVar}.getName()",
            "MemberName" => $"{cqlVar}.getMethod().getName()",
            "TypeName" => $"{cqlVar}.getMethod().getDeclaringType().getName()",
            "File.Path" or "File" => $"{cqlVar}.getFile().getRelativePath()",
            "Line" => $"{cqlVar}.getLocation().getStartLine().toString()",
            "Source" => $"{cqlVar}.toString()",
            "Signature" => $"{cqlVar}.getSignature()",
            _ => $"\"{placeholder}\"" // fallback: literal
        };
    }

    private static bool IsLanguageFilter(string name) =>
        name is "csharp" or "python" or "javascript";

    internal static string SanitizeIdentifier(string name)
    {
        // Replace hyphens with underscores (CodeQL doesn't allow hyphens in identifiers)
        return name.Replace('-', '_');
    }

    /// <summary>
    /// Escapes a string for use in CodeQL string literals.
    /// Handles quotes, backslashes, and CodeQL matches() wildcards.
    /// </summary>
    internal static string EscapeCqlString(string value)
    {
        return value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
    }

    /// <summary>
    /// Escapes a regex pattern for CodeQL's regexpMatch() predicate.
    /// </summary>
    private static string EscapeCqlRegex(string pattern)
    {
        return pattern.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}

/// <summary>
/// Result of a CodeQL transpilation.
/// </summary>
public record CqlTranspileResult(List<CqlQueryFile> Files, List<string> Errors)
{
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// A single generated CodeQL query file.
/// </summary>
public record CqlQueryFile(string FileName, string Content);
