using Cop.Providers.SourceModel;

namespace Cop.Providers.SourceParsers;

internal static class RustTypeReferences
{
    public static TypeReference Parse(string text, Action onNestingLimit, int depth = 0)
    {
        text = text.Trim();
        if (depth >= 128)
        {
            onNestingLimit();
            return new TypeReference(text, null, [], text);
        }
        int angle = text.IndexOf('<');
        string path = angle >= 0 ? text[..angle].Trim() : text;
        int separator = path.LastIndexOf("::", StringComparison.Ordinal);
        string name = separator >= 0 ? path[(separator + 2)..] : path;
        var arguments = new List<TypeReference>();
        if (angle >= 0 && text.EndsWith('>'))
        {
            string inner = text[(angle + 1)..^1];
            int nesting = 0, start = 0;
            for (int i = 0; i <= inner.Length; i++)
            {
                if (i == inner.Length || (inner[i] == ',' && nesting == 0))
                {
                    if (i > start) arguments.Add(Parse(inner[start..i], onNestingLimit, depth + 1));
                    start = i + 1;
                }
                else if (inner[i] is '<' or '(' or '[' or '{') nesting++;
                else if (inner[i] is '>' or ')' or ']' or '}') nesting--;
            }
        }
        return new TypeReference(name, separator >= 0 ? path[..separator] : null, arguments, text);
    }
}
