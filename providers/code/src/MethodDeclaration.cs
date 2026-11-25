// Represents callable declarations, signatures, and statement bodies.

namespace Cop.Providers.SourceModel;

/// <summary>
/// A parsed method signature with its body and language-neutral modifiers.
/// </summary>
public record MethodDeclaration(
    string Name,
    Modifier Modifiers,
    List<string> Decorators,
    TypeReference? ReturnType,
    List<ParameterDeclaration> Parameters,
    int Line)
{
    /// <summary>Parsed callable category; ordinary method declarations use "method".</summary>
    public string Kind { get; init; } = "method";

    /// <summary>
    /// The bound type of the invocation receiver, when available.
    /// </summary>
    public string? ReceiverType { get; init; }
    /// <summary>
    /// The generic type-parameter names in declaration order.
    /// </summary>
    public List<string> TypeParameters { get; init; } = [];
    /// <summary>
    /// The one-based source column, or null when the parser cannot provide it.
    /// </summary>
    public int? Column { get; init; }
    /// <summary>
    /// The one-based ending source line, when available.
    /// </summary>
    public int? EndLine { get; init; }
    /// <summary>
    /// The one-based exclusive ending source column, when available.
    /// </summary>
    public int? EndColumn { get; init; }
    /// <summary>
    /// True when the declaration has public accessibility.
    /// </summary>
    public bool IsPublic => Modifiers.HasFlag(Modifier.Public);
    /// <summary>
    /// True when the declaration has protected accessibility.
    /// </summary>
    public bool IsProtected => Modifiers.HasFlag(Modifier.Protected);
    /// <summary>
    /// True when the method has the async modifier.
    /// </summary>
    public bool IsAsync => Modifiers.HasFlag(Modifier.Async);
    /// <summary>
    /// True when the declaration is static.
    /// </summary>
    public bool IsStatic => Modifiers.HasFlag(Modifier.Static);
    /// <summary>
    /// True when the declaration is abstract.
    /// </summary>
    public bool IsAbstract => Modifiers.HasFlag(Modifier.Abstract);
    /// <summary>
    /// True when the declaration has the virtual modifier.
    /// </summary>
    public bool IsVirtual => Modifiers.HasFlag(Modifier.Virtual);
    /// <summary>
    /// True when the declaration overrides an inherited member.
    /// </summary>
    public bool IsOverride => Modifiers.HasFlag(Modifier.Override);
    /// <summary>
    /// True when the declaration has private accessibility.
    /// </summary>
    public bool IsPrivate => Modifiers.HasFlag(Modifier.Private);
    /// <summary>
    /// True when the declaration has internal accessibility.
    /// </summary>
    public bool IsInternal => Modifiers.HasFlag(Modifier.Internal);
    /// <summary>
    /// The statements belonging to this declaration or cache graph.
    /// </summary>
    public List<StatementInfo> Statements { get; set; } = [];
    /// <summary>
    /// True when a documentation comment is attached to the declaration.
    /// </summary>
    public bool HasDocComment { get; init; }
    /// <summary>
    /// The documentation comment attached to the declaration.
    /// </summary>
    public string? DocComment { get; init; }

    /// <summary>The source file declaring this method. Set during reference linking.</summary>
    public SourceFile? File { get; set; }

    /// <summary>Stable identity string for this method (file path + name).</summary>
    public string Source => File is null ? Name : $"{File.Path}:{Name}";

    /// <summary>
    /// Order-sensitive digest of parsed body structure, including nested statements and conditions: kinds, keywords, called
    /// syntactic type/member names, argument counts, and literal and member-access values in arguments and assigned
    /// values. Local variable names are excluded, so renamed copies of the same logic share a shape while
    /// data-driven variants that differ in literals do not. Empty when the body has no statements.
    /// </summary>
    public string Shape
    {
        get
        {
            if (Statements.Count == 0)
            {
                return string.Empty;
            }

            System.Text.StringBuilder text = new();
            foreach (StatementInfo statement in BodyStatements())
            {
                bool namesLocal = statement.Kind is "declaration" or "assignment";
                int depth = 0;
                for (StatementInfo? parent = statement.Parent; parent is not null && depth < 64; parent = parent.Parent)
                {
                    depth++;
                }
                text.Append(depth).Append('|').Append(statement.Kind).Append('|')
                    .AppendJoin(',', statement.Keywords).Append('|')
                    .Append(namesLocal ? null : statement.SyntaxTypeNameValue).Append('|')
                    .Append(namesLocal ? null : statement.MemberName).Append('|')
                    .Append(statement.ArgumentExpressions.Count).Append('|');
                foreach (ExpressionInfo argument in statement.ArgumentExpressions)
                {
                    AppendValue(text, argument);
                }
                AppendValue(text, statement.ValueExpression);
                AppendValue(text, statement.ConditionExpression);
                text.Append('\n');
            }
            byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
            return Convert.ToHexString(hash, 0, 8);
        }
    }

    private IEnumerable<StatementInfo> BodyStatements()
    {
        HashSet<StatementInfo> visited = new(ReferenceEqualityComparer.Instance);
        Stack<StatementInfo> pending = new(Statements.AsEnumerable().Reverse());
        while (pending.TryPop(out StatementInfo? statement))
        {
            if (!visited.Add(statement))
            {
                continue;
            }
            yield return statement;
            foreach (StatementInfo child in statement.Children.Reverse())
            {
                pending.Push(child);
            }
        }
    }

    private static void AppendValue(System.Text.StringBuilder text, ExpressionInfo? expression)
    {
        if (expression is null)
        {
            return;
        }

        text.Append(expression.Kind).Append(':');
        if (expression.Kind != "identifier")
        {
            string? value = expression.StringValue ?? expression.Name;
            text.Append(value?.Length ?? 0).Append(':').Append(value).Append(expression.BooleanValue);
        }
        AppendValue(text, expression.Target);
        foreach (ExpressionInfo child in expression.Children)
        {
            AppendValue(text, child);
        }
        text.Append(';');
    }

    /// <summary>Lines spanned by the declaration, when the parser reports its end line.</summary>
    public int? LineCount => EndLine is int end && end >= Line ? end - Line + 1 : null;

    /// <summary>Comment lines inside the declaration span, when the parser reports its end line.</summary>
    public int? CommentLineCount => EndLine is int end && File is not null
        ? File.CommentLines.Count(line => line >= Line && line <= end) : null;

    /// <summary>
    /// Language-specific subtype tag for disk-cache round-tripping (e.g. "csharp"). Null for
    /// the language-agnostic base method. Subclasses that add language-specific fields override
    /// this together with <see cref="LanguageFlags"/> and register a reconstruction factory via
    /// <see cref="MethodTypeRegistry"/>.
    /// </summary>
    public virtual string? LanguageTag => null;

    /// <summary>Language-specific boolean facts serialized alongside the base method. Null for the base.</summary>
    public virtual IReadOnlyList<KeyValuePair<string, bool>>? LanguageFlags => null;
}
