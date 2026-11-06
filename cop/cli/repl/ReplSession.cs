// Coordinates the interactive prompt, session commands, and input evaluation.

using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Providers;

namespace Cop.Repl;

/// <summary>
/// Main REPL session: prompt, read, eval, print loop.
/// </summary>
public class ReplSession : IAsyncDisposable
{
    private readonly string _scriptsDir;
    private readonly string _rootPath;
    private ReplContext _context;
    private ReplEvaluator _evaluator;
    private ReplCompleter _completer;
    private LineEditor _editor;
    private Dictionary<string, DateTime> _fileTimestamps;

    private const string Prompt = "cop> ";

    /// <summary>Initializes an interactive session from scripts and an analysis target.</summary>
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
    public async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        PrintBanner();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? input = _editor.ReadLine(Prompt);

            if (input is null)
            {
                Console.WriteLine("Bye!");
                return 0;
            }

            input = input.Trim();
            if (string.IsNullOrEmpty(input))
            {
                continue;
            }

            // Handle built-in REPL commands
            BangResult bangResult = await HandleBuiltinCommandAsync(input);
            if (bangResult == BangResult.Exit)
            {
                Console.WriteLine("Bye!");
                return 0;
            }
            if (bangResult == BangResult.Handled)
            {
                continue;
            }

            // Auto-reload if .cop files changed on disk
            await AutoReloadIfNeededAsync();

            // Evaluate the input
            try
            {
                List<string> outputs = await _evaluator.EvaluateAsync(input, cancellationToken);
                foreach (string line in outputs)
                {
                    Console.WriteLine(line);
                }
            }
            catch (Exception ex) when (ex is Cop.Lang.Interpreter.CopEvaluationException or
                ParseException or IOException or UnauthorizedAccessException or
                System.Security.SecurityException or HttpRequestException or TimeoutException or
                ArgumentException or InvalidOperationException or NotSupportedException or
                FormatException or System.Text.Json.JsonException)
            {
                Console.Error.WriteLine($"\x1b[31mError: {ex.Message}\x1b[0m");
            }
        }
    }

    private enum BangResult { NotHandled, Handled, Exit }

    private void PrintBanner()
    {
        Version? version = typeof(ReplSession).Assembly.GetName().Version;
        string versionStr = version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "dev";
        string dirName = Path.GetFileName(_scriptsDir);
        string[] files = Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
        string fileNames = string.Join(", ", files.Select(Path.GetFileName));

        const string b = "\x1b[34m";   // blue (body/hat)
        const string c = "\x1b[96m";   // bright cyan (magnifying glass)
        const string d = "\x1b[2m";    // dim
        const string r = "\x1b[0m";    // reset

        Console.WriteLine($"""

        {b}          ▄███▄              {c}Agent Cop{r} {d}v{versionStr}{r}
        {b}       ▄████████▄{r}
        {b}    ██████████████████       {d}Static analysis for the AI era{r}
        {b}        ▐██{r}●{b}██▌{r}
        {b}     {c}▄██████▄{b} ▄████{r}       {d}Working on{r} {dirName}/{fileNames}
        {b}    {c}████████████{b}▐████{r}
        {b}    {c}████████████{b}▐████{r}      {d}Type{r} help! {d}for commands{r}
        {b}     {c}▀██████▀{b}▐██████{r}
        {b}       ████████████{r}
        {b}       ████  ████{r}

        """);
    }

    /// <summary>
    /// Handles built-in REPL commands. All commands require a trailing '!' suffix.
    /// Returns Exit to quit, Handled to continue loop, NotHandled if not a built-in command.
    /// </summary>
    private async ValueTask<BangResult> HandleBuiltinCommandAsync(string input)
    {
        if (!input.EndsWith('!'))
        {
            return BangResult.NotHandled;
        }

        string word = input[..^1].ToLowerInvariant();

        switch (word)
        {
            case "quit" or "q":
                return BangResult.Exit;

            case "clear" or "c":
                Console.Write("\x1b[2J\x1b[H");
                PrintBanner();
                return BangResult.Handled;

            case "reload" or "r":
                await ForceReloadAsync();
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

    private async ValueTask AutoReloadIfNeededAsync()
    {
        if (!FilesChanged())
        {
            return;
        }

        await ReloadContextAsync();
    }

    private async ValueTask ForceReloadAsync()
    {
        await ReloadContextAsync();
        Console.WriteLine("Reloaded.");
    }

    private async ValueTask ReloadContextAsync()
    {
        ReplContext previousContext = _context;
        _context = InitializeContext();
        _evaluator = new ReplEvaluator(_context);
        _completer = new ReplCompleter(_evaluator);
        _editor = new LineEditor(_completer);
        _editor.SetPrompt(Prompt);
        _fileTimestamps = SnapshotTimestamps();
        await previousContext.DisposeAsync();
    }

    /// <summary>Releases external providers retained by the interactive session.</summary>
    public ValueTask DisposeAsync() => _context.DisposeAsync();

    private Dictionary<string, DateTime> SnapshotTimestamps()
    {
        Dictionary<string, DateTime> timestamps = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (string file in Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly))
            {
                timestamps[file] = File.GetLastWriteTimeUtc(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot inspect REPL scripts: {ex.Message}");
        }
        return timestamps;
    }

    private bool FilesChanged()
    {
        try
        {
            string[] currentFiles = Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
            if (currentFiles.Length != _fileTimestamps.Count)
            {
                return true;
            }

            foreach (string file in currentFiles)
            {
                if (!_fileTimestamps.TryGetValue(file, out DateTime lastWrite))
                {
                    return true;
                }

                if (File.GetLastWriteTimeUtc(file) != lastWrite)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Cannot inspect REPL scripts: {ex.Message}");
            return true;
        }
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
        string[] files = Directory.GetFiles(_scriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
        {
            Console.WriteLine("(no .cop files)");
            return;
        }

        foreach (string? filePath in files.OrderBy(f => f))
        {
            if (files.Length > 1)
            {
                Console.WriteLine($"\x1b[36m── {Path.GetFileName(filePath)} ──\x1b[0m");
            }

            string[] lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                Console.WriteLine($"\x1b[90m{i + 1,3}\x1b[0m {lines[i]}");
            }

            if (files.Length > 1)
            {
                Console.WriteLine();
            }
        }
    }

    private void PrintList()
    {
        List<string> commands = _context.Modules
            .SelectMany(m => m.Module.Declarations)
            .Where(d => d is CommandDecl || (d is FunctionDecl f && f.IsCommand))
            .Select(d => d is CommandDecl cmd ? cmd.Name : ((FunctionDecl)d).Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();

        List<string> lets = _context.Modules
            .SelectMany(m => m.Module.Declarations)
            .OfType<LetDecl>()
            .Select(l => l.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();

        List<string> collections = _context.TypeRegistry.GetAllCollectionNames()
            .OrderBy(s => s)
            .ToList();

        if (commands.Count > 0)
        {
            Console.WriteLine("Commands:");
            foreach (string c in commands)
            {
                Console.WriteLine($"  {c}");
            }
        }
        if (lets.Count > 0)
        {
            Console.WriteLine("Let bindings:");
            foreach (string l in lets)
            {
                Console.WriteLine($"  {l}");
            }
        }
        if (collections.Count > 0)
        {
            Console.WriteLine("Collections:");
            foreach (string c in collections)
            {
                Console.WriteLine($"  {c}");
            }
        }
        if (commands.Count == 0 && lets.Count == 0 && collections.Count == 0)
        {
            Console.WriteLine("(nothing defined)");
        }
    }

    private ReplContext InitializeContext()
    {
        List<string> errors = new List<string>();
        ReplContext? context = Engine.PrepareRepl(_scriptsDir, _rootPath, errors);

        if (context is null)
        {
            Console.Error.WriteLine("Failed to initialize REPL:");
            foreach (string e in errors)
            {
                Console.Error.WriteLine($"  {e}");
            }

            return new ReplContext([], new TypeRegistry(), _rootPath, _scriptsDir, []);
        }

        if (errors.Count > 0)
        {
            foreach (string e in errors)
            {
                Console.Error.WriteLine($"\x1b[33mWarning: {e}\x1b[0m");
            }
        }

        return context;
    }
}
