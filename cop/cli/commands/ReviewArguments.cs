using Cop.Core;

namespace Cop.Cli.Commands;

internal static class ReviewArguments
{
    public static (string[] Arguments, ReviewOptions Options) Parse(string[] args)
    {
        var remaining = new List<string>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] names = ["--scope", "--base", "--pr", "--report-scope", "--ai-config"];
        for (int i = 0; i < args.Length; i++)
        {
            var separator = args[i].IndexOf('=');
            var name = separator < 0 ? args[i] : args[i][..separator];
            if (!names.Contains(name, StringComparer.Ordinal))
            {
                remaining.Add(args[i]);
                continue;
            }

            if (values.ContainsKey(name))
                throw new ArgumentException($"Option '{name}' may only be specified once.");
            var value = separator >= 0 ? args[i][(separator + 1)..] :
                i + 1 < args.Length && !args[i + 1].StartsWith('-') ? args[++i] : "";
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Option '{name}' requires a value.");
            values.Add(name, value);
        }

        var options = new ReviewOptions
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
