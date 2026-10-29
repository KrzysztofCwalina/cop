using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class RustParityIntegrationTests
{
    [Test]
    public void RustChecks_PanicMacrosAndOrdinaryFunctionsRemainDistinctColdAndWarm()
    {
        string? repository = TestContext.CurrentContext.TestDirectory;
        while (repository is not null && !File.Exists(Path.Combine(repository, "cop.sln")))
            repository = Path.GetDirectoryName(repository);
        Assert.That(repository, Is.Not.Null);
        string root = Path.Combine(Path.GetTempPath(), "cop-rust-check-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "lib.rs"), """
                fn run() {
                    panic!("boom");
                    unreachable!();
                    todo!();
                    unimplemented!();
                    panic("ordinary");
                }
                """);
            for (int run = 0; run < 2; run++)
            {
                var result = Engine.RunProject([Path.Combine(repository!, "packages")], ["rust-checks"], root, []);
                Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
                Assert.That(result.Outputs.Count(o => o.Message.Contains("Avoid panic!/unreachable!", StringComparison.Ordinal)),
                    Is.EqualTo(2), $"run {run}: both panic macros must retain their checks after the ! migration");
                Assert.That(result.Outputs.Count(o => o.Message.Contains("Unfinished code", StringComparison.Ordinal)),
                    Is.EqualTo(2), $"run {run}: both unfinished macros must be reported");
                Assert.That(result.Outputs, Has.Count.EqualTo(4),
                    $"run {run}: the ordinary panic function must not be reported as a macro");
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
