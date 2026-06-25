using System.Linq;

namespace Cop.Cli.Commands;

/// <summary>
/// Renders the CLI's help / usage / getting-started screens with a single, consistent,
/// lower-case design. Colour is applied with <see cref="Console.ForegroundColor"/> (not ANSI
/// escapes) so it renders correctly in every terminal — including legacy Windows consoles that
/// don't enable virtual-terminal processing — and is automatically suppressed when output is
/// redirected or <c>--no-color</c> is passed (via <see cref="ConsoleMarkdown.UseColor"/>).
/// </summary>
internal static class CliHelp
{
    private const ConsoleColor Accent = ConsoleColor.Cyan;   // commands, title
    private const ConsoleColor Label = ConsoleColor.DarkGray; // section labels, hints

    private static bool Color => ConsoleMarkdown.UseColor;

    /// <summary>Full help, shown for <c>cop -h</c> and <c>cop help</c>.</summary>
    public static void PrintMainHelp()
    {
        Title();
        Section("usage");
        Rows(
            ("cop run <package>", "run a package from a feed (auto-restores)"),
            ("cop <file.cop>", "run a local .cop file"),
            ("cop package list", "browse available packages"),
            ("cop help language", "full language reference"),
            ("cop help <package>", "package documentation"),
            ("cop init", "generate copilot instructions (--claude for claude code)"),
            ("cop update", "update cop to the latest release"),
            ("cop vscode", "install vs code extension"),
            ("cop test [<file>]", "run tests"),
            ("cop verify [<path>]", "verify program correctness"),
            ("cop repl", "interactive repl"));
        Section("options");
        Rows(
            ("-t <dir>", "target directory"),
            ("-p <provider>", "load a provider package (repeatable)"),
            ("-c <commands>", "filter to specific commands (comma-separated)"),
            ("-f <format>", "output format: text or json"),
            ("-h", "show help"),
            ("-v", "show version"));
        Console.WriteLine();
    }

    /// <summary>Short, friendly intro shown for a bare <c>cop</c> (or <c>cop run</c>) with no local .cop files.</summary>
    public static void PrintGettingStarted()
    {
        Title();
        Section("usage");
        Rows(
            ("cop run <package>", "run a package from a feed"),
            ("cop <file.cop>", "run a local .cop file"),
            ("cop package list", "browse available packages"),
            ("cop repl", "interactive repl"));
        Section("getting started");
        Console.WriteLine("  1. run a package:   cop run <package-name>");
        Console.WriteLine("  2. run a .cop file: cop run <file.cop>");
        Console.WriteLine();
        WriteLine("  cop -h for more options", Label);
    }

    // ── layout helpers ───────────────────────────────────────────────────────

    private static void Title() => WriteLine(HelpCommand.Banner, Accent);

    private static void Section(string label)
    {
        Console.WriteLine();
        WriteLine(label, Label);
    }

    private static void Rows(params (string Left, string Right)[] rows)
    {
        int width = rows.Max(r => r.Left.Length);
        foreach (var (left, right) in rows)
        {
            Console.Write("  ");
            Write(left, Accent);
            Console.Write(new string(' ', width - left.Length + 3));
            Console.WriteLine(right);
        }
    }

    // ── colour-aware writers ─────────────────────────────────────────────────

    private static void Write(string text, ConsoleColor color)
    {
        if (!Color) { Console.Write(text); return; }
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }

    private static void WriteLine(string text, ConsoleColor color)
    {
        Write(text, color);
        Console.WriteLine();
    }
}
