using System.CommandLine;
using Cop.Cli.Commands;

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
rootCommand.Add(StatusCommand.Create());
rootCommand.Add(LogsCommand.Create());
rootCommand.Add(StopCommand.Create());
rootCommand.Add(FeedbackCommand.Create());
rootCommand.Add(PauseResumeCommand.CreatePause());
rootCommand.Add(PauseResumeCommand.CreateResume());
rootCommand.Add(RegisterCommand.Create());

return rootCommand.Parse(args).Invoke();
