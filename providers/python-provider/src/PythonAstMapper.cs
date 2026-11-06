// Projects Python AST protocol responses into the shared Cop source model.

using System.Text.Json;
using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal sealed class PythonAstMapper(string path, string source)
{
    private readonly List<TypeDeclaration> _types = [];
    private readonly List<MethodDeclaration> _functions = [];
    private readonly List<StatementInfo> _statements = [];
    private readonly List<string> _imports = [];
    private readonly List<ImportInfo> _importFacts = [];

    /// <summary>Projects a Python AST response into a file-scoped source model.</summary>
    public SourceFile Parse(JsonElement document)
    {
        if (document.TryGetProperty("error", out JsonElement error))
        {
            return new(path, "python", [], [], source)
            {
                ParseErrors = [$"{path}({document.GetProperty("line")},{document.GetProperty("column")}): error: {error.GetString()}"],
            };
        }

        Walk(document.GetProperty("tree"), null, null);
        return new(path, "python", _types, _statements, source)
        {
            Methods = _functions.OrderBy(m => m.Line).ToList(),
            Usings = _imports.Distinct(StringComparer.Ordinal).ToList(),
            Imports = _importFacts,
            CommentLines = document.GetProperty("comments").EnumerateArray().Select(n => n.GetInt32()).ToHashSet(),
            Regions = PythonParserCore.ExtractRegions(source),
        };
    }

    private static string Kind(JsonElement node) => String(node, "kind");
    private static string String(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
    private static int Line(JsonElement node) => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("line", out JsonElement line) ? line.GetInt32() : 0;
    private static JsonElement Get(JsonElement node, string key) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(key, out JsonElement value) ? value : default;
    private static IEnumerable<JsonElement> Items(JsonElement node) =>
        node.ValueKind == JsonValueKind.Array ? node.EnumerateArray() : [];
    private static string Text(JsonElement node) => String(node, "text");
    private static int? Position(JsonElement node, string key) =>
        Get(node, key).ValueKind == JsonValueKind.Number ? Get(node, key).GetInt32() : null;
    private static Modifier Visibility(string name) =>
        name.StartsWith('_') && !(name.StartsWith("__") && name.EndsWith("__")) ? Modifier.Private : Modifier.Public;
    private static string Decorator(JsonElement node) => Kind(node) == "Call"
        ? Text(Get(node, "func")) : Text(node);

    private static TypeReference? Type(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string originalText = Text(node);
        string text = IsString(node)
            ? Get(node, "value").GetString()! : Text(node);
        List<TypeReference> arguments = Kind(node) == "Subscript"
            ? Kind(Get(node, "slice")) == "Tuple"
                ? Items(Get(Get(node, "slice"), "elts")).Select(Type).OfType<TypeReference>().ToList()
                : new[] { Type(Get(node, "slice")) }.OfType<TypeReference>().ToList()
            : [];
        string name = Kind(node) == "Subscript" ? Text(Get(node, "value")) : text;
        return new(name, null, arguments, originalText);
    }

    private TypeDeclaration Class(JsonElement node)
    {
        string name = String(node, "name");
        List<string> bases = Items(Get(node, "bases")).Select(Text).ToList();
        List<string> decorators = Items(Get(node, "decorator_list")).Select(Decorator).ToList();
        List<MethodDeclaration> methods = new List<MethodDeclaration>();
        List<MethodDeclaration> constructors = new List<MethodDeclaration>();
        List<TypeDeclaration> nested = new List<TypeDeclaration>();
        List<FieldDeclaration> fields = new List<FieldDeclaration>();
        List<PropertyDeclaration> properties = new List<PropertyDeclaration>();
        List<JsonElement> body = Items(Get(node, "body")).ToList();
        string? doc = Doc(body);
        bool slots = false;
        void DeclareMember(JsonElement child, StatementInfo? parent)
        {
            if (Kind(child) == "ClassDef")
            {
                nested.Add(Class(child));
                ClassExpressions(child, null, parent);
            }
            else if (Kind(child) == "TypeAlias")
            {
                nested.Add(Alias(child));
                foreach (JsonElement expression in Children(child))
                {
                    Walk(expression, null, parent);
                }
            }
            else
            {
                MethodDeclaration method = Function(child);
                DefinitionExpressions(child, null, parent);
                CollectReceiverFields(child, fields);
                (method.Name == "__init__" ? constructors : methods).Add(method);
            }
        }
        foreach (JsonElement child in body)
        {
            CollectFields(child, fields, ref slots);
            Walk(child, null, null, DeclareMember);
        }
        foreach (MethodDeclaration? method in methods.Where(m => m.Decorators.Any(d => d == "property" || d.EndsWith(".property", StringComparison.Ordinal))))
        {
            properties.Add(new(method.Name, method.ReturnType, method.Modifiers, method.Line)
            {
                HasGetter = true, HasDocComment = method.HasDocComment, DocComment = method.DocComment,
                HasSetter = methods.Any(m => m.Decorators.Contains(method.Name + ".setter")),
            });
        }

        bool Base(string value) => bases.Any(b => b == value || b.EndsWith("." + value, StringComparison.Ordinal));
        return new PythonTypeDeclaration(new TypeDeclaration(name, TypeKind.Class, Visibility(name), bases, decorators,
            constructors, methods, nested, [], Line(node))
        {
            Fields = fields.DistinctBy(f => f.Name).ToList(), Properties = properties,
            HasDocComment = doc is not null, DocComment = doc,
            TypeParameters = Items(Get(node, "type_params")).Select(p => String(p, "name")).ToList(),
            Column = Position(node, "column"), EndLine = Position(node, "end_line"), EndColumn = Position(node, "end_column"),
        },
            isAbstract: Base("ABC") || Items(Get(node, "keywords")).Any(k => String(k, "arg") == "metaclass" && Text(Get(k, "value")).EndsWith("ABCMeta", StringComparison.Ordinal))
        )
        {
            IsDataclass = decorators.Any(d => d == "dataclass" || d.EndsWith(".dataclass", StringComparison.Ordinal)),
            IsEnum = new[] { "Enum", "IntEnum", "StrEnum", "Flag", "IntFlag" }.Any(Base),
            IsNamedTuple = Base("NamedTuple"), IsProtocol = Base("Protocol"),
            IsException = bases.Any(b => b.EndsWith("Exception", StringComparison.Ordinal) || b.EndsWith("Error", StringComparison.Ordinal)),
            HasSlots = slots
        };
    }

    private static void CollectFields(JsonElement node, List<FieldDeclaration> fields, ref bool slots)
    {
        if (Kind(node) is "ClassDef" or "FunctionDef" or "AsyncFunctionDef")
        {
            return;
        }

        if (Kind(node) is "Assign" or "AnnAssign")
        {
            IEnumerable<JsonElement> targets = Kind(node) == "Assign" ? Items(Get(node, "targets")) : [Get(node, "target")];
            foreach (JsonElement target in targets)
            {
                foreach (string name in AssignedNames(target))
                {
                    fields.Add(new(name, Type(Get(node, "annotation")), Visibility(name) | Modifier.Static, Line(node)));
                    slots |= name == "__slots__";
                }
            }
        }
        foreach (JsonElement child in Children(node))
        {
            CollectFields(child, fields, ref slots);
        }
    }

    private static IEnumerable<string> AssignedNames(JsonElement node)
    {
        if (Kind(node) == "Name")
        {
            yield return String(node, "id");
        }

        if (Kind(node) is "Tuple" or "List")
        {
            foreach (JsonElement child in Items(Get(node, "elts")))
            {
                foreach (string name in AssignedNames(child))
                {
                    yield return name;
                }
            }
        }
    }

    private static void CollectReceiverFields(JsonElement function, List<FieldDeclaration> fields)
    {
        List<string> decorators = Items(Get(function, "decorator_list")).Select(Decorator).ToList();
        if (decorators.Any(d => d == "staticmethod" || d.EndsWith(".staticmethod", StringComparison.Ordinal)))
        {
            return;
        }

        JsonElement parameters = Get(function, "args");
        JsonElement receiver = Items(Get(parameters, "posonlyargs")).Concat(Items(Get(parameters, "args"))).FirstOrDefault();
        if (receiver.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string name = String(receiver, "arg");
        bool isClass = decorators.Any(d => d == "classmethod" || d.EndsWith(".classmethod", StringComparison.Ordinal));
        void Visit(JsonElement node)
        {
            if (Kind(node) is "ClassDef" or "FunctionDef" or "AsyncFunctionDef" or "Lambda")
            {
                return;
            }

            if (Kind(node) is "Assign" or "AnnAssign")
            {
                IEnumerable<JsonElement> targets = Kind(node) == "Assign" ? Items(Get(node, "targets")) : [Get(node, "target")];
                foreach (JsonElement target in targets)
                {
                    if (Kind(target) == "Attribute" && Kind(Get(target, "value")) == "Name"
                        && String(Get(target, "value"), "id") == name)
                    {
                        string member = String(target, "attr");
                        fields.Add(new(member, Type(Get(node, "annotation")),
                            Visibility(member) | (isClass ? Modifier.Static : Modifier.None), Line(node)));
                    }
                }
            }
            foreach (JsonElement child in Children(node))
            {
                Visit(child);
            }
        }
        foreach (JsonElement statement in Items(Get(function, "body")))
        {
            Visit(statement);
        }
    }

    private static string? Doc(List<JsonElement> body) =>
        body.Count > 0 && Kind(body[0]) == "Expr" && IsString(Get(body[0], "value"))
                ? Get(Get(body[0], "value"), "value").GetString() : null;

    private MethodDeclaration Function(JsonElement node)
    {
        string name = String(node, "name");
        List<string> decorators = Items(Get(node, "decorator_list")).Select(Decorator).ToList();
        Modifier modifiers = Visibility(name);
        if (Kind(node) == "AsyncFunctionDef")
        {
            modifiers |= Modifier.Async;
        }

        if (decorators.Any(d => d == "staticmethod" || d.EndsWith(".staticmethod", StringComparison.Ordinal)))
        {
            modifiers |= Modifier.Static;
        }

        if (decorators.Any(d => d == "abstractmethod" || d.EndsWith(".abstractmethod", StringComparison.Ordinal)))
        {
            modifiers |= Modifier.Abstract;
        }

        List<JsonElement> body = Items(Get(node, "body")).ToList();
        string? doc = Doc(body);
        PythonMethodDeclaration method = new MethodDeclaration(name, modifiers, decorators, Type(Get(node, "returns")),
            Parameters(Get(node, "args")), Line(node))
        {
            HasDocComment = doc is not null, DocComment = doc,
            TypeParameters = Items(Get(node, "type_params")).Select(p => String(p, "name")).ToList(),
            Column = Position(node, "column"), EndLine = Position(node, "end_line"), EndColumn = Position(node, "end_column"),
        }
            .AsPython(isGenerator: body.Any(ContainsYield));
        foreach (JsonElement child in body)
        {
            Walk(child, method, null);
        }
        // Defaults, annotations and decorators execute in the enclosing scope, not the function body.
        return method;
    }

    private static bool ContainsYield(JsonElement node)
    {
        if (Kind(node) is "FunctionDef" or "AsyncFunctionDef" or "ClassDef" or "Lambda")
        {
            return false;
        }

        return Kind(node) is "Yield" or "YieldFrom" || Children(node).Any(ContainsYield);
    }

    private static List<ParameterDeclaration> Parameters(JsonElement arguments)
    {
        List<ParameterDeclaration> result = new List<ParameterDeclaration>();
        List<JsonElement> positional = Items(Get(arguments, "posonlyargs")).Concat(Items(Get(arguments, "args"))).ToList();
        List<JsonElement> defaults = Items(Get(arguments, "defaults")).ToList();
        void Add(JsonElement arg, JsonElement value, bool variadic = false, bool kwargs = false)
        {
            if (arg.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            result.Add(new(String(arg, "arg"), Type(Get(arg, "annotation")), variadic, kwargs,
                value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null), Line(arg))
            {
                DefaultValueText = value.ValueKind == JsonValueKind.Object ? Text(value) : null,
                IsOptional = value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null),
                Column = Position(arg, "column"), EndLine = Position(arg, "end_line"), EndColumn = Position(arg, "end_column"),
            });
        }
        for (int i = 0; i < positional.Count; i++)
        {
            int index = i - (positional.Count - defaults.Count);
            Add(positional[i], index >= 0 ? defaults[index] : default);
        }
        Add(Get(arguments, "vararg"), default, variadic: true);
        List<JsonElement> keywordDefaults = Items(Get(arguments, "kw_defaults")).ToList();
        List<JsonElement> keywords = Items(Get(arguments, "kwonlyargs")).ToList();
        for (int i = 0; i < keywords.Count; i++)
        {
            Add(keywords[i], keywordDefaults[i]);
        }

        Add(Get(arguments, "kwarg"), default, kwargs: true);
        return result;
    }

    private void Walk(JsonElement node, MethodDeclaration? method, StatementInfo? parent,
        Action<JsonElement, StatementInfo?>? declareMember = null)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        string kind = Kind(node);
        if (declareMember is not null && kind is "ClassDef" or "FunctionDef" or "AsyncFunctionDef" or "TypeAlias")
        {
            declareMember(node, parent);
            return;
        }
        if (kind == "ClassDef")
        {
            _types.Add(Class(node));
            ClassExpressions(node, method, parent);
            return;
        }
        if (kind is "FunctionDef" or "AsyncFunctionDef")
        {
            _functions.Add(Function(node));
            DefinitionExpressions(node, method, parent);
            return;
        }
        if (kind == "TypeAlias")
        {
            _types.Add(Alias(node));
        }
        if (kind == "Import")
        {
            _imports.AddRange(Items(Get(node, "names")).Select(n => String(n, "name")));
            foreach (JsonElement alias in Items(Get(node, "names")))
            {
                _importFacts.Add(Import(node, String(alias, "name"), null, String(alias, "asname"), 0));
            }
        }
        if (kind == "ImportFrom")
        {
            _imports.Add(new string('.', Get(node, "level").GetInt32()) + String(node, "module"));
            foreach (JsonElement alias in Items(Get(node, "names")))
            {
                _importFacts.Add(Import(node, String(node, "module"), String(alias, "name"), String(alias, "asname"), Get(node, "level").GetInt32()));
            }
        }

        string? statementKind = kind switch
        {
            "Call" => "call", "If" or "IfExp" => "if", "For" or "AsyncFor" => "foreach",
            "While" => "while", "Try" or "TryStar" => "try", "ExceptHandler" => "catch",
            "With" => "with", "AsyncWith" => "async with", "Raise" => "throw",
            "Return" => "return", "Yield" or "YieldFrom" => "yield", "Assert" => "assert",
            "Global" => "global", "Nonlocal" => "nonlocal", "Break" => "break", "Continue" => "continue",
            "Assign" or "AnnAssign" or "AugAssign" => "assignment",
            "ListComp" or "SetComp" or "DictComp" or "GeneratorExp" => "comprehension",
            "Match" => "switch", _ => null,
        };
        if (statementKind is not null)
        {
            JsonElement target = Get(node, "func");
            List<JsonElement> args = Items(Get(node, "args")).ToList();
            List<JsonElement> kwargs = Items(Get(node, "keywords")).ToList();
            string? typeName = kind switch
            {
                "Call" when Kind(target) == "Attribute" => Text(Get(target, "value")),
                "ExceptHandler" => Kind(Get(node, "type")) == "Tuple"
                    ? Items(Get(Get(node, "type"), "elts")).Select(Text).FirstOrDefault() : Text(Get(node, "type")),
                "Raise" => Kind(Get(node, "exc")) == "Call" ? Text(Get(Get(node, "exc"), "func")) : Text(Get(node, "exc")),
                _ => null,
            };
            if (typeName == string.Empty)
            {
                typeName = null;
            }

            string? member = kind == "Call" ? Kind(target) == "Name" ? String(target, "id")
                : Kind(target) == "Attribute" ? String(target, "attr") : null : null;
            PythonStatementInfo statement = new PythonStatementInfo(new StatementInfo(statementKind, [], typeName, member,
                args.Select(LiteralArgument).Concat(kwargs.Select(k => LiteralArgument(Get(k, "value")))).ToList(),
                Line(node), method is not null))
            {
                Method = method, Parent = parent,
                Column = Position(node, "column"), EndLine = Position(node, "end_line"), EndColumn = Position(node, "end_column"),
                CallTarget = kind == "Call" ? Expression(target) : null,
                ArgumentExpressions = args.Select(Expression).Concat(kwargs.Select(k => new ExpressionInfo(
                    String(k, "arg") == string.Empty ? "spread" : "namedArgument")
                { Name = String(k, "arg") == string.Empty ? null : String(k, "arg"), Children = [Expression(Get(k, "value"))] })).ToList(),
                Condition = kind is "If" or "While" or "Assert" ? Text(Get(node, "test")) : null,
                Expression = Text(Get(node, "value")),
                ValueExpression = Get(node, "value").ValueKind == JsonValueKind.Object ? Expression(Get(node, "value")) : null,
                IsErrorHandler = kind == "ExceptHandler",
                IsGenericErrorHandler = kind == "ExceptHandler" && (typeName is null or "Exception" or "BaseException"),
                HasRethrow = kind == "ExceptHandler" && Items(Get(node, "body")).Any(ContainsRethrow),
            };
            _statements.Add(statement);
            method?.Statements.Add(statement);
            parent?.MutableChildren.Add(statement);
            parent = statement;
        }
        foreach (JsonElement child in Children(node))
        {
            Walk(child, method, parent, declareMember);
        }
    }

    private static ImportInfo Import(JsonElement node, string module, string? name, string alias, int level) =>
        new(module, Line(node))
        {
            Name = name, Alias = alias == string.Empty ? null : alias, RelativeLevel = level, IsWildcard = name == "*",
            Column = Position(node, "column"), EndLine = Position(node, "end_line"), EndColumn = Position(node, "end_column"),
        };

    private static TypeDeclaration Alias(JsonElement node) =>
        new TypeDeclaration(String(Get(node, "name"), "id"), TypeKind.Alias,
            Visibility(String(Get(node, "name"), "id")), [], [], [], [], [], [], Line(node))
        {
            AliasTarget = Type(Get(node, "value")),
            TypeParameters = Items(Get(node, "type_params")).Select(p => String(p, "name")).ToList(),
            Column = Position(node, "column"), EndLine = Position(node, "end_line"), EndColumn = Position(node, "end_column"),
        }.AsPython();

    private void DefinitionExpressions(JsonElement node, MethodDeclaration? method, StatementInfo? parent)
    {
        foreach (JsonElement decorator in Items(Get(node, "decorator_list")))
        {
            Walk(decorator, method, parent);
        }

        foreach (JsonElement child in Children(Get(node, "args")))
        {
            Walk(child, method, parent);
        }

        Walk(Get(node, "returns"), method, parent);
        foreach (JsonElement parameter in Items(Get(node, "type_params")))
        {
            Walk(parameter, method, parent);
        }
    }

    private void ClassExpressions(JsonElement node, MethodDeclaration? method, StatementInfo? parent)
    {
        foreach (JsonElement decorator in Items(Get(node, "decorator_list")))
        {
            Walk(decorator, method, parent);
        }

        foreach (JsonElement baseType in Items(Get(node, "bases")))
        {
            Walk(baseType, method, parent);
        }

        foreach (JsonElement keyword in Items(Get(node, "keywords")))
        {
            Walk(keyword, method, parent);
        }

        foreach (JsonElement parameter in Items(Get(node, "type_params")))
        {
            Walk(parameter, method, parent);
        }
    }

    private static bool ContainsRethrow(JsonElement node) =>
        Kind(node) is not ("FunctionDef" or "AsyncFunctionDef" or "ClassDef")
        && (Kind(node) == "Raise" && Get(node, "exc").ValueKind == JsonValueKind.Null || Children(node).Any(ContainsRethrow));

    private static string LiteralArgument(JsonElement node) =>
        IsString(node)
            ? Get(node, "value").GetString()! : Text(node);

    private static ExpressionInfo Expression(JsonElement node)
    {
        string kind = Kind(node);
        return kind switch
        {
            "Name" => new("identifier") { Name = String(node, "id") },
            "Attribute" => new("memberAccess") { Name = String(node, "attr"), Target = Expression(Get(node, "value")) },
            "Call" => new("invocation") { Target = Expression(Get(node, "func")), Children =
                Items(Get(node, "args")).Select(Expression).Concat(Items(Get(node, "keywords")).Select(k => Expression(Get(k, "value")))).ToList() },
            "Constant" when IsString(node) =>
                new("stringLiteral") { StringValue = Get(node, "value").GetString() },
            "Starred" => new("spread") { Children = [Expression(Get(node, "value"))] },
            "Constant" => new("literal"),
            "List" or "Tuple" or "Set" => new("collection") { Children = Items(Get(node, "elts")).Select(Expression).ToList() },
            _ => new("other") { Children = Children(node).Select(Expression).ToList() },
        };
    }

    private static bool IsString(JsonElement node) => Kind(node) == "Constant" && String(node, "constant_type") == "str";

    private static IEnumerable<JsonElement> Children(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        foreach (JsonProperty property in node.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                yield return property.Value;
            }
            else if (property.Value.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in property.Value.EnumerateArray())
                {
                    if (child.ValueKind == JsonValueKind.Object)
                    {
                        yield return child;
                    }
                }
            }
        }
    }
}
