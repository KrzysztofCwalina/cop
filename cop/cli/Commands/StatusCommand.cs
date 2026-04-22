using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

namespace Cop.Cli.Commands;

public static class StatusCommand
{
    public static Command Create()
    {
        var command = new Command("status", "Get status of all running tasks");
        command.SetAction(_ => ExecuteAsync().GetAwaiter().GetResult());
        return command;
    }

    public static async Task<int> ExecuteAsync()
    {
        using var client = new HttpClient();
        
        try
        {
            var response = await client.GetAsync("http://localhost:5100/api/tasks");
            var body = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Console.Error.WriteLine($"Error: {response.StatusCode} — {body}");
                return 1;
            }

            // Parse JSON response
            using var jsonDoc = JsonDocument.Parse(body);
            var root = jsonDoc.RootElement;
            
            // Assuming response is an array of tasks or an object with tasks property
            var tasks = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("tasks");
            
            // Print header
            Console.WriteLine($"{"ID",-36} {"Spec",-30} {"Phase",-15} {"Elapsed",-15} {"Verifications",-10}");
            Console.WriteLine(new string('-', 106));
            
            // Print each task
            foreach (var task in tasks.EnumerateArray())
            {
                var id = task.GetProperty("id").GetString() ?? "—";
                var spec = task.TryGetProperty("spec", out var specElem) ? specElem.GetString() ?? "—" : "—";
                var phase = task.TryGetProperty("phase", out var phaseElem) ? phaseElem.GetString() ?? "—" : "—";
                var elapsed = task.TryGetProperty("elapsed", out var elapsedElem) ? elapsedElem.GetString() ?? "—" : "—";
                var verifications = task.TryGetProperty("verifications", out var verifElem) ? verifElem.GetString() ?? "—" : "—";
                
                Console.WriteLine($"{id,-36} {spec,-30} {phase,-15} {elapsed,-15} {verifications,-10}");
            }
            
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Error: Cannot connect to copweb driver. Is it running? ({ex.Message})");
            return 1;
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"Error: Invalid response format ({ex.Message})");
            return 1;
        }
    }
}
