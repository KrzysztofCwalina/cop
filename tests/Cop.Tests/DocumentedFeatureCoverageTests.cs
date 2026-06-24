using System.Text.RegularExpressions;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class DocumentedFeatureCoverageTests
{
    private static readonly IReadOnlyDictionary<string, FeatureCoverage> FeatureClassifications =
        new Dictionary<string, FeatureCoverage>(StringComparer.Ordinal)
        {
            [":"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["."] = CoveredBy("LanguageFeatureExecutionTests"),
            ["&"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["&&"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["||"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["!"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["|"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["=="] = CoveredBy("LanguageFeatureExecutionTests"),
            ["!="] = CoveredBy("LanguageFeatureExecutionTests"),
            [">"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["<"] = CoveredBy("LanguageFeatureExecutionTests"),
            [">="] = CoveredBy("LanguageFeatureExecutionTests"),
            ["<="] = CoveredBy("LanguageFeatureExecutionTests"),
            ["?"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["=>"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["+"] = CoveredBy("LanguageFeatureExecutionTests"),

            ["all"] = CoveredBy("doc-samples"),
            ["any"] = CoveredBy("CodebaseModelPopulationTests"),
            ["assert"] = CoveredBy("doc-samples"),
            ["Average"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["concat"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["contains"] = CoveredBy("CodebaseModelPopulationTests"),
            ["containsAny"] = CoveredBy("doc-samples"),
            ["containsKey"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["count"] = CoveredBy("doc-samples"),
            ["Count"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["debug"] = NotYetCovered("Diagnostic output intrinsic has no active execution test yet"),
            ["Distinct"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["ElementAt"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["empty"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["endsWith"] = CoveredBy("doc-samples"),
            ["equals"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["error"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["fail"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["First"] = CoveredBy("doc-samples"),
            ["Get"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["greaterOrEqual"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["greaterThan"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["GroupBy"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["in"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["isClear"] = CoveredBy("CodebaseModelPopulationTests"),
            ["isError"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["isSet"] = CoveredBy("CodebaseModelPopulationTests"),
            ["Keys"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["Last"] = CoveredBy("doc-samples"),
            ["Length"] = CoveredBy("doc-samples"),
            ["lessOrEqual"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["lessThan"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["Lower"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["matches"] = CoveredBy("CodebaseModelPopulationTests"),
            ["Matches"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["Max"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["Min"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["none"] = CoveredBy("doc-samples"),
            ["Normalized"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["notEquals"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["OrderBy"] = CoveredBy("doc-samples"),
            ["OrderByDescending"] = CoveredBy("doc-samples"),
            ["Path"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["pathMatches"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["print"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["program"] = NotYetCovered("Program context intrinsic has no active focused execution test yet"),
            ["provider"] = CoveredBy("EngineProviderIntegrationTests"),
            ["read"] = NotYetCovered("File read intrinsic has no active focused execution test yet"),
            ["Reduce"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["reduce"] = CoveredBy("EvaluatorTests"),
            ["Replace"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["sameAs"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["save"] = CoveredBy("doc-samples"),
            ["Select"] = CoveredBy("doc-samples"),
            ["Single"] = CoveredBy("doc-samples"),
            ["sink"] = NotYetCovered("Streaming sink intrinsic has no active execution test yet"),
            ["source"] = NotYetCovered("Streaming source intrinsic has no active execution test yet"),
            ["startsWith"] = CoveredBy("doc-samples"),
            ["Sum"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["text"] = NotYetCovered("Lowercase intrinsic overload is documented but not directly executed"),
            ["Text"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["Trim"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["Upper"] = CoveredBy("LanguageFeatureExecutionTests"),
            ["Values"] = CoveredBy("DocumentedOperationsExecutionTests"),
            ["Where"] = CoveredBy("doc-samples"),
            ["Words"] = CoveredBy("LanguageFeatureExecutionTests"),
        };

    [Test]
    public void EveryDocumentedIntrinsicIsClassified()
    {
        var intrinsics = ExtractDocumentedIntrinsicNames();
        ReportCoverageSummary(intrinsics, ExtractDocumentedLanguageReferenceFeatureNames());

        var missing = intrinsics
            .Where(name => !FeatureClassifications.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(missing, Is.Empty,
            "Documented intrinsic features without coverage classification: " + string.Join(", ", missing));
    }

    [Test]
    public void EveryDocumentedOperatorIsClassified()
    {
        var documentedFeatures = ExtractDocumentedLanguageReferenceFeatureNames();
        ReportCoverageSummary(ExtractDocumentedIntrinsicNames(), documentedFeatures);

        var missing = documentedFeatures
            .Where(name => !FeatureClassifications.ContainsKey(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(missing, Is.Empty,
            "Documented operators/predicates/transforms without coverage classification: " + string.Join(", ", missing));
    }

    [Test]
    public void NoStaleClassificationEntries()
    {
        var documented = new HashSet<string>(ExtractDocumentedIntrinsicNames(), StringComparer.Ordinal);
        documented.UnionWith(ExtractDocumentedLanguageReferenceFeatureNames());
        ReportCoverageSummary(ExtractDocumentedIntrinsicNames(), ExtractDocumentedLanguageReferenceFeatureNames());

        var stale = FeatureClassifications.Keys
            .Where(name => !documented.Contains(name))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.That(stale, Is.Empty,
            "Coverage classifications no longer backed by intrinsics.cop or language-reference.md: " + string.Join(", ", stale));
    }

    private static IReadOnlyCollection<string> ExtractDocumentedIntrinsicNames()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "packages", "core", "core", "src", "intrinsics.cop"));
        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in Regex.Matches(text, @"^\s*export\s+(?:function|predicate)\s+([A-Za-z_][A-Za-z0-9_]*)\s*\(",
            RegexOptions.Multiline))
        {
            names.Add(match.Groups[1].Value);
        }

        return names;
    }

    private static IReadOnlyCollection<string> ExtractDocumentedLanguageReferenceFeatureNames()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot, "docs", "language-reference.md"));
        var names = new HashSet<string>(StringComparer.Ordinal);

        AddMarkdownTableFeatures(text, names);
        AddCodeFormattedFeatures(text, names,
        [
            "Get", "containsKey", "Keys", "Values", "Count",
            "Length", "Lower", "Upper", "Normalized", "Words", "Trim", "Replace",
            "First", "Last", "Single", "any", "none", "all", "count",
            "Where", "ElementAt", "Select", "Text", "OrderBy", "OrderByDescending",
            "Distinct", "GroupBy", "Sum", "Min", "Max", "Average", "Reduce"
        ]);
        AddDocumentedOperators(text, names);

        return names;
    }

    private static void AddMarkdownTableFeatures(string text, ISet<string> names)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');

        for (var i = 0; i < lines.Length - 2; i++)
        {
            var headerCells = SplitMarkdownRow(lines[i]);
            var separatorCells = SplitMarkdownRow(lines[i + 1]);

            if (headerCells.Length == 0 || separatorCells.Length == 0 || !IsMarkdownSeparatorRow(separatorCells))
                continue;

            var firstHeader = headerCells[0].Trim();
            if (!string.Equals(firstHeader, "Predicate", StringComparison.Ordinal)
                && !string.Equals(firstHeader, "Function", StringComparison.Ordinal))
            {
                continue;
            }

            for (var row = i + 2; row < lines.Length; row++)
            {
                var cells = SplitMarkdownRow(lines[row]);
                if (cells.Length == 0 || IsMarkdownSeparatorRow(cells))
                    break;

                var firstCell = cells[0];
                var codeMatch = Regex.Match(firstCell, @"`([^`]+)`");
                if (!codeMatch.Success)
                    continue;

                var name = NormalizeFeatureName(codeMatch.Groups[1].Value);
                if (name.Length > 0)
                    names.Add(name);
            }
        }
    }

    private static void AddCodeFormattedFeatures(string text, ISet<string> names, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var pattern = $@"`[^`]*\b{Regex.Escape(candidate)}(?:\b|\()";
            if (Regex.IsMatch(text, pattern))
                names.Add(candidate);
        }
    }

    private static void AddDocumentedOperators(string text, ISet<string> names)
    {
        foreach (var op in new[] { ":", ".", "&", "&&", "||", "!", "|", "==", "!=", ">", "<", ">=", "<=", "?", "=>", "+" })
        {
            if (text.Contains($"`{op}`", StringComparison.Ordinal)
                || Regex.IsMatch(text, $@"(^|\s){Regex.Escape(op)}(\s|$)")
                || (op == "!" && (text.Contains("!A", StringComparison.Ordinal) || text.Contains(":!", StringComparison.Ordinal))))
            {
                names.Add(op);
            }
        }
    }

    private static string[] SplitMarkdownRow(string line)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
            return [];

        return trimmed.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
    }

    private static bool IsMarkdownSeparatorRow(IEnumerable<string> cells) =>
        cells.All(cell => Regex.IsMatch(cell, @"^:?-{3,}:?$"));

    private static string NormalizeFeatureName(string raw)
    {
        var name = raw.Trim();
        var parenIndex = name.IndexOf('(');
        if (parenIndex >= 0)
            name = name[..parenIndex];

        return Regex.IsMatch(name, @"^[A-Za-z_][A-Za-z0-9_]*$") ? name : string.Empty;
    }

    private static void ReportCoverageSummary(
        IReadOnlyCollection<string> documentedIntrinsics,
        IReadOnlyCollection<string> documentedReferenceFeatures)
    {
        var covered = FeatureClassifications.Values.Count(value => value.IsCovered);
        var notYetCovered = FeatureClassifications.Values.Count(value => !value.IsCovered);

        TestContext.WriteLine(
            $"Documented feature coverage: intrinsics={documentedIntrinsics.Count}, operators/predicates/transforms={documentedReferenceFeatures.Count}, CoveredBy={covered}, NotYetCovered={notYetCovered}");
    }

    private static FeatureCoverage CoveredBy(string suiteName) => new(true, suiteName);

    private static FeatureCoverage NotYetCovered(string reason) => new(false, reason);

    private static string RepoRoot
    {
        get
        {
            var dir = TestContext.CurrentContext.TestDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "cop.sln")))
                dir = Path.GetDirectoryName(dir);

            return dir ?? throw new InvalidOperationException("Could not find repo root (cop.sln)");
        }
    }

    private sealed record FeatureCoverage(bool IsCovered, string Detail);
}
