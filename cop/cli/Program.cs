using System.CommandLine;
using System.CommandLine.Help;
using System.Diagnostics;
using System.Linq;
using Cop.Cli.Commands;

bool diag = args.Contains("-d");
long clrStartupMs = 0;
if (diag)
{
    var process = Process.GetCurrentProcess();
    clrStartupMs = (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
    Console.Error.WriteLine($"[diag] Process startup: {clrStartupMs}ms");
}

var rootCommand = new RootCommand
{
    Description = """
        cop — a DSL for processing lists

        Quick reference:
          cop run [<command>] [-t <target>] [-c <commands>] [-f text|json] [-d]
          cop check <packages> [-t <target>] [-c <rules>] [-f text|json] [-d]
          cop test [<file>] [-d]
          cop package <subcommand>
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

rootCommand.Add(RunCommand.Create());
rootCommand.Add(CheckCommand.Create());
rootCommand.Add(TestCommand.Create());
rootCommand.Add(LockCommand.Create());
rootCommand.Add(UnlockCommand.Create());
rootCommand.Add(HelpCommand.Create());

var packageCommand = new Command("package", "Manage cop packages");
packageCommand.Add(RestoreCommand.Create());
packageCommand.Add(NewCommand.Create());
packageCommand.Add(ValidateCommand.Create());
packageCommand.Add(PublishCommand.Create());
packageCommand.Add(SearchCommand.Create());
packageCommand.Add(FeedCommand.Create());
rootCommand.Add(packageCommand);


// System.CommandLine reserves 'help' as a directive, so intercept it before parsing
if (args.Length >= 1 && args[0] == "help")
{
    string? file = args.Length >= 2 ? args[1] : null;
    return HelpCommand.Execute(file);
}

return rootCommand.Parse(args).Invoke();
