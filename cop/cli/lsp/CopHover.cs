using System.Text;
using System.Text.RegularExpressions;
using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;

namespace Cop.Cli.Lsp;

/// <summary>
/// Produces hover markdown for a position in a cop document by querying the real compiler's
/// <see cref="SemanticModel"/> (and <see cref="LanguageMetadata"/> for built-ins). This replaces the
/// JavaScript hover reimplementation in the extension — the editor now shows exactly what the
/// compiler knows, so it can never drift (e.g. a union of violation lists shows <c>[Violation]</c>,
/// not "unknown").
/// </summary>
internal static class CopHover
{
    private static readonly Regex DotChain =
        new(@"([A-Za-z_][A-Za-z0-9_.:()'\-]*)\.\s*$", RegexOptions.Compiled);

    private static readonly Regex ItemChain =
        new(@"([A-Za-z_][A-Za-z0-9_.:()'\-]*)\s*:\s*to(?:Error|Warning|Info|Violation)\s*\(", RegexOptions.Compiled);

    /// <summary>Returns hover markdown for the token at (<paramref name="line"/>, <paramref name="character"/>),
    /// 0-based, or null when there is nothing confident to show.</summary>
    public static string? Hover(SemanticModel model, ModuleNode? fileModule, string text, int line, int character)
    {
        var lines = SplitLines(text);
        if (line < 0 || line >= lines.Length) return null;
        var lineText = lines[line];
        var (word, start) = WordAt(lineText, character);
        if (word is null) return null;
        var before = lineText[..start];

        var locals = BuildLocals(model, fileModule, lines, line);

        // 1. Declared predicate or function.
        var callable = model.Callable(word);
        if (callable is not null) return CallableHover(callable);

        // 2. Top-level `let` binding.
        if (model.IsLet(word))
        {
            var t = model.LetType(word);
            return Code($"let {word}: {t?.Display ?? "unknown"}");
        }

        // 3. Property in a dot-chain (e.g. `codebase.Types.Name`).
        if (before.TrimEnd().EndsWith('.'))
        {
            var m = DotChain.Match(before);
            if (m.Success)
            {
                var parent = model.InferExpressionType(m.Groups[1].Value, locals);
                if (parent is not null)
                {
                    // A collection projects to its element type; a scalar uses its own type.
                    var owner = parent.Value.Name;
                    var prop = model.PropertyOf(owner, word);
                    if (prop is not null)
                        return Code($"(property) {prop.DeclaringType}.{prop.Name}: {prop.Type}");
                    var builtin = BuiltinProperty(parent.Value, word);
                    if (builtin is not null) return Code(builtin);
                }
            }
        }

        // 4. Implicit `item`.
        if (word == "item" && locals.TryGetValue("item", out var itemType))
            return Code($"(variable) item: {itemType.Display}");

        // 5. Type name.
        if (model.IsKnownType(word)) return TypeHover(model, word);

        // 6. Predicate/function parameter in scope.
        if (locals.TryGetValue(word, out var paramType))
            return Code($"(parameter) {word}: {paramType.Display}");

        // 7. Keyword.
        foreach (var kw in LanguageMetadata.Keywords)
            if (kw.Name == word)
                return Code($"(keyword) {word}") + "\n\n" + kw.Detail;

        return null;
    }

    private static string CallableHover(CallableInfo c)
    {
        var pars = string.Join(", ", c.ParamTypes.Select(p => p ?? "?"));
        if (c.IsPredicate)
        {
            var ret = c.IsNarrowing && c.ReturnType is not null ? c.ReturnType : "bool";
            return Code($"predicate {c.Name}({pars}) => {ret}");
        }
        return c.ReturnType is not null
            ? Code($"function {c.Name}({pars}) => {c.ReturnType}")
            : Code($"function {c.Name}({pars})");
    }

    private static string TypeHover(SemanticModel model, string name)
    {
        var sb = new StringBuilder();
        sb.Append(Code($"type {name}"));
        var props = model.PropertiesOf(name);
        if (props.Count > 0)
        {
            sb.Append("\n\n**Properties:**\n");
            sb.Append(CodeBlock(string.Join("\n", props.Select(p => $"  {p.Name}: {p.Type}"))));
        }
        return sb.ToString();
    }

    /// <summary>Hover for a built-in string/collection property (e.g. <c>.Length</c>, <c>.Count</c>).</summary>
    private static string? BuiltinProperty(TypeInfo parent, string word)
    {
        if (parent.IsCollection)
        {
            var e = Array.Find(LanguageMetadata.CollectionProperties, p => p.Name == word);
            if (e.Name == word) return $"(property) [{parent.Name}].{word}{Detail(e.Detail)}";
        }
        if (SemanticModel.IsStringType(parent.Display))
        {
            var e = Array.Find(LanguageMetadata.StringProperties, p => p.Name == word);
            if (e.Name == word) return $"(property) string.{word}{Detail(e.Detail)}";
        }
        return null;
    }

    /// <summary>Builds the in-scope local types: predicate/function parameters plus the implicit
    /// <c>item</c> (resolved from an enclosing <c>:toError(...)</c> chain when present).</summary>
    private static Dictionary<string, TypeInfo> BuildLocals(
        SemanticModel model, ModuleNode? fileModule, string[] lines, int line)
    {
        var locals = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);

        var enclosing = EnclosingFunction(fileModule, line);
        if (enclosing is not null)
            foreach (var prm in enclosing.Params)
                if (prm.Type is not null)
                    locals[prm.Name] = new TypeInfo(prm.Type.Name, prm.Type.IsCollection);

        // `item` is the element of the collection being mapped in a `:toError(...)` (and siblings).
        for (int i = line; i >= Math.Max(0, line - 6); i--)
        {
            var m = ItemChain.Match(lines[i]);
            if (!m.Success) continue;
            var chainType = model.InferExpressionType(m.Groups[1].Value, locals);
            if (chainType is null) break;
            locals["item"] = chainType.Value.IsCollection
                ? new TypeInfo(chainType.Value.Name, false)
                : chainType.Value;
            break;
        }
        return locals;
    }

    private static FunctionDecl? EnclosingFunction(ModuleNode? module, int line)
    {
        if (module is null) return null;
        FunctionDecl? best = null;
        foreach (var decl in module.Declarations)
            if (decl is FunctionDecl fn && fn.Line <= line + 1 && (best is null || fn.Line > best.Line))
                best = fn;
        return best;
    }

    private static (string? word, int start) WordAt(string lineText, int ch)
    {
        if (ch < 0) ch = 0;
        if (ch > lineText.Length) ch = lineText.Length;
        int start = ch, end = ch;
        while (start > 0 && IsWordChar(lineText[start - 1])) start--;
        while (end < lineText.Length && IsWordChar(lineText[end])) end++;
        return start == end ? (null, start) : (lineText[start..end], start);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    private static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static string Detail(string detail) => string.IsNullOrEmpty(detail) ? "" : $": {detail}";

    private static string Code(string code) => $"```cop\n{code}\n```";

    private static string CodeBlock(string code) => $"```cop\n{code}\n```";
}
