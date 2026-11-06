// Captures standard Rust toolchain inputs when callers inherit the process environment.

using System.Collections;

namespace Cop.Providers.SourceParsers;

internal static class RustToolchainEnvironment
{
    internal static IReadOnlyDictionary<string, string?> Capture(
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        Dictionary<string, string?> values = new(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                values[name] = value;
            }
        }
        if (overrides is not null)
        {
            foreach (KeyValuePair<string, string?> entry in overrides)
            {
                values[entry.Key] = entry.Value;
            }
        }

        return values;
    }
}
