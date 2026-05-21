using Cop.Lang;
using Cop.Providers;

namespace Cop.Repl;

/// <summary>
/// Main REPL session: prompt, read, eval, print loop.
/// </summary>
public class ReplSession
{
    private readonly string _scriptsDir;
    private readonly string _rootPath;
    private ReplContext _context;
    private ReplEvaluator _evaluator;
    private ReplCompleter _completer;
    private LineEditor _editor;
    private Dictionary<string, DateTime> _fileTimestamps;

    private const string Prompt = "cop> ";

    public ReplSession(string scriptsDir, string rootPath)
    {
        _scriptsDir = scriptsDir;
        _rootPath = rootPath;
        _context = InitializeContext();
        _evaluator = new ReplEvaluator(_context);
        _completer = new ReplCompleter(_evaluator);
        _editor = new LineEditor(_completer);
        _editor.SetPrompt(Prompt);
        _fileTimestamps = SnapshotTimestamps();
    }

    /// <summary>
    /// Runs the REPL loop until the user exits.
    /// </summary>
    public int Run()
    {
        PrintBanner();

        while (true)
        {
            string? input = _editor.ReadLine(Prompt);

            if (input is null)
            {
                Console.WriteLine("Bye!");
                return 0;
            }

            input = input.Trim();
            if (string.IsNullOrEmpty(input))
                continue;

            // Handle built-in REPL commands
            var bangResult = HandleBuiltinCommand(input);
            if (bangResult == BangResult.Exit)
            {
                Console.WriteLine("Bye!");
                return 0;
            }
            if (bangResult == BangResult.Handled)
                continue;

            // Auto-reload if .cop files changed on disk
            AutoReloadIfNeeded();

            // Evaluate the input
            try
            {
                var outputs = _evaluator.Evaluate(input);
                foreach (var line in outputs)
                    Console.WriteLine(line);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Console.Error.WriteLine($"\x1b[31mError: {ex.Message}\x1b[0m");
            }
        }
    }

    private enum BangResult { NotHandled, Handled, Exit }

    private void PrintBanner()
    {
        var dirName = Path.GetFileName(_scriptsDir);
        var files = Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
        var fileNames = string.Join(", ", files.Select(Path.GetFileName));
        Console.WriteLine($"\x1b[36mcop\x1b[0m working on {dirName}/{fileNames}");
        Console.WriteLine();
    }

    /// <summary>
    /// Handles built-in REPL commands. All commands require a trailing '!' suffix.
    /// Returns Exit to quit, Handled to continue loop, NotHandled if not a built-in command.
    /// </summary>
    private BangResult HandleBuiltinCommand(string input)
    {
        if (!input.EndsWith('!'))
            return BangResult.NotHandled;

        var word = input[..^1].ToLowerInvariant();

        switch (word)
        {
            case "quit" or "q":
                return BangResult.Exit;

            case "clear" or "c":
                Console.Write("\x1b[2J\x1b[H");
                PrintBanner();
                return BangResult.Handled;

            case "reload" or "r":
                ForceReload();
                return BangResult.Handled;

            case "help" or "h":
                PrintHelp();
                return BangResult.Handled;

            case "list" or "l":
                PrintList();
                return BangResult.Handled;

            case "src" or "s":
                PrintSource();
                return BangResult.Handled;

            default:
                return BangResult.NotHandled;
        }
    }

    private void AutoReloadIfNeeded()
    {
        if (!FilesChanged()) return;
        ReloadContext();
    }

    private void ForceReload()
    {
        ReloadContext();
        Console.WriteLine("Reloaded.");
    }

    private void ReloadContext()
    {
        _context = InitializeContext();
        _evaluator = new ReplEvaluator(_context);
        _completer = new ReplCompleter(_evaluator);
        _editor = new LineEditor(_completer);
        _editor.SetPrompt(Prompt);
        _fileTimestamps = SnapshotTimestamps();
    }

    private Dictionary<string, DateTime> SnapshotTimestamps()
    {
        var timestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly))
                timestamps[file] = File.GetLastWriteTimeUtc(file);
        }
        catch { }
        return timestamps;
    }

    private bool FilesChanged()
    {
        try
        {
            var currentFiles = Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
            if (currentFiles.Length != _fileTimestamps.Count)
                return true;
            foreach (var file in currentFiles)
            {
                if (!_fileTimestamps.TryGetValue(file, out var lastWrite))
                    return true;
                if (File.GetLastWriteTimeUtc(file) != lastWrite)
                    return true;
            }
        }
        catch { return true; }
        return false;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Commands:  quit! (q!) | clear! (c!) | reload! (r!) | list! (l!) | src! (s!) | help! (h!)
            Evaluate:  <name>  command or let binding
                       <N>!    line N from .cop file
                       <expr>  cop expression (e.g., Code.Types:isPublic)
            Values:    42  'text'  [1, 2, 3]
            Files auto-reload when changed on disk.
            Keys:      Tab completions | Up/Down history | Ctrl+D exit
            """);
    }

    private void PrintSource()
    {
        var files = Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            Console.WriteLine("(no .cop files)");
            return;
        }

        foreach (var filePath in files.OrderBy(f => f))
        {
            if (files.Length > 1)
                Console.WriteLine($"\x1b[36m── {Path.GetFileName(filePath)} ──\x1b[0m");

            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
                Console.WriteLine($"\x1b[90m{i + 1,3}\x1b[0m {lines[i]}");

            if (files.Length > 1)
                Console.WriteLine();
        }
    }

    private void PrintList()
    {
        var commands = _context.ScriptFiles
            .SelectMany(f => f.Commands)
            .Where(c => c.IsCommand)
            .Select(c => c.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();

        var lets = _context.ScriptFiles
            .SelectMany(f => f.LetDeclarations)
            .Select(l => l.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();

        var collections = _context.TypeRegistry.GetAllCollectionNames()
            .OrderBy(s => s)
            .ToList();

        if (commands.Count > 0)
        {
            Console.WriteLine("Commands:");
            foreach (var c in commands)
                Console.WriteLine($"  {c}");
        }
        if (lets.Count > 0)
        {
            Console.WriteLine("Let bindings:");
            foreach (var l in lets)
                Console.WriteLine($"  {l}");
        }
        if (collections.Count > 0)
        {
            Console.WriteLine("Collections:");
            foreach (var c in collections)
                Console.WriteLine($"  {c}");
        }
        if (commands.Count == 0 && lets.Count == 0 && collections.Count == 0)
            Console.WriteLine("(nothing defined)");
    }

    private ReplContext InitializeContext()
    {
        var errors = new List<string>();
        var context = Engine.PrepareRepl(_scriptsDir, _rootPath, errors);

        if (context is null)
        {
            Console.Error.WriteLine("Failed to initialize REPL:");
            foreach (var e in errors)
                Console.Error.WriteLine($"  {e}");
            return new ReplContext([], new TypeRegistry(), _rootPath, _scriptsDir, []);
        }

        if (errors.Count > 0)
        {
            foreach (var e in errors)
                Console.Error.WriteLine($"\x1b[33mWarning: {e}\x1b[0m");
        }

        return context;
    }
}
