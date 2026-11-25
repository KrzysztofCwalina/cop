// Projects lint and type-checker suppression comments as directive statements.

using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class JavaScriptDirectives
{
    private static readonly string[] s_eslintNames = ["eslint-disable-next-line", "eslint-disable-line", "eslint-disable"];
    private static readonly string[] s_typeScriptNames = ["@ts-expect-error", "@ts-nocheck", "@ts-ignore"];

    /// <summary>
    /// Keywords are the directive name followed by named rules; a directive without rules suppresses everything.
    /// </summary>
    public static IEnumerable<StatementInfo> Extract(IEnumerable<JsToken> tokens)
    {
        foreach (JsToken token in tokens)
        {
            if (token.Kind is not (JsTok.LineComment or JsTok.BlockComment))
            {
                continue;
            }

            string body = token.Value.StartsWith("//", StringComparison.Ordinal) ? token.Value[2..]
                : token.Value.StartsWith("/*", StringComparison.Ordinal) ? token.Value[2..^2].Trim('*') : token.Value;
            body = body.Trim();
            if (MatchName(body, s_eslintNames, out string eslint, out string rest) &&
                (rest.Length == 0 || char.IsWhiteSpace(rest[0]) || rest.StartsWith("--", StringComparison.Ordinal)))
            {
                int description = rest.IndexOf("--", StringComparison.Ordinal);
                string rules = description >= 0 ? rest[..description] : rest;
                List<string> keywords = [eslint];
                keywords.AddRange(rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                yield return new StatementInfo("directive", keywords, null, null, [], token.Line, false);
                continue;
            }

            if (MatchName(body, s_typeScriptNames, out string typescript, out string after) &&
                (after.Length == 0 || !(char.IsLetterOrDigit(after[0]) || after[0] == '_')))
            {
                yield return new StatementInfo("directive", [typescript], null, null, [], token.Line, false);
            }
        }
    }

    // Names are ordered longest-first so a prefix never shadows a longer directive.
    private static bool MatchName(string body, string[] names, out string name, out string rest)
    {
        foreach (string candidate in names)
        {
            if (body.StartsWith(candidate, StringComparison.Ordinal))
            {
                name = candidate;
                rest = body[candidate.Length..];
                return true;
            }
        }

        name = rest = "";
        return false;
    }
}
