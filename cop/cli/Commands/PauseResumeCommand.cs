using System.CommandLine;
using System.CommandLine.Parsing;

namespace Cop.Cli.Commands;

public static class PauseResumeCommand
{
    public static Command CreatePause()
    {
        var taskIdArg = new Argument<string>("taskId");
        var command = new Command("pause", "Pause a running task")
        {
            taskIdArg
        };
        command.SetAction(parseResult => ExecutePause(parseResult.GetValue(taskIdArg)).GetAwaiter().GetResult());
        return command;
    }

    public static Command CreateResume()
    {
        var taskIdArg = new Argument<string>("taskId");
        var command = new Command("resume", "Resume a paused task")
        {
            taskIdArg
        };
        command.SetAction(parseResult => ExecuteResume(parseResult.GetValue(taskIdArg)).GetAwaiter().GetResult());
        return command;
    }

    public static async Task<int> ExecutePause(string taskId)
    {
        using var client = new HttpClient();
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:5100/api/tasks/{taskId}/pause");
            request.Content = new StringContent("");
            
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Task {taskId} paused.");
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

    public static async Task<int> ExecuteResume(string taskId)
    {
        using var client = new HttpClient();
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:5100/api/tasks/{taskId}/resume");
            request.Content = new StringContent("");
            
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Task {taskId} resumed.");
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
