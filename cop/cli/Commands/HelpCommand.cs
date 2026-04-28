using System.CommandLine;
using System.CommandLine.Parsing;
using Cop.Lang;

namespace Cop.Cli.Commands;

public static class HelpCommand
{
    public static Command Create()
    {
        var fileArg = new Argument<string>("file")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = ".cop file path (or omit to scan current directory)"
        };
        var command = new Command("help", "List commands defined in a .cop program")
        {
            fileArg
        };
        command.SetAction(parseResult => Execute(parseResult.GetValue(fileArg)));
        return command;
    }

    public static int Execute(string? file)
    {
        string[] filePaths;

        if (file != null)
        {
            var spec = new FileInfo(file);
            if (!spec.Exists) { Console.Error.WriteLine($"Error: File '{spec.FullName}' not found"); return 1; }
            filePaths = [spec.FullName];
        }
        else
        {
            var dir = Directory.GetCurrentDirectory();
            if (!Directory.Exists(dir))
            {
                Console.Error.WriteLine($"Directory not found: {dir}");
                return 1;
            }
            filePaths = Directory.GetFiles(dir, "*.cop", SearchOption.AllDirectories);
        }

        if (filePaths.Length == 0)
        {
            Console.WriteLine("No .cop files found.");
            return 0;
        }

        var commandEntries = new List<(string Name, string? DocComment, List<string>? Parameters)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in filePaths)
        {
            ScriptFile scriptFile;
            try
            {
                var source = File.ReadAllText(path);
                scriptFile = ScriptParser.Parse(source, path);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine(ex.Message);
                continue;
            }

            foreach (var cmd in scriptFile.Commands)
            {
                if (!cmd.IsCommand) continue;
                if (!seen.Add(cmd.Name)) continue;
                commandEntries.Add((cmd.Name, cmd.DocComment, cmd.Parameters));
            }
        }

        if (commandEntries.Count == 0)
        {
            Console.WriteLine("No commands defined.");
            return 0;
        }

        Console.WriteLine("Commands:");
        foreach (var (name, doc, parameters) in commandEntries)
        {
            var displayName = parameters is { Count: > 0 }
                ? $"{name}({string.Join(", ", parameters)})"
                : name;

            if (!string.IsNullOrEmpty(doc))
                Console.WriteLine($"  {displayName,-30} {doc}");
            else
                Console.WriteLine($"  {displayName}");
        }

        return 0;
    }
}
