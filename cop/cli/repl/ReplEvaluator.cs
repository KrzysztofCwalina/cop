using Cop.Lang;
using Cop.Lang.Ast;
using Cop.Providers;

namespace Cop.Repl;

/// <summary>
/// Evaluates REPL input: classifies as command name, let binding, line reference, or ad-hoc expression.
/// </summary>
public class ReplEvaluator
{
    private readonly ReplContext _context;

    public ReplEvaluator(ReplContext context)
    {
        _context = context;
    }

    /// <summary>
    /// Evaluates a line of REPL input and returns the output lines to display.
    /// </summary>
    public List<string> Evaluate(string input)
    {
        input = input.Trim();
        if (string.IsNullOrEmpty(input))
            return [];

        // Line reference: <N>! — evaluate line N from main.cop
        if (TryParseLineReference(input, out int lineNumber))
            return EvaluateLineFromMainCop(lineNumber);

        // Try as command name
        var commandResult = TryEvaluateCommand(input);
        if (commandResult is not null)
            return commandResult;

        // Try as let binding name (simple identifier)
        var letResult = TryEvaluateLetBinding(input);
        if (letResult is not null)
            return letResult;

        // Try as ad-hoc expression
        return EvaluateExpression(input);
    }

    private static bool TryParseLineReference(string input, out int lineNumber)
    {
        lineNumber = 0;
        // Line reference requires trailing '!': "13!" evaluates line 13 from main.cop
        if (input.EndsWith('!') && int.TryParse(input.AsSpan(0, input.Length - 1), out lineNumber))
            return lineNumber > 0;
        return false;
    }

    private List<string> EvaluateLineFromMainCop(int lineNumber)
    {
        var targetPath = ResolveLineReferenceFile();
        if (targetPath is null)
            return [$"Error: no .cop file found in {_context.ScriptsDir} (need exactly one file, or a main.cop)"];

        var lines = File.ReadAllLines(targetPath);
        if (lineNumber > lines.Length)
            return [$"Error: {Path.GetFileName(targetPath)} has only {lines.Length} lines (requested line {lineNumber})"];

        var line = lines[lineNumber - 1].Trim();
        if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith("//"))
            return [$"(line {lineNumber} is empty or a comment)"];

        // If line defines a named command or let binding, evaluate by name
        if (line.StartsWith("let ", StringComparison.Ordinal) || line.StartsWith("export let ", StringComparison.Ordinal))
        {
            var nameStart = line.IndexOf("let ", StringComparison.Ordinal) + 4;
            var nameEnd = nameStart;
            while (nameEnd < line.Length && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] == '-' || line[nameEnd] == '_'))
                nameEnd++;
            var name = line[nameStart..nameEnd];
            if (!string.IsNullOrEmpty(name))
            {
                // Try as command name first (let X = foreach ... => '...' is a command)
                var cmdResult = TryEvaluateCommand(name);
                if (cmdResult is not null) return cmdResult;
                // Then try as let binding
                var letResult = TryEvaluateLetBinding(name);
                if (letResult is not null) return letResult;
            }
        }
        else if (line.StartsWith("command ", StringComparison.Ordinal) || line.StartsWith("export command ", StringComparison.Ordinal))
        {
            var nameStart = line.IndexOf("command ", StringComparison.Ordinal) + 8;
            var nameEnd = nameStart;
            while (nameEnd < line.Length && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] == '-' || line[nameEnd] == '_'))
                nameEnd++;
            var name = line[nameStart..nameEnd];
            if (!string.IsNullOrEmpty(name))
            {
                var cmdResult = TryEvaluateCommand(name);
                if (cmdResult is not null) return cmdResult;
            }
        }

        // For multi-line constructs (e.g., object literals starting with '{'), gather
        // all lines until braces balance.
        var snippet = GatherBalancedBlock(lines, lineNumber - 1);

        // Otherwise evaluate as raw expression
        return EvaluateExpression(snippet);
    }

    /// <summary>
    /// Starting at startIndex, gather lines until braces are balanced.
    /// Returns the joined text. If no braces are involved, returns just the single line.
    /// </summary>
    private static string GatherBalancedBlock(string[] lines, int startIndex)
    {
        var first = lines[startIndex].Trim();
        int braceDepth = 0;
        foreach (char c in first)
        {
            if (c == '{') braceDepth++;
            else if (c == '}') braceDepth--;
        }

        if (braceDepth <= 0)
            return first;

        // Gather subsequent lines until braces balance
        var sb = new System.Text.StringBuilder(first);
        for (int i = startIndex + 1; i < lines.Length && braceDepth > 0; i++)
        {
            sb.Append(' ').Append(lines[i].Trim());
            foreach (char c in lines[i])
            {
                if (c == '{') braceDepth++;
                else if (c == '}') braceDepth--;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolves which .cop file to use for line references.
    /// If exactly one .cop file exists in the scripts dir (top-level), use it.
    /// Otherwise, use main.cop.
    /// </summary>
    private string? ResolveLineReferenceFile()
    {
        var topLevelFiles = Directory.GetFiles(_context.ScriptsDir, "*.cop", SearchOption.TopDirectoryOnly);
        if (topLevelFiles.Length == 1)
            return topLevelFiles[0];

        var mainCopPath = Path.Combine(_context.ScriptsDir, "main.cop");
        return File.Exists(mainCopPath) ? mainCopPath : null;
    }

    private List<string>? TryEvaluateCommand(string input)
    {
        var hasCommand = _context.Modules
            .SelectMany(m => m.Module.Declarations)
            .Any(d => (d is CommandDecl cmd && string.Equals(cmd.Name, input, StringComparison.OrdinalIgnoreCase))
                   || (d is FunctionDecl f && char.IsUpper(f.Name[0]) && f.Body is BlockBody && string.Equals(f.Name, input, StringComparison.OrdinalIgnoreCase)));

        if (!hasCommand)
            return null;

        EnsureProviders();
        return RunWithCommand(input);
    }

    private List<string>? TryEvaluateLetBinding(string input)
    {
        if (input.Contains(':') || input.Contains('.') || input.Contains('(') || input.Contains(' '))
            return null;

        var letDecl = _context.Modules
            .SelectMany(m => m.Module.Declarations)
            .OfType<LetDecl>()
            .FirstOrDefault(l => string.Equals(l.Name, input, StringComparison.OrdinalIgnoreCase));

        if (letDecl is null)
            return null;

        EnsureProviders();
        var snippet = $"foreach {input} => '{{item}}'";
        return EvaluateSnippet(snippet);
    }

    private List<string> EvaluateExpression(string input)
    {
        EnsureProviders();
        return EvaluateSnippet(input);
    }



    private List<string> EvaluateSnippet(string snippet)
    {
        // TODO: Migrate REPL snippet execution to the new evaluator pipeline.
        _ = snippet;
        return ["REPL evaluation not yet migrated to new pipeline"];
    }

    private List<string> RunWithCommand(string commandName)
    {
        // TODO: Migrate REPL command execution to the new evaluator pipeline.
        _ = commandName;
        return ["REPL evaluation not yet migrated to new pipeline"];
    }


    private void EnsureProviders()
    {
        if (!_context.ProvidersLoaded)
            Engine.LoadProviders(_context);
    }

    /// <summary>
    /// Gets all available identifiers for completion: commands, lets, collections.
    /// </summary>
    public List<string> GetCompletionCandidates()
    {
        EnsureProviders();
        var candidates = new List<string>();

        foreach (var module in _context.Modules)
        {
            foreach (var decl in module.Module.Declarations)
            {
                if (decl is CommandDecl cmd)
                    candidates.Add(cmd.Name);
                else if (decl is FunctionDecl f && char.IsUpper(f.Name[0]) && f.Body is BlockBody)
                    candidates.Add(f.Name);
                else if (decl is LetDecl let)
                    candidates.Add(let.Name);
            }
        }

        candidates.AddRange(_context.TypeRegistry.GetAllCollectionNames());
        candidates.AddRange(_context.TypeRegistry.GetProviderNamespaces());

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
    }

    /// <summary>
    /// Gets predicate names available for completion after a colon.
    /// </summary>
    public List<string> GetPredicateNames()
    {
        return _context.Modules
            .SelectMany(m => m.Module.Declarations)
            .OfType<FunctionDecl>()
            .Where(f => f.ReturnType?.Name == "bool" || char.IsLower(f.Name[0]))
            .Select(f => f.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s)
            .ToList();
    }

    /// <summary>
    /// Gets property names for a given type (for completion after a dot).
    /// </summary>
    public List<string> GetPropertyNames(string typeName)
    {
        var descriptor = _context.TypeRegistry.GetType(typeName);
        if (descriptor is null) return [];
        return descriptor.Properties.Keys.OrderBy(s => s).ToList();
    }

    /// <summary>
    /// Gets the item type for a collection (for completion after a dot on collection name).
    /// </summary>
    public string? GetCollectionItemType(string collectionName)
    {
        return _context.TypeRegistry.GetCollectionItemType(collectionName);
    }

    /// <summary>
    /// Gets all provider namespace names (e.g., "csharp", "files").
    /// </summary>
    public List<string> GetProviderNamespaces()
    {
        return _context.TypeRegistry.GetProviderNamespaces();
    }

    /// <summary>
    /// Gets collection names for a specific provider namespace.
    /// </summary>
    public List<string> GetNamespaceCollections(string ns)
    {
        EnsureProviders();
        return _context.TypeRegistry.GetNamespaceCollections(ns);
    }
}
