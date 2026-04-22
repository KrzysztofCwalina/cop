using System.CommandLine;
using System.CommandLine.Parsing;

namespace Cop.Cli.Commands;

public static class StopCommand
{
    public static Command Create()
    {
        var taskIdArg = new Argument<string>("taskId");
        var command = new Command("stop", "Stop a running task")
        {
            taskIdArg
        };
        command.SetAction(parseResult => ExecuteAsync(parseResult.GetValue(taskIdArg)).GetAwaiter().GetResult());
        return command;
    }

    public static async Task<int> ExecuteAsync(string taskId)
    {
        using var client = new HttpClient();
        
        try
        {
            var response = await client.DeleteAsync($"http://localhost:5100/api/tasks/{taskId}");
            var body = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Task {taskId} stopped.");
                return 0;
            }
            Console.Error.WriteLine($"Error: {response.StatusCode} — {body}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Error: Cannot connect to copweb driver. Is it running? ({ex.Message})");
            return 1;
        }
    }
}
