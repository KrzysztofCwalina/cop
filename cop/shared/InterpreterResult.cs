namespace Cop.Lang;

/// <summary>
/// Result of running the interpreter, containing PRINT outputs and file outputs.
/// </summary>
public record InterpreterResult(
    List<PrintOutput> Outputs,
    List<FileOutput> FileOutputs);
