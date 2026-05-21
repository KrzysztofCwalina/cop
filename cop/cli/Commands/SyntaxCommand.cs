using System.CommandLine;
using Cop.Lang;

namespace Cop.Cli.Commands;

public static class SyntaxCommand
{
    public static Command Create()
    {
        var pathArg = new Argument<string>("path")
        {
            Arity = ArgumentArity.ZeroOrOne,
            Description = ".cop file or directory to validate syntax"
        };
        var command = new Command("syntax", "Validate .cop file syntax without executing")
        {
            pathArg
        };
        command.SetAction(parseResult => Execute(parseResult.GetValue(pathArg)));
        return command;
    }

    public static int Execute(string? path)
    {
        path ??= Directory.GetCurrentDirectory();
        path = Path.GetFullPath(path);

        string[] files;
        if (File.Exists(path) && path.EndsWith(".cop", StringComparison.OrdinalIgnoreCase))
        {
            files = [path];
        }
        else if (Directory.Exists(path))
        {
            files = Directory.GetFiles(path, "*.cop", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Console.Error.WriteLine($"No .cop files found in: {path}");
                return 1;
            }
        }
        else
        {
            Console.Error.WriteLine($"Path not found: {path}");
            return 1;
        }

        int errors = 0;
        foreach (var file in files)
        {
            try
            {
                var source = File.ReadAllText(file);
                Cop.Lang.Parser.CopParser.ParseFile(source, file);
            }
            catch (ParseException ex)
            {
                Console.Error.WriteLine(ex.Message);
                errors++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"{file}: {ex.Message}");
                errors++;
            }
        }

        if (errors == 0)
        {
            Console.WriteLine($"  \u2713 {files.Length} file(s) parsed successfully");
            return 0;
        }
        else
        {
            Console.Error.WriteLine($"\n  {errors} syntax error(s) in {files.Length} file(s)");
            return 1;
        }
    }
}
