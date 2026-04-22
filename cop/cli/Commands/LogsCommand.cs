using System.CommandLine;
using System.CommandLine.Parsing;

namespace Cop.Cli.Commands;

public static class LogsCommand
{
    public static Command Create()
    {
        var taskIdArg = new Argument<string>("taskId");
        var command = new Command("logs", "Get logs for a task")
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
            var response = await client.GetAsync($"http://localhost:5100/api/tasks/{taskId}/logs");
            var body = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error: {response.StatusCode} — {body}");
                return 1;
            }

            // Print each log line
            foreach (var line in body.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Console.WriteLine(line);
                }
            }
            
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Error: Cannot connect to copweb driver. Is it running? ({ex.Message})");
            return 1;
        }
    }
}
