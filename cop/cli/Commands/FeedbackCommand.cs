using System.CommandLine;
using System.CommandLine.Parsing;

namespace Cop.Cli.Commands;

public static class FeedbackCommand
{
    public static Command Create()
    {
        var taskIdArg = new Argument<string>("taskId");
        var messageArg = new Argument<string>("message");
        var command = new Command("feedback", "Send feedback to a running task")
        {
            taskIdArg,
            messageArg
        };
        command.SetAction(parseResult => ExecuteAsync(parseResult.GetValue(taskIdArg), parseResult.GetValue(messageArg)).GetAwaiter().GetResult());
        return command;
    }

    public static async Task<int> ExecuteAsync(string taskId, string message)
    {
        using var client = new HttpClient();
        
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:5100/api/tasks/{taskId}/feedback");
            request.Content = new StringContent(message);
            
            var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            
            if (response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Feedback sent to task {taskId}.");
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
