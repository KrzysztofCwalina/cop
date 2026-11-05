// Projects rust-analyzer syntax nodes into shared declarations and source facts.

using System.Text.Json;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

/// <summary>Projects maintained Rust syntax nodes; no source grammar or name binding is inferred here.</summary>
internal sealed class RustAstProjection
{
    private sealed class Node(string kind, int start, int end)
    {
        /// <summary>Gets the rust-analyzer syntax category.</summary>
        public string Kind { get; } = kind;
        /// <summary>Gets the starting UTF-8 byte offset.</summary>
        public int Start { get; } = start;
        /// <summary>Gets the exclusive ending UTF-8 byte offset.</summary>
        public int End { get; } = end;
        /// <summary>Links to the enclosing syntax node.</summary>
        public Node? Parent;
        /// <summary>Contains immediately nested syntax nodes.</summary>
        public List<Node> Children = [];
        /// <summary>Contains token spellings attached to this node.</summary>
        public List<string> Tokens = [];
        /// <summary>Finds the first child of the given syntax kind.</summary>
        public Node? Child(string kind) => Children.FirstOrDefault(c => c.Kind == kind);
        /// <summary>Enumerates enclosing nodes from nearest to outermost.</summary>
        public IEnumerable<Node> Ancestors()
        {
            for (Node? node = Parent; node is not null; node = node.Parent)
            {
                yield return node;
            }
        }
    }

    private readonly string _path;
    private readonly string _text;
    private readonly JsonElement _response;
    private readonly RustSourcePositions _positions;
    private readonly List<Node> _nodes = [];
    private readonly Dictionary<Node, TypeDeclaration> _types = [];
    private readonly Dictionary<Node, MethodDeclaration> _methods = [];
    private readonly Dictionary<Node, StatementInfo> _statements = [];
    private readonly Dictionary<Node, Node> _branches = [];

    /// <summary>
    /// Creates a source-model projector for a rust-analyzer parse response.
    /// </summary>
    public RustAstProjection(string path, string text, JsonElement response)
    {
        _path = path; _text = text; _response = response; _positions = new(text);
        foreach (JsonElement item in response.GetProperty("nodes").EnumerateArray())
        {
            Node node = new Node(item.GetProperty("kind").GetString()!,
                item.GetProperty("start").GetInt32(), item.GetProperty("end").GetInt32());
            if (item.GetProperty("parent") is { ValueKind: JsonValueKind.Number } parent)
            {
                node.Parent = _nodes[parent.GetInt32()];
                node.Parent.Children.Add(node);
            }
            if (item.TryGetProperty("tokens", out JsonElement tokens))
            {
                node.Tokens = tokens.EnumerateArray().Select(t => t.GetString()!).ToList();
            }

            _nodes.Add(node);
        }
    }

    /// <summary>
    /// Builds shared declarations and statements from the frontend syntax tree.
    /// </summary>
    public SourceFile Build()
    {
        List<TypeDeclaration> types = new List<TypeDeclaration>();
        foreach (Node? node in _nodes.Where(n => n.Kind is "STRUCT" or "ENUM" or "TRAIT" or "UNION" or "TYPE_ALIAS" or "IMPL"))
        {
            (int Line, int Column, int EndLine, int EndColumn) span = Span(node);
            bool impl = node.Kind == "IMPL";
            Node? selfType = node.Children.LastOrDefault(IsType);
            string trait = impl && node.Children.Count(IsType) > 1 ? Text(node.Children.First(IsType)) : "";
            string name = impl ? Text(selfType) + (trait.Length == 0 ? " (impl)" : $" (impl {trait})") : Name(node);
            List<FieldDeclaration> fields = node.Children.SelectMany(n => n.Kind is "RECORD_FIELD_LIST" or "TUPLE_FIELD_LIST" ? n.Children : [])
                .Where(n => n.Kind is "RECORD_FIELD" or "TUPLE_FIELD").Select((field, index) =>
                    new FieldDeclaration(field.Kind == "TUPLE_FIELD" ? index.ToString() : Name(field),
                        Type(field.Children.FirstOrDefault(IsType)), Modifiers(field), Span(field).Line)).ToList();
            RustTypeDeclaration type = new TypeDeclaration(name, node.Kind switch
            { "STRUCT" or "UNION" => TypeKind.Struct, "ENUM" => TypeKind.Enum,
                "TRAIT" => TypeKind.Interface, "TYPE_ALIAS" => TypeKind.Alias, _ => TypeKind.Class },
                Modifiers(node), impl ? node.Children.Where(IsType).SkipLast(1).Select(Text).ToList()
                    : node.Child("TYPE_BOUND_LIST")?.Children.SelectMany(n => n.Children.Where(IsType)).Select(Text).ToList() ?? [],
                Attributes(node), [], [], [], [], span.Line)
            {
                Namespace = Namespace(node), Fields = fields, Column = span.Column,
                HasDocComment = node.Child("DOC_COMMENT") is not null,
                DocComment = Documentation(node),
                EndLine = span.EndLine, EndColumn = span.EndColumn, TypeParameters = Generics(node),
                AliasTarget = node.Kind == "TYPE_ALIAS" ? Type(selfType) : null,
                EnumValues = node.Child("VARIANT_LIST")?.Children.Where(n => n.Kind == "VARIANT").Select(Name).ToList() ?? []
            }.AsRust(isTrait: node.Kind == "TRAIT", isImpl: impl, isUnsafe: node.Tokens.Contains("unsafe"),
                isUnion: node.Kind == "UNION", isTupleStruct: node.Child("TUPLE_FIELD_LIST") is not null,
                isUnitStruct: node.Kind == "STRUCT" && fields.Count == 0 && node.Tokens.Contains(";"),
                isNegativeImpl: impl && node.Tokens.Contains("!"));
            types.Add(type); _types.Add(node, type);
            if (node.Kind == "ENUM")
            {
                foreach (Node? variant in node.Child("VARIANT_LIST")?.Children.Where(n => n.Kind == "VARIANT") ?? [])
                {
                    Node? payload = variant.Children.FirstOrDefault(n => n.Kind is "RECORD_FIELD_LIST" or "TUPLE_FIELD_LIST");
                    if (payload is null)
                    {
                        continue;
                    }

                    (int Line, int Column, int EndLine, int EndColumn) location = Span(variant);
                    type.NestedTypes.Add(new TypeDeclaration(Name(variant), TypeKind.Struct, type.Modifiers,
                        [], Attributes(variant), [], [], [], [], location.Line)
                    {
                        Namespace = type.Namespace, Column = location.Column, EndLine = location.EndLine,
                        EndColumn = location.EndColumn,
                        Fields = payload.Children.Where(n => n.Kind is "RECORD_FIELD" or "TUPLE_FIELD")
                            .Select((field, index) => new FieldDeclaration(field.Kind == "TUPLE_FIELD" ? index.ToString() : Name(field),
                                Type(field.Children.FirstOrDefault(IsType)), Modifiers(field), Span(field).Line)).ToList()
                    }.AsRust());
                }
            }
        }
        foreach (Node? node in _nodes.Where(n => n.Kind == "FN"))
        {
            (int Line, int Column, int EndLine, int EndColumn) span = Span(node);
            List<ParameterDeclaration> parameters = node.Child("PARAM_LIST")?.Children.Where(n => n.Kind == "PARAM").Select(parameter =>
            {
                (int Line, int Column, int EndLine, int EndColumn) location = Span(parameter);
                Node? pattern = parameter.Children.FirstOrDefault(n => n.Kind.EndsWith("_PAT", StringComparison.Ordinal));
                return new ParameterDeclaration(pattern?.Kind == "IDENT_PAT" ? Name(pattern) : Text(pattern), Type(parameter.Children.FirstOrDefault(IsType)),
                    parameter.Tokens.Contains("..."), false, false, location.Line)
                { Column = location.Column, EndLine = location.EndLine, EndColumn = location.EndColumn };
            }).ToList() ?? [];
            Node? traitOwner = node.Ancestors().FirstOrDefault(n => n.Kind == "TRAIT");
            Modifier methodModifiers = traitOwner is not null && node.Child("VISIBILITY") is null
                ? _types[traitOwner].Modifiers : Modifiers(node);
            RustMethodDeclaration method = new MethodDeclaration(Name(node), methodModifiers, Attributes(node),
                Type(node.Child("RET_TYPE")?.Children.FirstOrDefault(IsType)), parameters, span.Line)
            {
                Column = span.Column, EndLine = span.EndLine, EndColumn = span.EndColumn, TypeParameters = Generics(node),
                HasDocComment = node.Child("DOC_COMMENT") is not null, DocComment = Documentation(node),
                ReceiverType = node.Ancestors().FirstOrDefault(n => n.Kind == "IMPL") is { } implementation
                    ? Text(implementation.Children.LastOrDefault(IsType)) : null
            }.AsRust(isUnsafe: node.Tokens.Contains("unsafe"), isConst: node.Tokens.Contains("const"),
                isExtern: node.Child("ABI") is not null || node.Ancestors().Any(n => n.Kind == "EXTERN_BLOCK"));
            _methods.Add(node, method);
            Node? owner = node.Ancestors().FirstOrDefault(n => _types.ContainsKey(n));
            if (owner?.Kind == "IMPL")
            {
                Node? selfType = owner.Children.LastOrDefault(IsType);
                // Only an exact same-module local target can be moved without semantic binding.
                Node? path = selfType?.Child("PATH");
                string target = path?.Child("PATH") is null && path?.Child("PATH_SEGMENT")?.Child("NAME_REF") is { } localName
                    ? Text(localName) : Text(path ?? selfType);
                if (target.StartsWith("r#", StringComparison.Ordinal))
                {
                    target = target[2..];
                }

                List<KeyValuePair<Node, TypeDeclaration>> declared = _types.Where(p => p.Key.Kind is "STRUCT" or "ENUM" or "UNION"
                    && p.Value.Name == target && p.Value.Namespace == Namespace(owner)).ToList();
                if (declared.Count == 1)
                {
                    owner = declared[0].Key;
                }
            }
            if (owner is not null)
            {
                _types[owner].Methods.Add(method);
            }
            else
            {
                string scope = Namespace(node);
                TypeDeclaration? container = types.FirstOrDefault(t => t.Name == "(functions)" && t.Namespace == scope);
                if (container is null)
                {
                    container = new TypeDeclaration("(functions)", TypeKind.Class, Modifier.None,
                        [], [], [], [], [], [], span.Line) { Namespace = scope }.AsRust();
                    types.Add(container);
                }
                container.Methods.Add(method);
            }
        }
        foreach (Node node in _nodes)
        {
            string? kind = StatementKind(node);
            if (kind is null)
            {
                continue;
            }

            (int Line, int Column, int EndLine, int EndColumn) span = Span(node);
            ExpressionInfo? expression = IsExpression(node) ? Expression(node) : node.Children.FirstOrDefault(IsExpression) is { } value
                ? Expression(value) : null;
            string? name = node.Kind switch
            {
                "CALL_EXPR" => CallName(node.Children.FirstOrDefault(IsExpression)),
                "METHOD_CALL_EXPR" => Text(node.Child("NAME_REF")),
                "MACRO_EXPR" => MacroName(node), "LET_STMT" =>
                    node.Children.FirstOrDefault(n => n.Kind.EndsWith("_PAT", StringComparison.Ordinal)) is { Kind: "IDENT_PAT" } pattern
                        ? Name(pattern) : null,
                _ => null
            };
            List<Node> args = node.Child("ARG_LIST")?.Children.Where(IsExpression).ToList() ?? [];
            Node? condition = node.Kind is "IF_EXPR" or "WHILE_EXPR" or "FOR_EXPR" or "MATCH_EXPR"
                ? node.Children.FirstOrDefault(IsExpression) : null;
            RustStatementInfo statement = new RustStatementInfo(kind, node.Tokens.Where(t => t.All(char.IsLetter)).ToList(),
                node.Kind == "CALL_EXPR" ? Text(node.Children.FirstOrDefault(IsExpression)?.Child("PATH")?.Child("PATH")) is { Length: > 0 } qualifier
                    ? qualifier : null : null, name, args.Select(Text).ToList(), span.Line, node.Ancestors().Any(n => n.Kind == "FN"))
            {
                Column = span.Column, EndLine = span.EndLine, EndColumn = span.EndColumn,
                CallTarget = kind is "call" or "throw" ? expression?.Target : null,
                ArgumentExpressions = args.Select(n => Expression(n)).ToList(),
                ValueExpression = node.Kind is "RETURN_EXPR" or "BREAK_EXPR" or "YIELD_EXPR"
                    ? expression?.Children.FirstOrDefault() : condition is not null ? Expression(condition) : expression,
                Condition = condition is not null ? Text(condition) : null,
                Expression = Text(node), IsBraced = node.Child("BLOCK_EXPR") is not null
            };
            _statements.Add(node, statement);
        }
        foreach (Node? node in _nodes.Where(n => n.Kind == "LET_ELSE" || n.Kind == "IF_EXPR" && n.Tokens.Contains("else")))
        {
            Node? branch = node.Children.LastOrDefault(IsExpression);
            if (branch is null)
            {
                continue;
            }

            Node synthetic = new Node("ELSE", branch.Start, branch.End)
            {
                Parent = _branches.GetValueOrDefault(node) ?? node.Parent
            };
            (int Line, int Column, int EndLine, int EndColumn) span = Span(branch);
            _branches.Add(branch, synthetic);
            _statements.Add(synthetic, new RustStatementInfo("else", ["else"], null, null, [], span.Line, true)
            {
                Column = span.Column, EndLine = span.EndLine, EndColumn = span.EndColumn,
                IsBraced = branch.Kind == "BLOCK_EXPR"
            });
        }
        foreach ((Node? node, StatementInfo? statement) in _statements)
        {
            Node? parent = _branches.GetValueOrDefault(node) ?? node.Ancestors()
                .FirstOrDefault(n => _branches.ContainsKey(n) || _statements.ContainsKey(n));
            if (parent is not null && !_statements.ContainsKey(parent) &&
                _branches.TryGetValue(parent, out Node? alternative))
            {
                parent = alternative;
            }

            if (parent is not null)
            { statement.Parent = _statements[parent]; statement.Parent._children.Add(statement); }
            Node? method = node.Ancestors().FirstOrDefault(n => n.Kind == "FN");
            if (method is not null)
            { statement.Method = _methods[method]; statement.Method.Statements.Add(statement); }
        }
        foreach (MethodDeclaration method in _methods.Values)
        {
            List<StatementInfo> ordered = method.Statements.OrderBy(s => s.Line).ThenBy(s => s.Column)
                .ThenBy(s => s.Kind == "else" ? 0 : 1).ToList();
            method.Statements.Clear();
            method.Statements.AddRange(ordered);
        }
        List<string> errors = _response.GetProperty("errors").EnumerateArray().Select(error =>
        {
            (int Line, int Column, int EndLine, int EndColumn) span = _positions.Span(error.GetProperty("start").GetInt32(), error.GetProperty("end").GetInt32());
            return $"{_path}({span.Line},{span.Column}): error: {error.GetProperty("message").GetString()}";
        }).ToList();
        List<ImportInfo> imports = _nodes.Where(n => n.Kind == "USE_TREE" && n.Child("USE_TREE_LIST") is null)
            .Select(n => new ImportInfo(UsePath(n), Span(n).Line)
            { Alias = n.Child("RENAME")?.Child("NAME") is { } alias ? Text(alias) : null,
                IsWildcard = n.Tokens.Contains("*") }).ToList();
        HashSet<int> comments = new HashSet<int>();
        foreach (Node? comment in _nodes.Where(n => n.Kind == "DOC_COMMENT"))
        {
            (int Line, int Column, int EndLine, int EndColumn) span = Span(comment);
            for (int line = span.Line; line <= span.EndLine; line++)
            {
                comments.Add(line);
            }
        }
        if (_response.TryGetProperty("comments", out JsonElement commentRanges))
        {
            foreach (JsonElement comment in commentRanges.EnumerateArray())
            {
                (int Line, int Column, int EndLine, int EndColumn) span = _positions.Span(comment.GetProperty("start").GetInt32(), comment.GetProperty("end").GetInt32());
                for (int line = span.Line; line <= span.EndLine; line++)
                {
                    comments.Add(line);
                }
            }
        }

        return new(_path, "rust", types, _statements.Values.OrderBy(s => s.Line).ThenBy(s => s.Column)
            .ThenBy(s => s.Kind == "else" ? 0 : 1).ToList(), _text)
        {
            ParseErrors = errors, Imports = imports, Usings = imports.Select(i => i.Module).ToList(),
            CommentLines = comments
        };
    }

    private ExpressionInfo Expression(Node node, int depth = 0)
    {
        if (depth > 256)
        {
            throw new InvalidDataException("Rust AST expression projection exceeded depth limit.");
        }

        List<Node> expressions = ExpressionChildren(node).ToList();
        string kind = node.Kind switch
        {
            "CALL_EXPR" or "METHOD_CALL_EXPR" => "invocation", "MACRO_EXPR" => "macroInvocation",
            "PATH_EXPR" => "identifier", "FIELD_EXPR" => "memberAccess", "LITERAL" =>
                node.Tokens.Any(t => t.StartsWith('"') || t.StartsWith("r\"", StringComparison.Ordinal)
                    || t.StartsWith("r#", StringComparison.Ordinal)) ? "stringLiteral" : "literal",
            "BIN_EXPR" => "binary", "PREFIX_EXPR" or "REF_EXPR" => "unary", "TUPLE_EXPR" => "tuple",
            "ARRAY_EXPR" => "array", "INDEX_EXPR" => "elementAccess", "CLOSURE_EXPR" => "lambda",
            "BLOCK_EXPR" => "block", "AWAIT_EXPR" => "await", "TRY_EXPR" => "try",
            "RECORD_EXPR" => "objectCreation", _ => "other"
        };
        ExpressionInfo? target = null;
        string? name = null;
        if (node.Kind == "METHOD_CALL_EXPR")
        {
            name = Text(node.Child("NAME_REF"));
            target = new("memberAccess") { Name = name,
                Target = expressions.FirstOrDefault() is { } receiver ? Expression(receiver, depth + 1) : null };
        }
        else if (node.Kind == "CALL_EXPR")
        {
            target = expressions.FirstOrDefault() is { } callee ? Expression(callee, depth + 1) : null;
        }
        else if (node.Kind == "MACRO_EXPR")
        { name = MacroName(node); target = new("identifier") { Name = name }; }
        else if (node.Kind == "PATH_EXPR")
        {
            name = Text(node.Child("PATH"));
        }
        else if (node.Kind == "FIELD_EXPR")
        { name = Text(node.Child("NAME_REF")); target = expressions.FirstOrDefault() is { } receiver ? Expression(receiver, depth + 1) : null; }
        else if (node.Kind is "BIN_EXPR" or "PREFIX_EXPR" or "REF_EXPR")
        {
            name = string.Concat(node.Tokens);
        }

        List<Node> children = node.Kind is "CALL_EXPR" or "METHOD_CALL_EXPR"
            ? node.Child("ARG_LIST")?.Children.Where(IsExpression).ToList() ?? [] : expressions;
        return new(kind)
        {
            Name = name, Target = target, Children = children.Select(n => Expression(n, depth + 1)).ToList(),
            StringValue = kind == "stringLiteral" ? RustBodyParser.DecodeString(Text(node)) : null
        };
    }

    private static bool IsExpression(Node node) => node.Kind.EndsWith("_EXPR", StringComparison.Ordinal) || node.Kind == "LITERAL";
    private static IEnumerable<Node> ExpressionChildren(Node node)
    {
        Stack<Node> pending = new Stack<Node>(node.Children.AsEnumerable().Reverse());
        while (pending.TryPop(out Node? child))
        {
            if (IsExpression(child))
            {
                yield return child;
            }
            else if (child.Kind is not "FN" && !IsType(child))
            {
                foreach (Node? nested in child.Children.AsEnumerable().Reverse())
                {
                    pending.Push(nested);
                }
            }
        }
    }
    private static bool IsType(Node node) => node.Kind.EndsWith("_TYPE", StringComparison.Ordinal) && node.Kind != "RET_TYPE";
    private static string? StatementKind(Node node) => node.Kind switch
    {
        "CALL_EXPR" or "METHOD_CALL_EXPR" => "call",
        "MACRO_EXPR" => node.Child("MACRO_CALL")?.Child("PATH") is { } path
            && path.Children.LastOrDefault()?.Child("NAME_REF") is { } name
            && name.Tokens.Any(t => t is "panic" or "todo" or "unreachable" or "unimplemented") ? "throw" : "call",
        "LET_STMT" => "declaration", "IF_EXPR" => "if", "WHILE_EXPR" or "LOOP_EXPR" => "while",
        "FOR_EXPR" => "for", "MATCH_EXPR" => "switch", "RETURN_EXPR" => "return",
        "BREAK_EXPR" or "CONTINUE_EXPR" or "YIELD_EXPR" => "command", "AWAIT_EXPR" => "await",
        _ => null
    };
    private string MacroName(Node node) => Text(node.Child("MACRO_CALL")?.Child("PATH")) + "!";
    private string CallName(Node? node) => node?.Kind == "PATH_EXPR"
        ? Text(node.Child("PATH")?.Children.LastOrDefault(n => n.Kind == "PATH_SEGMENT")?.Child("NAME_REF"))
        : node?.Kind == "FIELD_EXPR" ? Text(node.Child("NAME_REF")) : "";
    private string Name(Node node)
    {
        string name = Text(node.Child("NAME"));
        return name.StartsWith("r#", StringComparison.Ordinal) ? name[2..] : name;
    }
    private string? Documentation(Node node) => node.Children.Any(n => n.Kind == "DOC_COMMENT")
        ? string.Join("\n", node.Children.Where(n => n.Kind == "DOC_COMMENT").Select(Text)) : null;
    private string UsePath(Node node) => string.Join("::", node.Ancestors().Where(n => n.Kind == "USE_TREE").Reverse()
        .Append(node).Select(n => Text(n.Child("PATH"))).Where(p => p.Length != 0));
    private string Text(Node? node) => node is null ? "" : _text[_positions.Character(node.Start).._positions.Character(node.End)];
    private TypeReference? Type(Node? node) => node is null ? null : RustTypeReferences.Parse(Text(node),
        () => throw new InvalidDataException("Rust AST type projection exceeded depth limit."));
    private (int Line, int Column, int EndLine, int EndColumn) Span(Node node) => _positions.Span(node.Start, node.End);
    private string Namespace(Node node) => string.Join("::", node.Ancestors().Where(n => n.Kind == "MODULE").Reverse().Select(Name));
    private List<string> Attributes(Node node) => node.Children.Where(n => n.Kind == "ATTR").Select(Text).ToList();
    private List<string> Generics(Node node) => node.Child("GENERIC_PARAM_LIST")?.Children
        .Where(n => n.Kind is "TYPE_PARAM" or "LIFETIME_PARAM" or "CONST_PARAM")
        .Select(n => Text(n.Child("NAME") ?? n.Child("LIFETIME"))).ToList() ?? [];
    private static Modifier Modifiers(Node node)
    {
        Node? visibility = node.Child("VISIBILITY");
        Modifier value = visibility is null ? Modifier.Private : visibility.Child("VISIBILITY_INNER") is null
            ? Modifier.Public : Modifier.Internal;
        if (node.Tokens.Contains("async"))
        {
            value |= Modifier.Async;
        }

        return value;
    }
}
