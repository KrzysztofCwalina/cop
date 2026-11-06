// Exit status and captured output from a change-analysis process.

using System.Diagnostics;
using System.Text;

namespace Cop.Core;

/// <summary>
/// Exit status and captured output from a change-analysis process.
/// </summary>
internal sealed record ChangeCommandResult(int ExitCode, string Output, string Error);
