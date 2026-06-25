using System.Linq;

namespace Cop.Cli.Commands;

/// <summary>
/// Renders the CLI's help screens with a single, consistent, lower-case design:
///   - <see cref="PrintMainHelp"/>        the command list (`cop -h`, `cop help`)
///   - <see cref="PrintGettingStarted"/>  the friendly intro for a bare `cop`
///   - <see cref="PrintCommandHelp"/>     detailed per-command help (`cop &lt;command&gt; -h`)
///
/// The main list shows each command with its most useful option inline (optional parts in
/// <c>[ ]</c>); full options live under each command's <c>-h</c>. Colour uses
/// <see cref="Console.ForegroundColor"/> (not ANSI) so it renders in every terminal and is
/// suppressed when output is redirected or <c>--no-color</c> is set (<see cref="ConsoleMarkdown.UseColor"/>).
/// </summary>
internal static class CliHelp
{
    private const ConsoleColor Command = ConsoleColor.Cyan;    // command names, title, examples — emphasized
    private const ConsoleColor Option = ConsoleColor.Green;    // option / flag names
    private const ConsoleColor Label = ConsoleColor.DarkGray;  // section labels, footnotes

    private static bool Color => ConsoleMarkdown.UseColor;

    // ── main command list (`cop -h`) ─────────────────────────────────────────

    private static readonly (string Listing, string Summary)[] MainListing =
    {
        ("cop run <package> [-t <target>]", "run a package from a feed (auto-restores)"),
        ("cop <file.cop> [-t <target>]", "run a local .cop file"),
        ("cop package list", "browse available packages"),
        ("cop help language", "full language reference"),
        ("cop help <package>", "package documentation"),
        ("cop init [--claude]", "generate copilot instructions"),
        ("cop update [--force]", "update cop to the latest release"),
        ("cop vscode", "install vs code extension"),
        ("cop test [<file>]", "run tests"),
        ("cop verify [<path>]", "verify program correctness"),
        ("cop repl", "interactive repl"),
    };

    public static void PrintMainHelp()
    {
        Title();
        Section("usage");
        CommandRows(MainListing);
        Console.WriteLine();
        WriteLine("run 'cop <command> -h' for command details, 'cop -v' for the version", Label);
    }

    public static void PrintGettingStarted()
    {
        Title();
        Section("usage");
        CommandRows(
            ("cop run <package>", "run a package from a feed"),
            ("cop <file.cop>", "run a local .cop file"),
            ("cop package list", "browse available packages"),
            ("cop repl", "interactive repl"));
        Section("getting started");
        Console.WriteLine("  1. run a package:   cop run <package-name>");
        Console.WriteLine("  2. run a .cop file: cop run <file.cop>");
        Console.WriteLine();
        WriteLine("  run 'cop -h' for all commands", Label);
    }

    // ── per-command help (`cop <command> -h`) ────────────────────────────────

    private sealed record CommandHelp(
        string Usage,
        string About,
        (string Name, string Desc)[] Options,
        string[] Examples,
        string OptionsLabel = "options");

    private static readonly Dictionary<string, CommandHelp> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["run"] = new(
            "cop run <package|file.cop|url> [args] [-t <dir>] [-c <commands>] [-f <format>]",
            "run a package from a feed (auto-restored on first use), a local .cop file, or a url.",
            new[]
            {
                ("-t <dir>", "target directory to analyze (default: current directory)"),
                ("-c <commands>", "run only these commands (comma-separated)"),
                ("-f <format>", "output format: text or json (default: text)"),
                ("-p <provider>", "load a provider package (repeatable)"),
            },
            new[] { "cop run csharp-checks -t .", "cop run ./cop-checks/main.cop -t src" }),

        ["test"] = new(
            "cop test [<file|dir>]",
            "run the `test` assertions in .cop files and report pass/fail. with no path, scans the current directory.",
            new[] { ("-d", "print diagnostic timing to stderr") },
            new[] { "cop test", "cop test tests/" }),

        ["verify"] = new(
            "cop verify [<path>]",
            "verify program correctness without running it: syntax, imports, types, and name bindings. with no path, scans the current directory.",
            Array.Empty<(string, string)>(),
            new[] { "cop verify", "cop verify cop-checks/" }),

        ["syntax"] = new(
            "cop syntax [<path>]",
            "validate .cop syntax only — a lighter check than verify (no imports or types).",
            Array.Empty<(string, string)>(),
            new[] { "cop syntax checks.cop" }),

        ["lock"] = new(
            "cop lock <files...> [--list]",
            "lock files for tamper protection.",
            new[] { ("--list", "show locked files (no key needed)") },
            new[] { "cop lock cop-checks/main.cop" }),

        ["unlock"] = new(
            "cop unlock <files...>",
            "unlock previously locked files.",
            Array.Empty<(string, string)>(),
            new[] { "cop unlock cop-checks/main.cop" }),

        ["init"] = new(
            "cop init [--claude] [--checks] [--cop-cmd <invocation>]",
            "generate agent instruction files for this repository (github copilot by default).",
            new[]
            {
                ("--claude", "target claude code instead of github copilot"),
                ("--checks", "generate cop checks from your existing instructions (via a coding agent)"),
                ("--cop-cmd <invocation>", "how generated files should invoke cop (e.g. \"mise exec -- cop\")"),
            },
            new[] { "cop init", "cop init --claude" }),

        ["update"] = new(
            "cop update [--force]",
            "update cop to the latest github release. skips the download when already up to date.",
            new[] { ("--force, -f", "reinstall even when already on the latest release") },
            new[] { "cop update", "cop update --force" }),

        ["vscode"] = new(
            "cop vscode",
            "install the cop vs code extension.",
            Array.Empty<(string, string)>(),
            new[] { "cop vscode" }),

        ["repl"] = new(
            "cop repl",
            "launch the interactive read-eval-print loop for prototyping checks.",
            Array.Empty<(string, string)>(),
            new[] { "cop repl" }),

        ["help"] = new(
            "cop help [language | <package> | <file.cop>]",
            "show help: with no argument, the command list; `language` for the language reference; a package name for its api docs; or a .cop file to list its commands.",
            Array.Empty<(string, string)>(),
            new[] { "cop help language", "cop help csharp" }),

        ["package"] = new(
            "cop package <command>",
            "manage cop packages.",
            new[]
            {
                ("list [--feed <feed>]", "list all available packages"),
                ("commands <package>", "show checks, groups, and commands a package exports"),
                ("search <query>", "search packages across configured feeds"),
                ("restore <file>", "restore packages used by a .cop file"),
                ("new <name>", "scaffold a new package directory"),
                ("validate <path>", "validate a package's structure, source, and samples"),
                ("publish <name>", "validate and publish a package version"),
                ("feed <add|remove|list>", "manage package feeds"),
            },
            new[] { "cop package list", "cop package commands csharp" },
            OptionsLabel: "commands"),
    };

    /// <summary>True when <paramref name="verb"/> has detailed per-command help.</summary>
    public static bool HasCommandHelp(string verb) => Commands.ContainsKey(verb);

    public static void PrintCommandHelp(string verb)
    {
        if (!Commands.TryGetValue(verb, out var c))
        {
            PrintMainHelp();
            return;
        }

        Title();
        Section("usage");
        Console.Write("  ");
        WriteSignature(c.Usage);
        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine("  " + c.About);

        // Options (or, for `package`, subcommands), always including the universal -h. Option flags
        // are green; subcommands get the same command-word emphasis as the main list.
        Section(c.OptionsLabel);
        var opts = c.Options.Append(("-h", "show this help")).ToArray();
        if (c.OptionsLabel == "commands")
            CommandRows(opts);
        else
            Rows(Option, opts);

        if (c.Examples.Length > 0)
        {
            Section("examples");
            foreach (var ex in c.Examples)
            {
                Console.Write("  ");
                WriteSignature(ex);
                Console.WriteLine();
            }
        }
        Console.WriteLine();
    }

    // ── layout helpers ───────────────────────────────────────────────────────

    private static void Title() => WriteLine(HelpCommand.Banner, Command);

    private static void Section(string label)
    {
        Console.WriteLine();
        WriteLine(label, Label);
    }

    private static void Rows(ConsoleColor leftColor, params (string Left, string Right)[] rows)
    {
        int width = rows.Max(r => r.Left.Length);
        foreach (var (left, right) in rows)
        {
            Console.Write("  ");
            Write(left, leftColor);
            Console.Write(new string(' ', width - left.Length + 3));
            Console.WriteLine(right);
        }
    }

    // Rows whose left column is a command signature: only the command word is emphasized (see
    // WriteSignature), so the commands form a scannable coloured column.
    private static void CommandRows(params (string Left, string Right)[] rows)
    {
        int width = rows.Max(r => r.Left.Length);
        foreach (var (left, right) in rows)
        {
            Console.Write("  ");
            WriteSignature(left);
            Console.Write(new string(' ', width - left.Length + 3));
            Console.WriteLine(right);
        }
    }

    /// <summary>
    /// Writes a command signature such as "cop run &lt;package&gt; [-t &lt;target&gt;]" with only the
    /// command word emphasized: a leading "cop" is dimmed, the command word (the next token, or the
    /// first token when there is no "cop" prefix) is shown in the command colour, and the remaining
    /// arguments/options are left in the default colour so the command itself stands out.
    /// </summary>
    private static void WriteSignature(string signature)
    {
        if (!Color) { Console.Write(signature); return; }

        var parts = signature.Split(' ');
        bool hasCopPrefix = parts.Length > 0 && parts[0] == "cop";
        int cmdIndex = hasCopPrefix ? 1 : 0;
        for (int i = 0; i < parts.Length; i++)
        {
            if (i > 0) Console.Write(' ');
            if (i == 0 && hasCopPrefix) Write(parts[i], Label);   // dim the repeated "cop"
            else if (i == cmdIndex) Write(parts[i], Command);     // the command word — emphasized
            else Console.Write(parts[i]);                         // arguments/options — default colour
        }
    }

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
