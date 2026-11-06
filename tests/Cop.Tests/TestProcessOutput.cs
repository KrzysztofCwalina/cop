using System.Diagnostics;
using NUnit.Framework;

namespace Cop.Tests;

internal static class TestProcessOutput
{
    internal static (string stdout, string stderr, int exitCode) Read(
        Process process, int timeoutMs)
    {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeoutMs))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.Fail($"Test process timed out after {timeoutMs} ms.");
        }

        return (stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult(),
            process.ExitCode);
    }
}
