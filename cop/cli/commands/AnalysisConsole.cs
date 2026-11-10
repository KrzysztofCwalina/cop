// Formats analysis phase status and package-restoration messages on stderr.

namespace Cop.Cli.Commands;

internal static class AnalysisConsole
{
    internal static bool UseColor(bool isErrorRedirected, bool noColor) =>
        !isErrorRedirected && !noColor;

    internal static string Format(string phase, string message, bool useColor)
    {
        string label = phase.PadRight(8);
        if (useColor)
        {
            string color = phase == "Restored" ? ConsoleMarkdown.Green : ConsoleMarkdown.Cyan;
            label = color + label + ConsoleMarkdown.Reset;
        }
        return $"  {label}  {message}";
    }

    internal static void Write(string phase, string message) =>
        Console.Error.WriteLine(Format(phase, message,
            UseColor(Console.IsErrorRedirected, ConsoleMarkdown.NoColor)));

    internal static void Restoring(int feeds) =>
        Write("Packages", $"Restoring packages from {feeds} feed{(feeds == 1 ? "" : "s")} " +
            $"for Cop {Cop.Core.ProviderCompatibility.HostVersion}");

    internal static void PackageRestored(string package, string feed) =>
        Write("Restored", $"{package} ({feed})");

    internal static void RestoreFailed(string package, string feed, string reason) =>
        Console.Error.WriteLine($"Warning: Cannot restore '{package}' from {feed}: {reason}");
}
