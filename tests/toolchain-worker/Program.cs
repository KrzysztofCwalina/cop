using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Cop.Providers;

switch (args[0])
{
    case "arguments":
        Console.Write(JsonSerializer.Serialize(args.Skip(1)));
        break;
    case "echo":
        Console.Write(await Console.In.ReadToEndAsync());
        Console.Error.Write("stderr");
        break;
    case "fail":
        Console.Error.Write("source.py(3): invalid syntax");
        return 17;
    case "sleep":
        await Task.Delay(TimeSpan.FromMinutes(1));
        break;
    case "tree":
    case "exited-tree":
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("sleep");
        using (var child = Process.Start(start)!)
        {
            File.WriteAllText(args[1], child.Id.ToString());
            if (args[0] == "exited-tree") return 0;
            await Task.Delay(TimeSpan.FromMinutes(1));
        }
        break;
    case "environment":
        Console.Write(Environment.GetEnvironmentVariable(args[1]));
        break;
    case "engine-probe":
        var engineResult = Engine.Run(args[1], args[2]);
        var pids = File.ReadAllLines(Path.Combine(args[2], "pids.txt")).Select(int.Parse).Distinct().ToArray();
        bool stopped = true;
        foreach (var pid in pids)
        {
            try
            {
                using var worker = Process.GetProcessById(pid);
                if (!worker.HasExited)
                {
                    stopped = false;
                    worker.Kill(entireProcessTree: true);
                    worker.WaitForExit(2000);
                }
            }
            catch (ArgumentException) { }
        }
        Console.Write(JsonSerializer.Serialize(new
        {
            engineResult.HasFatalErrors, engineResult.Errors, engineResult.Warnings, Stopped = stopped, Pids = pids
        }));
        break;
    case "flood":
        while (true)
        {
            Console.Out.Write(new string('x', 4096));
            Console.Error.Write(new string('y', 4096));
        }
    case "worker":
    case "invalid-worker":
    case "stderr-worker":
    case "oversized-worker":
    case "slow-worker":
        int count = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            if (args[0] == "slow-worker") await Task.Delay(TimeSpan.FromMinutes(1));
            if (args[0] == "invalid-worker")
                Console.WriteLine("not json");
            else if (args[0] == "stderr-worker")
            {
                Console.Error.Write(new string('e', 100_000));
                Console.Error.Flush();
                await Task.Delay(TimeSpan.FromMinutes(1));
            }
            else if (args[0] == "oversized-worker")
                Console.WriteLine(new string('x', 100_000));
            else
                Console.WriteLine(JsonSerializer.Serialize(new { pid = Environment.ProcessId, count = ++count, request = line }));
            Console.Out.Flush();
        }
        break;
    default:
        throw new ArgumentException($"Unknown helper mode: {args[0]}");
}
return 0;
