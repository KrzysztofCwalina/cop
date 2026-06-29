using System.Text.Json;
using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// `cop verify --json` exposes the real compiler's diagnostics (the same parse + import-resolution +
/// bind + type-check pipeline) as structured output, so editors and tools can consume the compiler
/// instead of reimplementing analysis. This is the additive foundation for editor diagnostics.
/// </summary>
[TestFixture]
public class VerifyJsonTests
{
    [Test]
    public void Verify_Json_EmitsStructuredDiagnostics_ForAnError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-verifyjson-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "bad.cop");
        File.WriteAllText(file, "command MAIN = doesNotExist\n");
        var original = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            int exit;
            try { exit = VerifyCommand.Execute(file, json: true); }
            finally { Console.SetOut(original); }

            Assert.That(exit, Is.EqualTo(1), "an error must yield exit code 1");

            using var doc = JsonDocument.Parse(sw.ToString());
            var root = doc.RootElement;
            Assert.That(root.GetProperty("errors").GetInt32(), Is.GreaterThan(0));

            var diags = root.GetProperty("diagnostics");
            Assert.That(diags.GetArrayLength(), Is.GreaterThan(0), "expected at least one diagnostic");

            var first = diags.EnumerateArray().First();
            Assert.That(first.GetProperty("severity").GetString(), Is.EqualTo("error"));
            Assert.That(first.GetProperty("message").GetString(), Does.Contain("doesNotExist"));
            Assert.That(first.GetProperty("line").GetInt32(), Is.GreaterThan(0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public void Verify_Json_EmitsEmptyDiagnostics_ForValidProgram()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cop-verifyjson-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "ok.cop"), "let greeting = 'hello'\n");
        var original = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            int exit;
            try { exit = VerifyCommand.Execute(dir, json: true); }
            finally { Console.SetOut(original); }

            Assert.That(exit, Is.EqualTo(0));
            using var doc = JsonDocument.Parse(sw.ToString());
            Assert.That(doc.RootElement.GetProperty("errors").GetInt32(), Is.EqualTo(0));
            Assert.That(doc.RootElement.GetProperty("diagnostics").GetArrayLength(), Is.EqualTo(0));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
