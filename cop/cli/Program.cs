using System.CommandLine;
using System.CommandLine.Help;
using System.Diagnostics;
using System.Linq;
using Cop.Cli.Commands;
using Cop.Repl;

bool diag = args.Contains("-d");
long clrStartupMs = 0;
if (diag)
{
    var process = Process.GetCurrentProcess();
    clrStartupMs = (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
    Console.Error.WriteLine($"[diag] Process startup: {clrStartupMs}ms");
}

// Known verbs (subcommands) — anything else is treated as a program to run
var knownVerbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "test", "syntax", "lock", "unlock", "help", "package", "repl"
};

// Bare invocation (no arguments): look for local .cop config or show getting-started
if (args.Length == 0)
{
    return CheckCommand.ExecuteFromConfig();
}

// Intercept help flags before System.CommandLine to show clean single-section help
if (args.Length == 1 && (args[0] == "-h" || args[0] == "-help" || args[0] == "--help"))
{
    Console.WriteLine("""
        cop — a general-purpose scripting language

        Usage:
          cop <program>                      Run a package, local command, or .cop file
          cop package list                   Browse available packages
          cop package commands <package>     Show what a package exports
          cop test [<file>]                  Run tests
          cop syntax <path>                  Validate .cop file syntax
          cop lock/unlock <files>            Tamper protection
          cop repl                           Interactive REPL

        Options:
          -t <dir>      Target directory
          -c <commands> Filter to specific commands (comma-separated)
          -f <format>   Output format: text or json
          -h            Show help
          -v            Show version
        """);
    return 0;
}

// System.CommandLine reserves 'help' as a directive, so intercept it before parsing
if (args[0] == "help")
{
    string? file = args.Length >= 2 ? args[1] : null;
    return HelpCommand.Execute(file);
}

// If first arg is not a known verb and not a switch, figure out what to run
if (!knownVerbs.Contains(args[0]) && !args[0].StartsWith('-') && !args[0].StartsWith('/'))
{
    var firstArg = args[0];

    // 1. Explicit .cop file → run it directly
    if (firstArg.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
    {
        return RunCommand.Execute(firstArg, args.Length > 1 ? args[1..] : null);
    }

    // 2. URL → run remotely
    if (firstArg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        firstArg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return RunCommand.Execute(firstArg, args.Length > 1 ? args[1..] : null);
    }

    // 3. Check if local .cop files define a command with this name
    var cwd = Directory.GetCurrentDirectory();
    var localCopFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);
    if (localCopFiles.Length > 0 && IsLocalCommand(firstArg, localCopFiles))
    {
        return RunCommand.Execute(firstArg, args.Length > 1 ? args[1..] : null);
    }

    // 4. Otherwise → treat all non-switch args as package names
    var packages = args.TakeWhile(a => !a.StartsWith('-') && !a.StartsWith('/')).ToArray();
    var remainingArgs = args.Skip(packages.Length).ToArray();

    // Parse common options from remaining args
    string? target = null;
    string? rules = null;
    string? format = "text";
    bool isDiag = diag;
    for (int i = 0; i < remainingArgs.Length; i++)
    {
        if (remainingArgs[i] == "-t" && i + 1 < remainingArgs.Length) target = remainingArgs[++i];
        else if (remainingArgs[i] == "-c" && i + 1 < remainingArgs.Length) rules = remainingArgs[++i];
        else if (remainingArgs[i] == "-f" && i + 1 < remainingArgs.Length) format = remainingArgs[++i];
        else if (remainingArgs[i] == "-d") isDiag = true;
    }

    string rootPath = target != null ? Path.GetFullPath(target) : Directory.GetCurrentDirectory();
    string[]? rulesFilter = null;
    if (!string.IsNullOrEmpty(rules))
        rulesFilter = rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    return RunCommand.ExecutePackages(packages, rootPath, rulesFilter, format, isDiag);
}

var rootCommand = new RootCommand
{
    Description = """
        cop — a general-purpose scripting language

        Quick reference:
          cop <program>                    Run a package, local command, or .cop file
          cop package list                 Browse available packages
          cop package commands <package>   Show what a package exports
          cop test [<file>]               Run tests
          cop repl                        Interactive REPL
          cop <command> -h for details
        """
};

// Replace built-in --help/-?/-h with just -h
var defaultHelp = rootCommand.Options.FirstOrDefault(o => o is HelpOption);
if (defaultHelp != null) rootCommand.Options.Remove(defaultHelp);
rootCommand.Options.Add(new HelpOption("-h"));

// Replace built-in --version with -v
var defaultVersion = rootCommand.Options.FirstOrDefault(o => o is VersionOption);
if (defaultVersion != null) rootCommand.Options.Remove(defaultVersion);
rootCommand.Options.Add(new VersionOption("-v"));

rootCommand.Add(TestCommand.Create());
rootCommand.Add(SyntaxCommand.Create());
rootCommand.Add(LockCommand.Create());
rootCommand.Add(UnlockCommand.Create());

// cop repl — launch interactive REPL
var replCommand = new Command("repl", "Launch interactive REPL");
replCommand.SetAction(_ =>
{
    var scriptsDir = Directory.GetCurrentDirectory();
    var session = new ReplSession(scriptsDir, scriptsDir);
    return session.Run();
});
rootCommand.Add(replCommand);

var packageCommand = new Command("package", "Manage cop packages");
packageCommand.Add(PackageListCommand.Create());
packageCommand.Add(ListCommand.Create());
packageCommand.Add(RestoreCommand.Create());
packageCommand.Add(NewCommand.Create());
packageCommand.Add(ValidateCommand.Create());
packageCommand.Add(PublishCommand.Create());
packageCommand.Add(SearchCommand.Create());
packageCommand.Add(FeedCommand.Create());
rootCommand.Add(packageCommand);

return rootCommand.Parse(args).Invoke();

/// <summary>
/// Checks if any local .cop file defines a command with the given name.
/// </summary>
static bool IsLocalCommand(string name, string[] copFiles)
{
    foreach (var file in copFiles)
    {
        try
        {
            var source = File.ReadAllText(file);
            var sf = Cop.Lang.ScriptParser.Parse(source, file);
            foreach (var cmd in sf.Commands)
            {
                if (cmd.IsCommand && cmd.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
    }
    return false;
}
