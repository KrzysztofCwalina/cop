// Coordinates parsing, binding, type checking, and syntax-tree evaluation.

namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;
using Cop.Lang.Parser;

/// <summary>
/// Runs .cop packages through the new clean pipeline (Parser → AST → Evaluator).
/// This is the migration path: Engine.RunProject() will eventually route through here.
///
/// Current capabilities:
/// - Parse .cop source via CopParser (clean recursive-descent)
/// - Register provider data (DataObject collections) in the evaluator
/// - Execute commands through the LanguageBridge
/// - Standard library intrinsics (print, save, debug, assert, fail, text, read, etc.)
///
/// The new pipeline is domain-free: all domain behavior (providers, streaming, sinks)
/// is injected via FFI registrations, never hardcoded in the evaluator.
/// </summary>
public sealed class NewPipelineRunner
{
    private readonly LanguageBridge _bridge;
    private readonly List<string> _parseErrors = [];

    /// <summary>
    /// All outputs produced during execution.
    /// </summary>
    public IReadOnlyList<string> Outputs => _bridge.Outputs;

    /// <summary>
    /// All errors produced during execution.
    /// </summary>
    public IReadOnlyList<string> Errors => _bridge.Errors;

    /// <summary>
    /// Parse errors from source loading.
    /// </summary>
    public IReadOnlyList<string> ParseErrors => _parseErrors;

    public NewPipelineRunner()
    {
        _bridge = new LanguageBridge();
    }

    /// <summary>
    /// Load all .cop source files from a package directory.
    /// </summary>
    public async ValueTask<bool> LoadPackageDirAsync(string srcDir, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(srcDir))
        {
            _parseErrors.Add($"Directory not found: {srcDir}");
            return false;
        }

        string[] copFiles = Directory.GetFiles(srcDir, "*.cop");
        Array.Sort(copFiles, StringComparer.Ordinal);

        foreach (string file in copFiles)
        {
            try
            {
                string source = File.ReadAllText(file);
                await _bridge.LoadSourceAsync(source, file, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ParseException or CopEvaluationException)
            {
                _parseErrors.Add($"{file}: {ex.Message}");
            }
        }

        return _parseErrors.Count == 0;
    }

    /// <summary>
    /// Load a single .cop source string.
    /// </summary>
    public async ValueTask<bool> LoadSourceAsync(string source, string path = "<input>", CancellationToken cancellationToken = default)
    {
        try
        {
            await _bridge.LoadSourceAsync(source, path, cancellationToken: cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is ParseException or CopEvaluationException)
        {
            _parseErrors.Add($"{path}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Register provider data collection.
    /// </summary>
    public void RegisterCollection(string name, IReadOnlyList<DataObject> items)
        => _bridge.RegisterCollection(name, items);

    /// <summary>
    /// Register a single value.
    /// </summary>
    public void RegisterValue(string name, CopValue value)
        => _bridge.RegisterValue(name, value);

    /// <summary>
    /// Run a named command (defaults to "main").
    /// </summary>
    public async ValueTask<CopValue> RunAsync(string command = "main", CancellationToken cancellationToken = default)
        => (await _bridge.RunCommandAsync(command, cancellationToken: cancellationToken));

    /// <summary>
    /// Access the underlying bridge for advanced scenarios.
    /// </summary>
    public LanguageBridge Bridge => _bridge;
}
