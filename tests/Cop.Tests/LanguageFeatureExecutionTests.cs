using System.Diagnostics;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class LanguageFeatureExecutionTests
{
    private static string RepoRoot => FindRepoRoot();
    private static string PackagesDir => Path.Combine(RepoRoot, "packages");
    private static string CopExe => Path.Combine(RepoRoot, "install", "win-x64", "cop.exe");

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    private static IReadOnlyList<string> RunInProc(string source, string commandName = "main")
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "language-feature-execution", Guid.NewGuid().ToString("N"));
        var scriptsDir = Path.Combine(root, "scripts");
        var targetDir = Path.Combine(root, "target");
        Directory.CreateDirectory(scriptsDir);
        Directory.CreateDirectory(targetDir);

        try
        {
            File.WriteAllText(Path.Combine(scriptsDir, "program.cop"), source);
            var result = Engine.Run(scriptsDir, targetDir, commandName: commandName, additionalFeedPaths: [PackagesDir]);

            Assert.That(result.HasParseErrors, Is.False, string.Join(Environment.NewLine, result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join(Environment.NewLine, result.Errors));

            return result.Outputs.Select(o => o.Message).ToArray();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static (int ExitCode, string Stdout) RunCli(string source)
    {
        if (!File.Exists(CopExe))
            Assert.Ignore($"Published cop.exe not found at {CopExe}; run install/publish.ps1 first.");

        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory, "language-feature-execution-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var script = Path.Combine(root, "program.cop");

        try
        {
            File.WriteAllText(script, source);

            var psi = new ProcessStartInfo
            {
                FileName = CopExe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = RepoRoot,
            };
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(root);

            using var process = Process.Start(psi)!;
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(60_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                Assert.Fail("cop.exe timed out.");
            }

            return (process.ExitCode, stdoutTask.GetAwaiter().GetResult().Trim());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Test]
    [Explicit("Issue #46: list ElementAt/Distinct/Sum/Max on list values fail or return empty values at runtime — remove when fixed")]
    [Category("PendingFix")]
    public void ListAggregatesAndTransforms_ElementAtDistinctSumMax_RunOnListValue()
    {
        // Issue #46
        var outputs = RunInProc("""
            let Items = [1 2 2 3]
            command main =
                print(Items.ElementAt(1)) &
                print(Items.Distinct().Count) &
                print(Items.Sum(item)) &
                print(Items.Max(item))
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "2", "3", "8", "3" }));
    }

    [Test]
    [Explicit("Issue #45: list append [1 2] + 3 fails as non-numeric/non-string addition — remove when fixed")]
    [Category("PendingFix")]
    public void ListAppend_AppendsSingleValue()
    {
        // Issue #45
        var outputs = RunInProc("""
            let Items = [1 2] + 3
            command main = foreach Items => '{item}'
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "1", "2", "3" }));
    }

    [Test]
    [Explicit("Issue #44: value pipe value:function returns the original value instead of invoking the function — remove when fixed")]
    [Category("PendingFix")]
    public void ValuePipe_CallsFunctionOnValue()
    {
        // Issue #44
        var outputs = RunInProc("""
            function inc(n) => n + 1
            let Six = 5:inc
            command main = print(Six)
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "6" }));
    }

    [Test]
    [Explicit("Issue #43: object .Get is not callable and quoted object keys do not parse — remove when fixed")]
    [Category("PendingFix")]
    public void ObjectOperations_GetKeysAndQuotedKeys_Work()
    {
        // Issue #43
        var outputs = RunInProc("""
            let Person = { Name: 'Ada' 'quoted-key': 7 }
            command main =
                print(Person.Get('Name')) &
                print(Person.Get('quoted-key')) &
                print(Person.Keys.Count)
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "Ada", "7", "2" }));
    }

    [Test]
    [Explicit("Issue #42: documented string properties Lower/Upper/Normalized/Words are unknown at runtime — remove when fixed")]
    [Category("PendingFix")]
    public void StringProperties_LowerUpperNormalizedWords_Work()
    {
        // Issue #42
        var outputs = RunInProc("""
            let Name = 'Foo_Bar'
            command main =
                print(Name.Lower) &
                print(Name.Upper) &
                print(Name.Normalized) &
                foreach Name.Words => print('{item}')
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "foo_bar", "FOO_BAR", "foobar", "foo", "bar" }));
    }

    [Test]
    [Explicit("Issue #41: verbatim string token @'...' is rejected by the parser — remove when fixed")]
    [Category("PendingFix")]
    public void VerbatimStrings_Tokenize()
    {
        // Issue #41
        var outputs = RunInProc("""
            command main = print(@'a\nb')
            """);

        Assert.That(outputs, Is.EqualTo(new[] { @"a\nb" }));
    }

    [Test]
    [Explicit("Issue #40: match expression with _ wildcard does not parse/evaluate — remove when fixed")]
    [Category("PendingFix")]
    public void MatchExpression_Wildcard_ReturnsMatchedArm()
    {
        // Issue #40
        var outputs = RunInProc("""
            let Result = 'x' ? 'y' => 'no' | _ => 'yes'
            command main = print(Result)
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "yes" }));
    }

    [Test]
    [Explicit("Issue #39: interpolation of non-member expressions prints the literal braces instead of the value — remove when fixed")]
    [Category("PendingFix")]
    public void StringInterpolation_NonMemberExpression_EvaluatesExpression()
    {
        // Issue #39
        var outputs = RunInProc("""
            command main = print('{1 + 2}')
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "3" }));
    }

    [Test]
    [Explicit("Issue #38: bare top-level expressions report 'No commands defined' instead of printing their value — remove when fixed")]
    [Category("PendingFix")]
    public void BareTopLevelExpression_ProducesOutput()
    {
        // Issue #38
        var (exitCode, stdout) = RunCli("""
            1 + 2
            """);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout, Is.EqualTo("3"));
    }

    [Test]
    [Explicit("Issue #37: successful foreach/report output exits 1 instead of 0 — remove when fixed")]
    [Category("PendingFix")]
    public void SuccessfulForeachReportProgram_ExitsZero()
    {
        // Issue #37
        var (exitCode, stdout) = RunCli("""
            command main = foreach [1 2] => '{item}'
            """);

        Assert.That(exitCode, Is.EqualTo(0));
        Assert.That(stdout, Is.EqualTo($"1{Environment.NewLine}2"));
    }

    [Test]
    public void BooleanPredicateComposition_AndHonorsRightHandPredicate()
    {
        // Issue #23
        var outputs = RunInProc("""
            predicate A(int) => item == 1
            predicate B(int) => item == 2
            predicate C(int) => item == 2
            predicate isMatch(int) => (A || B) && C
            command main = foreach [1 2 3]:isMatch => '{item}'
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "2" }));
    }

    [Test]
    public void SoleNegatedBarePredicate_IsInvokedPerItem()
    {
        // Issue #36
        var outputs = RunInProc("""
            predicate isOne(int) => item == 1
            predicate isNotOne(int) => !isOne
            command main = foreach [1 2]:isNotOne => '{item}'
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "2" }));
    }

    [Test]
    public void CollectionEmpty_ReturnsFalseForNonEmptyAndTrueForEmpty()
    {
        // Issue #32
        var outputs = RunInProc("""
            command main =
                print([1]:empty) &
                print([]:empty)
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "false", "true" }));
    }

    [Test]
    public void InMembershipFilter_MatchesRightElements()
    {
        // Issue #1
        var outputs = RunInProc("""
            command main = foreach [1 2 3]:in([1 3]) => '{item}'
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "1", "3" }));
    }

    [Test]
    [Explicit("Issue #34: curried function used as a filter receives the item as an extra argument and crashes — remove when fixed")]
    [Category("PendingFix")]
    public void CurriedFunction_UsedAsFilter_DoesNotCrash()
    {
        // Issue #34
        var outputs = RunInProc("""
            function greaterThan(limit) => item > limit
            command main = foreach [1 2 3]:greaterThan(1) => '{item}'
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "2", "3" }));
    }

    [Test]
    public void IdentifierA_CommandNameA_Works()
    {
        // Issue #9
        var outputs = RunInProc("""
            command a = print('ok')
            """, commandName: "a");

        Assert.That(outputs, Is.EqualTo(new[] { "ok" }));
    }

    [Test]
    public void MultiLineColonFilterPipeChains_ProduceResults()
    {
        // Issue #4
        var outputs = RunInProc("""
            predicate isPositive(int) => item > 0
            command main = foreach [0 1 2]
                :isPositive
                => '{item}'
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "1", "2" }));
    }
}
