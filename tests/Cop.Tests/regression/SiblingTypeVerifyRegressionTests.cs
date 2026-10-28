using Cop.Cli.Commands;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class SiblingTypeVerifyRegressionTests
{
    [Test]
    [Explicit("Issue #87: typed parameters cannot bind sibling type declarations - remove when fixed")]
    [Category("PendingFix")]
    public void Issue087_VerifyResolvesSiblingParameterTypes()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "cop-issue087-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "a.cop"), "export type Widget = { Name : string }");
            File.WriteAllText(Path.Combine(root, "b.cop"), "export function name(Widget) => Widget.Name");
            var diagnostics = VerifyCommand.CollectDiagnostics(Directory.GetFiles(root, "*.cop"), root, File.ReadAllText);
            Assert.That(diagnostics, Is.Empty, string.Join("\n", diagnostics.Select(d => d.Message)));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
