using System.CommandLine;
using System.Diagnostics;
using Cop.Cli.Commands;

bool diag = args.Contains("--diag");
long clrStartupMs = 0;
if (diag)
{
    var process = Process.GetCurrentProcess();
    clrStartupMs = (long)(DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds;
    Console.Error.WriteLine($"[diag] Process startup: {clrStartupMs}ms");
}

var rootCommand = new RootCommand { Description = "cop — a DSL for processing lists" };

rootCommand.Add(RestoreCommand.Create());
rootCommand.Add(LockCommand.Create());
rootCommand.Add(UnlockCommand.Create());
rootCommand.Add(NewCommand.Create());
rootCommand.Add(ValidateCommand.Create());
rootCommand.Add(PublishCommand.Create());
rootCommand.Add(SearchCommand.Create());
rootCommand.Add(FeedCommand.Create());
rootCommand.Add(RunCommand.Create());
rootCommand.Add(HelpCommand.Create());


// System.CommandLine reserves 'help' as a directive, so intercept it before parsing
if (args.Length >= 1 && args[0] == "help")
{
    string? file = args.Length >= 2 ? args[1] : null;
    return HelpCommand.Execute(file);
}

return rootCommand.Parse(args).Invoke();
