using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class MethodShapeRegressionTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Issue136_DuplicateShapesDoNotDemandDeferredBindingOrIgnoreLiteralDifferences(bool bindFirst)
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (!File.Exists(Path.Combine(directory.FullName, "cop.sln")))
        {
            directory = directory.Parent ?? throw new DirectoryNotFoundException("Repository root not found.");
        }
        string root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "shape-" + Guid.NewGuid().ToString("N"));
        string scripts = Path.Combine(root, "scripts");
        string target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(target, "Input.cs"), """
                class Input {
                    int value;
                    void Start() {}
                    void Step() {}
                    void Work(int number) {}
                    void Finish() {}
                    void Save() {}
                    void End() {}
                    void First() { Start(); Step(); Work(1); Finish(); Save(); End(); }
                    void Copy() { Start(); Step(); Work(1); Finish(); Save(); End(); }
                    void DifferentArgument() { Start(); Step(); Work(2); Finish(); Save(); End(); }
                    void AssignOne() { Start(); Step(); value = 1; Finish(); Save(); End(); }
                    void AssignTwo() { Start(); Step(); value = 2; Finish(); Save(); End(); }
                    void NestedOne() { Start(); Step(); Work(1); Finish(); Save(); End(); if (value > 0) { Work(1); } }
                    void NestedTwo() { Start(); Step(); Work(1); Finish(); Save(); End(); if (value > 0) { Work(2); } }
                    void ConditionOne() { Start(); Step(); Work(1); Finish(); Save(); End(); if (value > 1) { Work(3); } }
                    void ConditionTwo() { Start(); Step(); Work(1); Finish(); Save(); End(); if (value > 2) { Work(3); } }
                }
                """);
            File.WriteAllText(Path.Combine(scripts, "main.cop"), $$"""
                import code
                import csharp
                import code-quality-checks
                let cb = csharp.parse()
                command MAIN = {{(bindFirst ? "print(cb.Calls.Select(item.TypeName).Count) & " : "")}}
                    foreach duplicateMethodBodies(cb.Methods) => '{item.Check} {item.Line}'
                """);
            for (int run = 0; run < 2; run++)
            {
                var result = await Engine.RunAsync(scripts, target, new EngineRunOptions
                {
                    AdditionalFeedPaths = [Path.Combine(directory.FullName, "packages")]
                });
                Assert.That(result.HasParseErrors, Is.False, string.Join("\n", result.ParseErrors));
                Assert.That(result.HasFatalErrors, Is.False, string.Join("\n", result.Errors));
                string[] expected = bindFirst
                    ? ["56", "duplicateMethodBodies 9", "duplicateMethodBodies 10"]
                    : ["duplicateMethodBodies 9", "duplicateMethodBodies 10"];
                Assert.That(result.Outputs.Select(output => output.Message), Is.EqualTo(expected), $"Run {run}");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
