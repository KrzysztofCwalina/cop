// Exposes invocation arguments to the running Cop program.

namespace Cop.Lang;

/// <summary>
/// Contains the arguments supplied to the current Cop program invocation.
/// </summary>
public record ProgramInfo(List<string> Args);
