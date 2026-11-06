// Captures source-position context used by Cop editor features.

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
    /// <summary>Splits editor text while normalizing supported line endings.</summary>
    public static string[] SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    /// <summary>Returns the identifier touching the cursor and its starting column.</summary>
    public static (string? word, int start) WordAt(string lineText, int ch)
    {
        if (ch < 0)
        {
            ch = 0;
        }

        if (ch > lineText.Length)
        {
            ch = lineText.Length;
        }

        int start = ch, end = ch;
        while (start > 0 && IsWordChar(lineText[start - 1]))
        {
            start--;
        }

        while (end < lineText.Length && IsWordChar(lineText[end]))
        {
            end++;
        }

        return start == end ? (null, start) : (lineText[start..end], start);
    }

    /// <summary>Whether a character can belong to a Cop identifier.</summary>
    public static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '-';

    /// <summary>Finds the qualified member chain immediately before a delimiter.</summary>
    public static string? ChainBefore(string text, char delimiter)
    {
        int end = text.Length;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        if (end == 0 || text[end - 1] != delimiter)
        {
            return null;
        }

        end--;
        while (end > 0 && char.IsWhiteSpace(text[end - 1]))
        {
            end--;
        }

        int start = end;
        while (start > 0 && IsChainChar(text[start - 1]))
        {
            start--;
        }

        while (start < end && !IsIdentifierStart(text[start]))
        {
            start++;
        }

        return start == end ? null : text[start..end];
    }

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c) || c == '_';

    private static bool IsChainChar(char c) =>
        IsIdentifierStart(c) || char.IsAsciiDigit(c) || c is '.' or ':' or '(' or ')' or '\'' or '-';

    private static string? ViolationChain(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != ':')
            {
                continue;
            }

            int start = i + 1;
            while (start < text.Length && char.IsWhiteSpace(text[start]))
            {
                start++;
            }

            int end = start;
            while (end < text.Length && char.IsAsciiLetter(text[end]))
            {
                end++;
            }

            string name = text[start..end];
            if (name is not ("toError" or "toWarning" or "toInfo" or "toViolation"))
            {
                continue;
            }

            while (end < text.Length && char.IsWhiteSpace(text[end]))
            {
                end++;
            }

            if (end < text.Length && text[end] == '(' && ChainBefore(text[..(i + 1)], ':') is { } chain)
            {
                return chain;
            }
        }
        return null;
    }

    /// <summary>Resolves the in-scope locals at <paramref name="line"/>: the enclosing predicate/function
    /// parameters plus the implicit <c>item</c> (from an enclosing <c>:toError(...)</c> chain).</summary>
    public static Dictionary<string, TypeInfo> BuildLocals(
        SemanticModel model, ModuleNode? fileModule, string[] lines, int line)
    {
        Dictionary<string, TypeInfo> locals = new Dictionary<string, TypeInfo>(StringComparer.Ordinal);

        FunctionDecl? enclosing = EnclosingFunction(fileModule, line);
        if (enclosing is not null)
        {
            foreach (Parameter prm in enclosing.Params)
            {
                if (prm.Type is not null)
                {
                    locals[prm.Name] = new TypeInfo(prm.Type.Name, prm.Type.IsCollection);
                }
            }
        }

        for (int i = line; i >= Math.Max(0, line - 6) && i < lines.Length; i--)
        {
            string? chain = ViolationChain(lines[i]);
            if (chain is null)
            {
                continue;
            }

            TypeInfo? chainType = model.InferExpressionType(chain, locals);
            if (chainType is null)
            {
                break;
            }

            locals[Evaluator.ImplicitItemVariable] = chainType.Value.IsCollection
                ? new TypeInfo(chainType.Value.Name, false)
                : chainType.Value;
            break;
        }
        return locals;
    }

    private static FunctionDecl? EnclosingFunction(ModuleNode? module, int line)
    {
        if (module is null)
        {
            return null;
        }

        FunctionDecl? best = null;
        foreach (Declaration decl in module.Declarations)
        {
            if (decl is FunctionDecl fn && fn.Line <= line + 1 && (best is null || fn.Line > best.Line))
            {
                best = fn;
            }
        }

        return best;
    }
}
