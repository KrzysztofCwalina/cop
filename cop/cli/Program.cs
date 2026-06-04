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
    "test", "syntax", "verify", "lock", "unlock", "help", "package", "repl", "init", "update", "vscode"
};

// Bare invocation (no arguments): look for local .cop files to run or show getting-started
if (args.Length == 0)
{
    return ExecuteDefault();
}

// Intercept help flags before System.CommandLine to show clean single-section help
if (args.Length == 1 && (args[0] == "-h" || args[0] == "-help" || args[0] == "--help"))
{
    Console.WriteLine("""
        cop — a general-purpose scripting language

        Usage:
          cop <program>                      Run a package, local command, or .cop file
          cop package list                   Browse available packages
          cop help language                  Full language reference
          cop help <package>                 Package documentation
          cop init                           Generate agent instruction files
          cop update                         Update cop to the latest release
          cop vscode                         Install VS Code extension
          cop test [<file>]                  Run tests
          cop verify [<path>]                Verify program correctness
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
    string? helpArg = args.Length >= 2 ? args[1] : null;
    return HelpCommand.Execute(helpArg);
}

// cop init — generate agent instruction files
if (args[0] == "init")
{
    bool force = args.Contains("--force");
    return InitCommand.Execute(force);
}

// cop update — self-update from GitHub releases
if (args[0] == "update")
{
    return UpdateCommand.Execute();
}

// cop vscode — install VS Code extension
if (args[0] == "vscode")
{
    return VscodeCommand.Execute();
}

// If first arg is not a known verb and not a switch, figure out what to run
if (!knownVerbs.Contains(args[0]) && !args[0].StartsWith('-') && !args[0].StartsWith('/'))
{
    var firstArg = args[0];

    // 1. Explicit .cop file → run it directly
    if (firstArg.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
    {
        // Extract known flags (-t, -f, -d, -c) from remaining args
        string? copTarget = null;
        string? copFormat = null;
        string? copCommands = null;
        bool copDiag = diag;
        var programArgs = new List<string>();
        var remaining = args.Length > 1 ? args[1..] : Array.Empty<string>();
        for (int i = 0; i < remaining.Length; i++)
        {
            if (remaining[i] == "-t" && i + 1 < remaining.Length) copTarget = remaining[++i];
            else if (remaining[i] == "-f" && i + 1 < remaining.Length) copFormat = remaining[++i];
            else if (remaining[i] == "-c" && i + 1 < remaining.Length) copCommands = remaining[++i];
            else if (remaining[i] == "-d") copDiag = true;
            else programArgs.Add(remaining[i]);
        }
        return RunCommand.Execute(firstArg, programArgs.Count > 0 ? programArgs.ToArray() : null, copTarget, copFormat, copCommands, copDiag);
    }

    // 2. URL → run remotely
    if (firstArg.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        firstArg.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
    {
        return RunCommand.Execute(firstArg, args.Length > 1 ? args[1..] : null);
    }

    // 3. Existing directory containing .cop files → run from that directory
    var dirPath = Path.GetFullPath(firstArg.TrimEnd('/', '\\'));
    if (Directory.Exists(dirPath) && Directory.GetFiles(dirPath, "*.cop", SearchOption.AllDirectories).Length > 0)
    {
        string? copTarget = null;
        string? copFormat = null;
        string? copCommands = null;
        bool copDiag = diag;
        var remaining = args.Length > 1 ? args[1..] : Array.Empty<string>();
        for (int i = 0; i < remaining.Length; i++)
        {
            if (remaining[i] == "-t" && i + 1 < remaining.Length) copTarget = remaining[++i];
            else if (remaining[i] == "-f" && i + 1 < remaining.Length) copFormat = remaining[++i];
            else if (remaining[i] == "-c" && i + 1 < remaining.Length) copCommands = remaining[++i];
            else if (remaining[i] == "-d") copDiag = true;
        }
        return RunCommand.ExecuteDirectory(dirPath, copTarget, copFormat, copCommands, copDiag);
    }

    // 4. Check if local .cop files define a command with this name
    var cwd = Directory.GetCurrentDirectory();
    var localCopFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);
    if (localCopFiles.Length > 0 && IsLocalCommand(firstArg, localCopFiles))
    {
        return RunCommand.Execute(firstArg, args.Length > 1 ? args[1..] : null);
    }

    // 5. Otherwise → treat all non-switch args as package names
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
          cop help <package>               Package documentation
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
rootCommand.Add(VerifyCommand.Create());
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
            var module = Cop.Lang.Parser.CopParser.Parse(source, file);
            foreach (var decl in module.Declarations)
            {
                if (decl is Cop.Lang.Ast.CommandDecl cmd && cmd.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (decl is Cop.Lang.Ast.FunctionDecl func && char.IsUpper(func.Name[0]) && func.Body is Cop.Lang.Ast.BlockBody
                    && func.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { }
    }
    return false;
}

/// <summary>
/// Bare invocation: run local .cop files if present, or show getting-started message.
/// </summary>
static int ExecuteDefault()
{
    var cwd = Directory.GetCurrentDirectory();
    var copFiles = Directory.GetFiles(cwd, "*.cop", SearchOption.TopDirectoryOnly);

    if (copFiles.Length == 0)
    {
        Console.WriteLine("cop — a general-purpose scripting language");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  cop <program>          Run a package or .cop file");
        Console.WriteLine("  cop run [<command>]    Run .cop programs");
        Console.WriteLine("  cop package list       Browse available packages");
        Console.WriteLine("  cop repl              Launch interactive REPL");
        Console.WriteLine();
        Console.WriteLine("Getting started:");
        Console.WriteLine("  1. Run a package:  cop <package-name>");
        Console.WriteLine("  2. Customize:      Create a .cop file with 'import <package>'");
        Console.WriteLine("                     then just run 'cop' with no arguments");
        Console.WriteLine();
        Console.WriteLine("  cop -h for more options");
        return 0;
    }

    // Parse .cop files to understand what they contain
    var imports = new List<string>();
    bool hasOwnLogic = false;

    foreach (var file in copFiles)
    {
        try
        {
            var source = File.ReadAllText(file);
            var module = Cop.Lang.Parser.CopParser.Parse(source, file);
            foreach (var decl in module.Declarations)
            {
                if (decl is Cop.Lang.Ast.ImportDecl imp)
                    imports.Add(imp.ModuleName);
                else if (decl is Cop.Lang.Ast.FunctionDecl || decl is Cop.Lang.Ast.LetDecl || decl is Cop.Lang.Ast.CommandDecl)
                    hasOwnLogic = true;
            }
        }
        catch { /* skip unparseable files */ }
    }

    if (hasOwnLogic)
    {
        // Local .cop files define their own logic — run as a program
        return RunCommand.Execute(null);
    }

    if (imports.Count > 0)
    {
        // Config-only: just imports — run those packages
        return RunCommand.ExecutePackages(imports.ToArray(), cwd, null, "text", false);
    }

    return 0;
}
