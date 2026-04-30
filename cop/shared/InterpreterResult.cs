namespace Cop.Lang;

/// <summary>
/// Result of running the interpreter, containing outputs and file outputs.
/// </summary>
public record InterpreterResult(
    List<PrintOutput> Outputs,
    List<FileOutput> FileOutputs,
    List<string> Warnings,
    List<AssertResult> Asserts);
