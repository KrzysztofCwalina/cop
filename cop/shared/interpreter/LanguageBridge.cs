namespace Cop.Lang.Interpreter;

using Cop.Lang.Ast;
using Cop.Lang.Parser;

/// <summary>
/// The Language Bridge connects the clean new evaluator to the existing runtime layer.
/// It handles:
/// - Parsing .cop source via the new parser
/// - Registering provider-supplied data as evaluator values
/// - Running commands through the evaluator
/// - Collecting output from intrinsic functions
///
/// This is the single integration point between the domain-free evaluator
/// and the domain-specific runtime (providers, sinks, packages).
/// </summary>
public sealed class LanguageBridge
{
    private readonly ForeignFunctionRegistry _ffi;
    private readonly Evaluator _evaluator;
    private readonly List<string> _outputs = [];
    private readonly List<string> _errors = [];

    /// <summary>
    /// Output lines produced by print/debug/template rendering.
    /// </summary>
    public IReadOnlyList<string> Outputs => _outputs;

    /// <summary>
    /// Error messages from failed assertions or `fail` calls.
    /// </summary>
    public IReadOnlyList<string> Errors => _errors;

    /// <summary>
    /// The underlying evaluator (for advanced access).
    /// </summary>
    public Evaluator Evaluator => _evaluator;

    public LanguageBridge(string? filePath = null)
    {
        _ffi = new ForeignFunctionRegistry();
        StandardLibrary.Register(_ffi, OnOutput);
        _evaluator = new Evaluator(_ffi, filePath);
    }

    /// <summary>
    /// Create a bridge with a custom FFI registry (for testing or extension).
    /// </summary>
    public LanguageBridge(ForeignFunctionRegistry ffi, string? filePath = null)
    {
        _ffi = ffi;
        _evaluator = new Evaluator(_ffi, filePath);
    }

    // ========================================================================
    // Source Loading
    // ========================================================================

    /// <summary>
    /// Parse and load a .cop source file into the evaluator.
    /// </summary>
    public void LoadSource(string source, string filePath = "<unknown>")
    {
        var module = CopParser.Parse(source, filePath);
        _evaluator.EvalModule(module);
    }

    /// <summary>
    /// Parse and load a .cop source file from disk.
    /// </summary>
    public void LoadFile(string path)
    {
        var source = File.ReadAllText(path);
        LoadSource(source, path);
    }

    // ========================================================================
    // Provider Data Registration
    // ========================================================================

    /// <summary>
    /// Register a collection of DataObjects as a named value in the evaluator.
    /// Provider data becomes accessible as a list of dynamic objects.
    /// </summary>
    public void RegisterCollection(string name, IReadOnlyList<DataObject> items)
    {
        var copItems = items.Select(item =>
            (CopValue)new CopDynamicObject(item, DataObjectAdapter.Instance)).ToList();
        _evaluator.GlobalEnvironment.Define(name, new CopList(copItems));
    }

    /// <summary>
    /// Register a lazy collection (for streaming providers).
    /// </summary>
    public void RegisterLazyCollection(string name, Func<IEnumerable<DataObject>> factory)
    {
        var lazy = new CopLazyCollection(() =>
            factory().Select(item =>
                (CopValue)new CopDynamicObject(item, DataObjectAdapter.Instance)));
        _evaluator.GlobalEnvironment.Define(name, lazy);
    }

    /// <summary>
    /// Register a single DataObject as a named value.
    /// </summary>
    public void RegisterObject(string name, DataObject obj)
    {
        _evaluator.GlobalEnvironment.Define(name,
            new CopDynamicObject(obj, DataObjectAdapter.Instance));
    }

    /// <summary>
    /// Register a simple named value.
    /// </summary>
    public void RegisterValue(string name, CopValue value)
    {
        _evaluator.GlobalEnvironment.Define(name, value);
    }

    /// <summary>
    /// Register an additional foreign function.
    /// </summary>
    public void RegisterFunction(string name, ForeignFunction impl, int arity = -1)
    {
        _ffi.Register(name, impl, arity);
        // Also define in the evaluator's current global env
        _evaluator.GlobalEnvironment.Define(name, _ffi.Resolve(name)!);
    }

    // ========================================================================
    // Execution
    // ========================================================================

    /// <summary>
    /// Run a named command and return its result.
    /// Catches CopEvaluationException and stores in Errors (caller must check bridge.Errors).
    /// </summary>
    public CopValue RunCommand(string name = "main")
    {
        try
        {
            return _evaluator.RunCommand(name);
        }
        catch (CopEvaluationException ex)
        {
            _errors.Add(ex.Message);
            return CopNull.Instance;
        }
    }

    /// <summary>
    /// Evaluate a single expression and return its result.
    /// </summary>
    public CopValue EvalExpression(string exprSource)
    {
        var source = $"command __eval__ = {exprSource}";
        var module = CopParser.Parse(source, "<eval>");
        _evaluator.EvalModule(module);
        return _evaluator.RunCommand("__eval__");
    }

    // ========================================================================
    // Callbacks
    // ========================================================================

    private void OnOutput(string text)
    {
        _outputs.Add(text);
    }
}
