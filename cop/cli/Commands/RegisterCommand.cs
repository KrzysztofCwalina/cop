using System.CommandLine;
using System.CommandLine.Parsing;

namespace Cop.Cli.Commands;

public static class RegisterCommand
{
    public static Command Create()
    {
        var command = new Command("register", "Register with copweb driver");
        command.SetAction(_ => ExecuteAsync().GetAwaiter().GetResult());
        return command;
    }

    public static Task<int> ExecuteAsync()
    {
        Console.WriteLine("Registration with copweb driver — not yet implemented");
        return Task.FromResult(0);
    }
}
