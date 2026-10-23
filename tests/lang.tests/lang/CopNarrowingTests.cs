using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public class CopNarrowingTests
{
    private string _workspace = null!;
    private string _programDir = null!;
    private string _targetDir = null!;

    private static string RepoRoot
    {
        get
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
                dir = Path.GetDirectoryName(dir);
            return dir ?? throw new InvalidOperationException("Could not find cop.sln");
        }
    }

    [SetUp]
    public void SetUp()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "cop-functions-" + Guid.NewGuid().ToString("N"));
        _programDir = Path.Combine(_workspace, "program");
        _targetDir = Path.Combine(_workspace, "target");
        Directory.CreateDirectory(_programDir);
        Directory.CreateDirectory(_targetDir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_workspace, recursive: true);

    [Test]
    public void AsCop_ExposesFunctionFacts_WithoutIncludingCSharpMethods()
    {
        File.WriteAllText(Path.Combine(_targetDir, "sample.cop"), """
            ## Native function.
            export function native(value : string) : string => intrinsic
            function ordinary(value : string) : string => value
            predicate positive(value : int) => value > 0
            command MAIN = print('ok')
            """);
        File.WriteAllText(Path.Combine(_targetDir, "Sample.cs"),
            "public class Sample { public string Native(string value) => value; }");

        const string program = """
            import code
            import cop
            import csharp
            let cb = codebase(cop.parse(), csharp.parse())
            command MAIN = foreach cb.Methods:asCop =>
                '{item.Name}:{item.IsIntrinsic}:{item.IsPredicate}:{item.IsCommand}:{item.IsExported}:{item.File.Path}'
            """;

        string[] expected =
        [
            "native:true:false:false:true:sample.cop",
            "ordinary:false:false:false:false:sample.cop",
            "positive:false:true:false:false:sample.cop",
            "MAIN:false:false:true:false:sample.cop"
        ];
        Assert.That(Run(program), Is.EqualTo(expected));
        Assert.That(Run(program), Is.EqualTo(expected), "Facts must survive a source-cache hit.");
        Assert.That(Run(program.Replace("cb.Methods", "cb.Files.Methods", StringComparison.Ordinal)),
            Is.EqualTo(expected), "Top-level functions must also be accessible through their source files.");
    }

    [Test]
    public void IntrinsicPlacementRule_FlagsOnlyMisplacedCopIntrinsics()
    {
        File.WriteAllText(Path.Combine(_targetDir, "wrong.cop"), """
            export function misplaced(value : string) : string => intrinsic
            function ordinary() : string => 'intrinsic'
            export predicate misplacedPredicate(value : string) => intrinsic
            """);
        File.WriteAllText(Path.Combine(_targetDir, "intrinsics.cop"),
            "export function allowed(value : string) : string => intrinsic");
        File.WriteAllText(Path.Combine(_targetDir, "Sample.cs"),
            "public class Sample { public void intrinsic() { } }");
        File.Copy(Path.Combine(RepoRoot, "cop-checks", "cop", "intrinsic-declaration-location.cop"),
            Path.Combine(_programDir, "intrinsic-declaration-location.cop"));

        const string program = """
            import code
            import cop
            import csharp
            let codebase = codebase(cop.parse(), csharp.parse())
            command MAIN = CHECK(intrinsics-in-wrong-file)
            """;

        string[] expected =
        [
            "wrong.cop(1): error: Intrinsic declaration must be in intrinsics.cop, not wrong.cop",
            "wrong.cop(3): error: Intrinsic declaration must be in intrinsics.cop, not wrong.cop"
        ];
        Assert.That(Run(program), Is.EqualTo(expected));
        Assert.That(Run(program), Is.EqualTo(expected));
    }

    [Test]
    public void IntrinsicPlacementRule_AcceptsOrdinaryFunctionsAndAllowedIntrinsics()
    {
        File.WriteAllText(Path.Combine(_targetDir, "ordinary.cop"),
            "function ordinary() : string => 'intrinsic'");
        File.WriteAllText(Path.Combine(_targetDir, "intrinsics.cop"),
            "export function allowed(value : string) : string => intrinsic");
        File.Copy(Path.Combine(RepoRoot, "cop-checks", "cop", "intrinsic-declaration-location.cop"),
            Path.Combine(_programDir, "intrinsic-declaration-location.cop"));

        Assert.That(Run("""
            import code
            import cop
            let codebase = cop.parse()
            command MAIN = CHECK(intrinsics-in-wrong-file)
            """), Is.Empty);
    }

    private string[] Run(string program)
    {
        File.WriteAllText(Path.Combine(_programDir, "main.cop"), program);
        var result = Engine.Run(_programDir, _targetDir,
            additionalFeedPaths: [Path.Combine(RepoRoot, "packages")]);
        Assert.That(result.HasFatalErrors, Is.False, string.Join("; ", result.Errors));
        return result.Outputs.Select(o => o.Message).ToArray();
    }
}
