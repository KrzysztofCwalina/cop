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
            ["nameof"] = CoveredBy("EvaluatorTests"),
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

    private static IReadOnlyCollection<string> ExtractDocumentedIntrinsicNames() =>
        ExtractDocumentedIntrinsicNames(
            File.ReadAllText(Path.Combine(RepoRoot, "packages", "core", "core", "src", "intrinsics.cop")));

    private static IReadOnlyCollection<string> ExtractDocumentedIntrinsicNames(string text)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var lineStart = 0; lineStart < text.Length;)
        {
            var declaration = text.AsSpan(lineStart).TrimStart();
            if (ConsumeKeyword(ref declaration, "export")
                && (ConsumeKeyword(ref declaration, "function") || ConsumeKeyword(ref declaration, "predicate")))
            {
                var length = 0;
                if (!declaration.IsEmpty && IsIdentifierStart(declaration[0]))
                {
                    length++;
                    while (length < declaration.Length && IsIdentifierPart(declaration[length]))
                        length++;
                }

                if (length > 0 && declaration[length..].TrimStart().StartsWith("(", StringComparison.Ordinal))
                    names.Add(declaration[..length].ToString());
            }

            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                break;
            lineStart = lineEnd + 1;
        }

        return names;
    }

    private static bool ConsumeKeyword(ref ReadOnlySpan<char> text, string keyword)
    {
        if (!text.StartsWith(keyword, StringComparison.Ordinal)
            || text.Length <= keyword.Length || !char.IsWhiteSpace(text[keyword.Length]))
            return false;

        text = text[keyword.Length..].TrimStart();
        return true;
    }

    private static bool IsIdentifierStart(char value) => char.IsAsciiLetter(value) || value == '_';

    private static bool IsIdentifierPart(char value) => IsIdentifierStart(value) || char.IsAsciiDigit(value);

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
                var code = FirstCodeSpan(firstCell);
                if (code is null)
                    continue;

                var name = NormalizeFeatureName(code);
                if (name.Length > 0)
                    names.Add(name);
            }
        }
    }

    private static string? FirstCodeSpan(string text)
    {
        var start = text.IndexOf('`');
        while (start >= 0)
        {
            var end = text.IndexOf('`', start + 1);
            if (end < 0)
                return null;
            if (end > start + 1)
                return text[(start + 1)..end];
            start = end;
        }
        return null;
    }

    private static void AddCodeFormattedFeatures(string text, ISet<string> names, IEnumerable<string> candidates)
    {
        var spans = text.Split('`').Skip(1).ToArray();
        foreach (var candidate in candidates)
        {
            if (spans.Any(span => ContainsWord(span, candidate)))
                names.Add(candidate);
        }
    }

    private static bool ContainsWord(string text, string word)
    {
        for (var start = text.IndexOf(word, StringComparison.Ordinal); start >= 0;
            start = text.IndexOf(word, start + 1, StringComparison.Ordinal))
        {
            var end = start + word.Length;
            if ((start == 0 || !TestTextParsing.IsWordCharacter(text[start - 1]))
                && (end == text.Length || !TestTextParsing.IsWordCharacter(text[end])))
                return true;
        }
        return false;
    }

    private static void AddDocumentedOperators(string text, ISet<string> names)
    {
        var tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var op in new[] { ":", ".", "&", "&&", "||", "!", "|", "==", "!=", ">", "<", ">=", "<=", "?", "=>", "+" })
        {
            if (text.Contains($"`{op}`", StringComparison.Ordinal)
                || tokens.Contains(op)
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
        cells.All(cell =>
        {
            var dashes = cell.AsSpan();
            if (dashes.StartsWith(":", StringComparison.Ordinal))
                dashes = dashes[1..];
            if (dashes.EndsWith(":", StringComparison.Ordinal))
                dashes = dashes[..^1];
            return dashes.Length >= 3 && dashes.IndexOfAnyExcept('-') < 0;
        });

    private static string NormalizeFeatureName(string raw)
    {
        var name = raw.Trim();
        var parenIndex = name.IndexOf('(');
        if (parenIndex >= 0)
            name = name[..parenIndex];

        return name.Length > 0 && IsIdentifierStart(name[0]) && name.All(IsIdentifierPart)
            ? name : string.Empty;
    }

    [Test]
    public void IntrinsicExtraction_RecognizesOnlyExportedFunctionAndPredicateDeclarations()
    {
        var names = ExtractDocumentedIntrinsicNames("""
             export function read_2 (value) => value
            export	predicate isGood(value) => true
            export
                function multiline
                (value) => value
            # export function comment(value) => value
            let text = 'export function quoted(value)'
            export function 2invalid(value) => value
            export function hyphen-name(value) => value
            export function café(value) => value
            export functions plural(value) => value
            function local(value) => value
            export function read_2(value) => value
            """);

        Assert.That(names.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "isGood", "multiline", "read_2" }));
    }

    [Test]
    public void MarkdownFeatureExtraction_RequiresFeatureTableAndValidIdentifier()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddMarkdownTableFeatures("""
            | Predicate | Meaning |
            | :--- | ---: |
            | `isGood(value)` | valid |
            | text `other_2()` | valid |
            | `bad-name()` | invalid identifier |
            | no code | ignored |

            | Function | Meaning |
            | :---: | ---- |
            | `Select` | valid |

            | Predicate | Meaning |
            | -- | --- |
            | `badSeparator` | ignored |

            | Type | Meaning |
            | --- | --- |
            | `NotAFeature` | ignored |
            """, names);

        Assert.That(names.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "Select", "isGood", "other_2" }));
    }

    [Test]
    public void CodeFormattedFeatures_RequireCaseSensitiveWordBoundaries()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddCodeFormattedFeatures(
            "`obj.Get(key)` `Select(value)` `Forget` `_Get` `Get2` `ΩSum` `Sum\u0301` `get`",
            names, ["Get", "Select", "Sum", "Missing"]);

        Assert.That(names.Order(StringComparer.Ordinal), Is.EqualTo(new[] { "Get", "Select" }));
    }

    [Test]
    public void Operators_AreRecognizedAsCodeOrWhitespaceDelimitedTokens()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        AddDocumentedOperators("&&\t>=\n`||` `+` a==b !A :! \t?", names);

        Assert.That(names.Order(StringComparer.Ordinal),
            Is.EqualTo(new[] { "!", "&&", "+", ">=", "?", "||" }));
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
