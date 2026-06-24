using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

/// <summary>
/// End-to-end tests for narrowing the common Codebase model to language-specific
/// subtypes — here, narrowing a Type to a CSharpType with `:asCSharp` and reading
/// C#-only fields (IsRecord, IsPartial) that have no place in the common model.
///
/// These exercise the real Engine.Run path: the runtime selects a per-item adapter
/// by CLR type so CSharpType.IsRecord resolves to real provider data even though the
/// Types collection is declared as [Type].
/// </summary>
[TestFixture]
public class CSharpNarrowingTests
{
    private static string PackagesDir
    {
        get
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
                dir = Path.GetDirectoryName(dir);
            return dir is not null
                ? Path.Combine(dir, "packages")
                : throw new InvalidOperationException("Could not find repo root (cop.sln)");
        }
    }

    private static (string ProgramDir, string TargetDir) CreateWorkspace(string program, string csharp)
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "cop-narrow-" + Guid.NewGuid().ToString("N")[..8]);
        var programDir = Path.Combine(baseDir, "program");
        var targetDir = Path.Combine(baseDir, "target");
        Directory.CreateDirectory(programDir);
        Directory.CreateDirectory(targetDir);
        File.WriteAllText(Path.Combine(programDir, "main.cop"), program);
        File.WriteAllText(Path.Combine(targetDir, "Fixture.cs"), csharp);
        return (programDir, targetDir);
    }

    [Test]
    public void AsCSharp_IsRecord_FlagsOnlyRecords()
    {
        const string csharp = """
            namespace Demo;
            public record Customer(string Name);
            public class Order { }
            public record struct Money(decimal Amount);
            """;

        const string program = """
            import code
            import code
            import csharp

            let codebase = codebase(csharp.parse())

            let record-violations = codebase.Types:asCSharp:isRecord
                :toError('{item.Name} is a record')

            command MAIN = CHECK(record-violations)
            """;

        var (programDir, targetDir) = CreateWorkspace(program, csharp);
        try
        {
            var diag = new List<string>();
            var result = Engine.Run(programDir, targetDir,
                diagLog: diag.Add, additionalFeedPaths: [PackagesDir]);

            Assert.That(result.HasFatalErrors, Is.False,
                "Errors: " + string.Join("; ", result.Errors) + ". Diag: " + string.Join("; ", diag));

            var messages = result.Outputs.Select(o => o.Message).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(messages.Any(m => m.Contains("Customer")), Is.True,
                    "Expected the record 'Customer' to be flagged. Got: " + string.Join(", ", messages));
                Assert.That(messages.Any(m => m.Contains("Money")), Is.True,
                    "Expected the record struct 'Money' to be flagged. Got: " + string.Join(", ", messages));
                Assert.That(messages.Any(m => m.Contains("Order")), Is.False,
                    "The plain class 'Order' must NOT be flagged. Got: " + string.Join(", ", messages));
            });
        }
        finally
        {
            TryDeleteParent(programDir);
        }
    }

    [Test]
    public void AsCSharp_IsPartial_FlagsOnlyPartialTypes()
    {
        const string csharp = """
            namespace Demo;
            public partial class Widget { }
            public class Gadget { }
            """;

        const string program = """
            import code
            import code
            import csharp

            let codebase = codebase(csharp.parse())

            let partial-violations = codebase.Types:asCSharp:isPartial
                :toError('{item.Name} is partial')

            command MAIN = CHECK(partial-violations)
            """;

        var (programDir, targetDir) = CreateWorkspace(program, csharp);
        try
        {
            var diag = new List<string>();
            var result = Engine.Run(programDir, targetDir,
                diagLog: diag.Add, additionalFeedPaths: [PackagesDir]);

            Assert.That(result.HasFatalErrors, Is.False,
                "Errors: " + string.Join("; ", result.Errors) + ". Diag: " + string.Join("; ", diag));

            var messages = result.Outputs.Select(o => o.Message).ToList();
            Assert.Multiple(() =>
            {
                Assert.That(messages.Any(m => m.Contains("Widget")), Is.True,
                    "Expected the partial class 'Widget' to be flagged. Got: " + string.Join(", ", messages));
                Assert.That(messages.Any(m => m.Contains("Gadget")), Is.False,
                    "The non-partial class 'Gadget' must NOT be flagged. Got: " + string.Join(", ", messages));
            });
        }
        finally
        {
            TryDeleteParent(programDir);
        }
    }

    private static void TryDeleteParent(string programDir)
    {
        try { Directory.Delete(Path.GetDirectoryName(programDir)!, recursive: true); } catch { }
    }
}
