using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

/// <summary>
/// Focused end-to-end coverage for documented language OPERATIONS that previously had no direct
/// execution test (and several that were broken at runtime until fixed): collection aggregates and
/// transforms (Sum/Min/Max/Average/Distinct/GroupBy/Reduce/Text), string transforms (Trim/Replace),
/// numeric and string predicates (greaterOrEqual/lessThan/lessOrEqual/notEquals/sameAs), object
/// operations (Get/Keys/Values/containsKey/Count), glob/regex built-ins (pathMatches/Path/Matches),
/// collection combinators (concat), and error handling (error/isError).
///
/// Every assertion checks an EXACT computed value so a silently-empty or wrong result fails the test.
/// Programs run in-process against a small, stable C# fixture (4 methods with 0/1/1/2 parameters).
/// </summary>
[TestFixture]
public class DocumentedOperationsExecutionTests
{
    private static string RepoRoot => FindRepoRoot();
    private static string PackagesDir => Path.Combine(RepoRoot, "packages");

    private static string FindRepoRoot()
    {
        var dir = TestContext.CurrentContext.TestDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
    }

    // Alpha.M1(int), Alpha.M2(int,int), Alpha.M3(int), Beta.N1()  →  param counts 1,2,1,0
    private const string FixtureCSharp = """
        namespace Probe;
        public class Alpha {
            public void M1(int a) { }
            public void M2(int a, int b) { }
            public void M3(int a) { }
        }
        public class Beta {
            public void N1() { }
        }
        """;

    private static IReadOnlyList<string> Run(string program, bool withCSharpFixture = false)
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "documented-operations", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(scripts, "program.cop"), program);
            if (withCSharpFixture)
                File.WriteAllText(Path.Combine(target, "Fixture.cs"), FixtureCSharp);

            var result = Engine.Run(scripts, target, additionalFeedPaths: [PackagesDir]);

            Assert.That(result.HasParseErrors, Is.False, string.Join(Environment.NewLine, result.ParseErrors));
            Assert.That(result.HasFatalErrors, Is.False, string.Join(Environment.NewLine, result.Errors));
            return result.Outputs.Select(o => o.Message).ToArray();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ---- Collection aggregates ------------------------------------------------------------------

    [Test]
    public void Aggregates_SumMinMaxAverage_OverMethodParameterCounts()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main =
                print(cb.Methods.Sum(item.Parameters.Count)) &
                print(cb.Methods.Min(item.Parameters.Count)) &
                print(cb.Methods.Max(item.Parameters.Count)) &
                print(cb.Methods.Average(item.Parameters.Count))
            """, withCSharpFixture: true);

        Assert.That(outputs, Is.EqualTo(new[] { "4", "0", "2", "1" }));
    }

    [Test]
    public void Distinct_ByKeyExpression_DeduplicatesParameterCounts()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main = print(cb.Methods.Distinct(item.Parameters.Count).Count)
            """, withCSharpFixture: true);

        // Distinct param counts present are {0, 1, 2}.
        Assert.That(outputs, Is.EqualTo(new[] { "3" }));
    }

    [Test]
    public void GroupBy_GroupsItemsWithKeyAndCount()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main = foreach cb.Methods.GroupBy(item.Parameters.Count).OrderBy(item.Key)
                => '{item.Key}:{item.Count}'
            """, withCSharpFixture: true);

        // 0 params: N1 (1); 1 param: M1,M3 (2); 2 params: M2 (1).
        Assert.That(outputs, Is.EqualTo(new[] { "0:1", "1:2", "2:1" }));
    }

    [Test]
    public void Reduce_StringOperator_SumsNumbersAndJoinsStrings()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main =
                print(cb.Methods.Reduce('+', item.Parameters.Count)) &
                print(cb.Types.OrderBy(item.Name).Reduce('+', item.Name, ', '))
            """, withCSharpFixture: true);

        Assert.That(outputs, Is.EqualTo(new[] { "4", "Alpha, Beta" }));
    }

    [Test]
    public void CollectionText_RendersTemplatePerItemAndJoins()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main = print(cb.Types.OrderBy(item.Name).Text('{item.Name}'))
            """, withCSharpFixture: true);

        Assert.That(outputs, Has.Count.EqualTo(1));
        Assert.That(outputs[0], Does.Contain("Alpha"));
        Assert.That(outputs[0], Does.Contain("Beta"));
    }

    [Test]
    public void ScalarText_ConvertsValueToText()
    {
        var outputs = Run("""
            let n = 42
            command main = print(Text(n)) & print(n:Text)
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "42", "42" }));
    }

    // ---- Collection transforms / queries --------------------------------------------------------

    [Test]
    public void SelectWhereOrderByElementAtFirstLast_ProduceExactResults()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main =
                print(cb.Types.OrderBy(item.Name).First.Name) &
                print(cb.Types.OrderBy(item.Name).Last.Name) &
                print(cb.Types.OrderBy(item.Name).ElementAt(1).Name) &
                print(cb.Types.OrderByDescending(item.Name).First.Name) &
                print(cb.Types.Where(item.Name == 'Alpha').Count)
            """, withCSharpFixture: true);

        Assert.That(outputs, Is.EqualTo(new[] { "Alpha", "Beta", "Beta", "Beta", "1" }));
    }

    // ---- String transforms ----------------------------------------------------------------------

    [Test]
    public void StringTransforms_TrimSuffixAndReplaceSubstring()
    {
        var outputs = Run("""
            command main =
                print('GetItemAsync'.Trim('Async')) &
                print('GetItem'.Replace('Get', 'Set'))
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "GetItem", "SetItem" }));
    }

    // ---- Numeric / string predicates ------------------------------------------------------------

    [Test]
    public void NumericPredicates_FilterMethodsByParameterCount()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main =
                print(cb.Methods.Where(item.Parameters.Count:greaterOrEqual(2)).Count) &
                print(cb.Methods.Where(item.Parameters.Count:greaterThan(1)).Count) &
                print(cb.Methods.Where(item.Parameters.Count:lessThan(1)).Count) &
                print(cb.Methods.Where(item.Parameters.Count:lessOrEqual(1)).Count) &
                print(cb.Methods.Where(item.Parameters.Count:notEquals(0)).Count)
            """, withCSharpFixture: true);

        // counts: >=2 → M2 (1); >1 → M2 (1); <1 → N1 (1); <=1 → M1,M3,N1 (3); !=0 → M1,M2,M3 (3).
        Assert.That(outputs, Is.EqualTo(new[] { "1", "1", "1", "3", "3" }));
    }

    [Test]
    public void StringPredicates_SameAsAndNotEquals()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main =
                print(cb.Types.Where(item.Name:sameAs('alpha')).Count) &
                print(cb.Types.Where(item.Name:notEquals('Alpha')).Count)
            """, withCSharpFixture: true);

        // sameAs is convention-insensitive → 'alpha' matches 'Alpha' (1); notEquals 'Alpha' → Beta (1).
        Assert.That(outputs, Is.EqualTo(new[] { "1", "1" }));
    }

    // ---- Object operations ----------------------------------------------------------------------

    [Test]
    public void ObjectOperations_GetContainsKeyKeysValuesCount()
    {
        var outputs = Run("""
            let p = { Name = 'Alice' Age = 42 }
            command main =
                print(p.Get('Name')) &
                print(p:containsKey('Age')) &
                print(p:containsKey('Missing')) &
                print(p.Keys.Count) &
                print(p.Values.Count) &
                print(p.Count)
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "Alice", "true", "false", "2", "2", "2" }));
    }

    // ---- Glob / regex built-ins -----------------------------------------------------------------

    [Test]
    public void PathMatches_HandlesStarDoubleStarAndQuestionGlobs()
    {
        var outputs = Run("""
            command main =
                print(pathMatches('a/b/c.cs', '**/*.cs')) &
                print(pathMatches('c.cs', '*.cs')) &
                print(pathMatches('c.cs', '?.cs')) &
                print(pathMatches('a/b.txt', '**/*.cs'))
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "true", "true", "true", "false" }));
    }

    [Test]
    public void PathAndMatchesBuiltins_FilterFilesAndMethods()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main =
                print(cb.Methods.Where(item.Name:Matches('M.')).Count) &
                print(cb.Files.Where(item.Path:Path('**/*.cs')).Count)
            """, withCSharpFixture: true);

        // Matches('M.') → M1,M2,M3 (3); Path('**/*.cs') → the single Fixture.cs (1).
        Assert.That(outputs, Is.EqualTo(new[] { "3", "1" }));
    }

    // ---- Collection combinators -----------------------------------------------------------------

    [Test]
    public void Concat_PipeAndFunctionFormsCombineCollections()
    {
        var outputs = Run("""
            import code
            import csharp
            let cb = csharp.parse()
            command main =
                print(cb.Types:concat(cb.Types).Count) &
                print(concat(cb.Types, cb.Types).Count)
            """, withCSharpFixture: true);

        Assert.That(outputs, Is.EqualTo(new[] { "4", "4" }));
    }

    // ---- Error handling -------------------------------------------------------------------------

    [Test]
    public void IsError_DetectsErrorValuesAsFilterAndFunction()
    {
        var outputs = Run("""
            command main =
                print([error('boom')]:isError.Count) &
                print(['ok' error('x')]:isError.Count) &
                print(isError(error('y'))) &
                print(isError('not an error'))
            """);

        Assert.That(outputs, Is.EqualTo(new[] { "1", "1", "true", "false" }));
    }

    [Test]
    public void Fail_TerminatesWithFatalDiagnostic()
    {
        var root = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "documented-operations", Guid.NewGuid().ToString("N"));
        var scripts = Path.Combine(root, "scripts");
        var target = Path.Combine(root, "target");
        Directory.CreateDirectory(scripts);
        Directory.CreateDirectory(target);
        try
        {
            File.WriteAllText(Path.Combine(scripts, "program.cop"), "command main = fail('boom')");

            var result = Engine.Run(scripts, target, additionalFeedPaths: [PackagesDir]);

            Assert.That(result.HasFatalErrors, Is.True, "fail() must terminate with a fatal error");
            Assert.That(string.Join(" ", result.Errors), Does.Contain("boom"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
