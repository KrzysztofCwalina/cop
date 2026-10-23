using System.Text.Json;
using Cop.Core;
using Cop.Lang;

namespace Cop.Providers.Ai;

public partial class AiProvider
{
    private static string MarkdownRequirements(object? input, object? levelValue)
    {
        int level = levelValue switch
        {
            int value => value,
            long value when value is >= 1 and <= 6 => (int)value,
            _ => throw new ArgumentException("Markdown rule level must be an integer from 1 to 6")
        };
        if (level is < 1 or > 6)
            throw new ArgumentException("Markdown rule level must be an integer from 1 to 6");
        if (input is not DataObject document || document.TypeName != "MarkdownContent"
            || document.GetField("Documents") is not System.Collections.IEnumerable)
            throw new ArgumentException("expected MarkdownContent from markdown.parse() for the three-argument overload");

        var rules = new List<object>();
        foreach (var root in Items(document, "Documents"))
        {
            if (Member(root, "Kind") as string != "document")
                throw new ArgumentException("Markdown Documents must contain document roots");
            var path = FilePath(Member(root, "File"));
            Visit(root, [], path, 0);
        }
        if (rules.Count == 0)
            throw new ArgumentException($"no Markdown rule sections found at heading level {level}");

        return "Evaluate EVERY rule below as a separate logical requirement in this combined review. " +
            "Each selected heading and its COMPLETE section body form ONE rule, regardless of paragraph count. " +
            "Parent headings and their context constrain the rule. Nested headings, lists, and code examples " +
            "belong to the enclosing rule; do not treat them as additional rules. " +
            "Identify the relevant rule title in each finding's message. " +
            "Rule source locations identify the policy document, not defect locations: " +
            "report findings against the code under review, never against the policy file.\n\n" +
            JsonSerializer.Serialize(new { ruleLevel = level, rules });

        void Visit(object node, List<object> ancestors, string path, int depth)
        {
            if (depth > 128)
                throw new ArgumentException("Markdown rule tree exceeds the supported nesting depth");
            var children = Items(node, "Children").ToList();
            foreach (var child in children)
            {
                if (Member(child, "Kind") as string != "heading") continue;
                if (Member(child, "Text") is not string title || string.IsNullOrWhiteSpace(title)
                    || Member(child, "Content") is not string content || string.IsNullOrWhiteSpace(content))
                    throw new ArgumentException("Markdown rule headings require a title and complete section content");
                int headingLevel = LineNumber(child, "Level");
                if (headingLevel is < 1 or > 6)
                    throw new ArgumentException("Markdown heading levels must be from 1 to 6");
                if (headingLevel == level)
                {
                    if (!Items(child, "Children").Any())
                        throw new ArgumentException($"Markdown rule '{title}' has no section body");
                    rules.Add(new
                    {
                        id = rules.Count + 1,
                        source = path,
                        line = LineNumber(child, "StartLine"),
                        title,
                        context = ancestors.Append(Context(node)).ToArray(),
                        requirement = content
                    });
                }
                else if (headingLevel < level)
                {
                    Visit(child, ancestors.Append(Context(node)).ToList(), path, depth + 1);
                }
            }
        }

        static object Context(object node) => new
        {
            title = Member(node, "Text") as string ?? "",
            content = Items(node, "Children")
                .Where(child => Member(child, "Kind") as string != "heading")
                .Select(child => Member(child, "Content") as string
                    ?? throw new ArgumentException("Markdown context blocks require source content"))
                .ToArray()
        };
    }
}
