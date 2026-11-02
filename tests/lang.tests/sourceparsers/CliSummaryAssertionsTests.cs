using System.Globalization;
using NUnit.Framework;

namespace Cop.Tests.Lang.SourceParsers;

internal static class CliSummaryAssertions
{
    internal static string NormalizeElapsedTime(string output, int files, int commands, int timeoutSeconds)
    {
        string summary = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Summary:", StringComparison.Ordinal)).TrimEnd('\r');
        string prefix = $"Summary: {files} input file{(files == 1 ? "" : "s")} processed, " +
            $"{commands} command{(commands == 1 ? "" : "s")} executed in ";
        Assert.That(summary, Does.StartWith(prefix).And.EndWith("s."));
        Assert.That(double.TryParse(summary[prefix.Length..^2], NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var seconds), Is.True);
        Assert.That(seconds, Is.InRange(0, timeoutSeconds));
        return output.Replace(summary, prefix + "<elapsed>s.", StringComparison.Ordinal);
    }
}

[TestFixture]
public class CliSummaryAssertionsTests
{
    [TestCase(1, 6, "0.43", "0.44")]
    [TestCase(2, 4, "1.08", "1.06")]
    public void NormalizationPreservesCountsAndDiagnosticsWhileIgnoringOnlyElapsedTime(
        int files, int commands, string coldSeconds, string warmSeconds)
    {
        string prefix = $"finding\nWarning: diagnostic\nSummary: {files} input file{(files == 1 ? "" : "s")} " +
            $"processed, {commands} commands executed in ";
        Assert.That(CliSummaryAssertions.NormalizeElapsedTime(prefix + coldSeconds + "s.\n", files, commands, 120),
            Is.EqualTo(prefix + "<elapsed>s.\n"));
        Assert.That(CliSummaryAssertions.NormalizeElapsedTime(prefix + warmSeconds + "s.\n", files, commands, 120),
            Is.EqualTo(prefix + "<elapsed>s.\n"));
    }
}
