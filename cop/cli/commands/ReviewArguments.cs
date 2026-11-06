// Parses change-review options shared by commands that inspect source revisions.

using Cop.Core;

namespace Cop.Cli.Commands;

internal static class ReviewArguments
{
    /// <summary>Separates review options from the remaining program arguments.</summary>
    public static (string[] Arguments, ReviewOptions Options) Parse(string[] args)
    {
        List<string> remaining = new List<string>();
        Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] names = ["--scope", "--base", "--pr", "--report-scope", "--ai-config"];
        for (int i = 0; i < args.Length; i++)
        {
            int separator = args[i].IndexOf('=');
            string name = separator < 0 ? args[i] : args[i][..separator];
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                remaining.Add(args[i]);
                continue;
            }

            if (values.ContainsKey(name))
            {
                throw new ArgumentException($"Option '{name}' may only be specified once.");
            }

            string value = separator >= 0 ? args[i][(separator + 1)..] :
                i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : "";
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException($"Option '{name}' requires a value.");
            }

            values.Add(name, value);
        }

        ReviewOptions options = new ReviewOptions
        {
            Scope = values.GetValueOrDefault("--scope", "auto"),
            Base = values.GetValueOrDefault("--base"),
            Pr = values.GetValueOrDefault("--pr"),
            ReportScope = values.GetValueOrDefault("--report-scope", "all"),
            AiConfigPath = values.GetValueOrDefault("--ai-config")
        };
        options.Validate();
        return ([.. remaining], options);
    }
}
