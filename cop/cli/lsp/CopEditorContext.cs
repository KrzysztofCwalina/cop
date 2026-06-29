using System.Text.RegularExpressions;
using Cop.Lang.Ast;
using Cop.Lang.Interpreter;

namespace Cop.Cli.Lsp;

/// <summary>
/// Lightweight, position-aware text helpers shared by the hover and completion providers: word
/// extraction, line splitting, and resolving the in-scope locals (predicate/function parameters and
/// the implicit <c>item</c>) at a cursor. This is editor plumbing only — all type/semantic answers
/// come from <see cref="SemanticModel"/>.
/// </summary>
internal static class CopEditorContext
{
    private static readonly Regex ItemChain =
        new(@"([A-Za-z_][A-Za-z0-9_.:()'\-]*)\s*:\s*to(?:Error|Warning|Info|Violation)\s*\(", RegexOptions.Compiled);

    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    public static (string? word, int start) WordAt(string lineText, int ch)
    {
        if (ch < 0) ch = 0;
        if (ch > lineText.Length) ch = lineText.Length;
        int start = ch, end = ch;
        while (start > 0 && IsWordChar(lineText[start - 1])) start--;
        while (end < lineText.Length && IsWordChar(lineText[end])) end++;
        return start == end ? (null, start) : (lineText[start..end], start);
    }

    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    /// <summary>Resolves the in-scope locals at <paramref name="line"/>: the enclosing predicate/function
    /// parameters plus the implicit <c>item</c> (from an enclosing <c>:toError(...)</c> chain).</summary>
    public static Dictionary<string, TypeInfo> BuildLocals(
        SemanticModel model, ModuleNode? fileModule, string[] lines, int line)
    {
        var locals = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);

        var enclosing = EnclosingFunction(fileModule, line);
        if (enclosing is not null)
            foreach (var prm in enclosing.Params)
                if (prm.Type is not null)
                    locals[prm.Name] = new TypeInfo(prm.Type.Name, prm.Type.IsCollection);

        for (int i = line; i >= Math.Max(0, line - 6) && i < lines.Length; i--)
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
}
