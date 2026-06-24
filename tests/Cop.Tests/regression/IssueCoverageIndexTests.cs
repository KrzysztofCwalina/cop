using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class IssueRegressionCoverageIndexTests
{
    private enum CoverageKind
    {
        ImplementedHere,
        CoveredBy,
        OutOfScope,
    }

    private sealed record IssueCoverage(int Issue, CoverageKind Kind, string Detail);

    private static readonly IssueCoverage[] Coverage =
    [
        new(1, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(2, CoverageKind.OutOfScope, "install/platform self-update failure not runnable on win-x64 CI"),
        new(3, CoverageKind.CoveredBy, "CodebaseModelPopulationTests"),
        new(4, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(5, CoverageKind.ImplementedHere, "IssueRegressionTests.Issue005_SingleFileRunIsIsolatedButFolderVerifyRejectsDuplicateMain"),
        new(6, CoverageKind.CoveredBy, "DeterminismAndScaleTests"),
        new(7, CoverageKind.OutOfScope, "installer/self-update target location not runnable on win-x64 CI"),
        new(8, CoverageKind.ImplementedHere, "IssueRegressionTests.Issue008_ProviderProgramInChecksFolderScansTargetRoot"),
        new(9, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(10, CoverageKind.ImplementedHere, "IssueRegressionTests.Issue010_ExplicitSingleFileRunKeepsFileSelectionAndViolationExitCode"),
        new(11, CoverageKind.OutOfScope, "cop init hook generation/install behavior not runnable in this regression test project"),
        new(12, CoverageKind.CoveredBy, "DeterminismAndScaleTests"),
        new(13, CoverageKind.CoveredBy, "DeterminismAndScaleTests"),
        new(14, CoverageKind.CoveredBy, "DeterminismAndScaleTests"),
        new(15, CoverageKind.CoveredBy, "CodebaseModelPopulationTests"),
        new(16, CoverageKind.CoveredBy, "DeterminismAndScaleTests"),
        new(17, CoverageKind.CoveredBy, "DeterminismAndScaleTests"),
        new(18, CoverageKind.CoveredBy, "EngineProviderIntegrationTests"),
        new(19, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(20, CoverageKind.CoveredBy, "EngineProviderIntegrationTests"),
        new(21, CoverageKind.OutOfScope, "closed as not-a-bug/design simplification"),
        new(22, CoverageKind.CoveredBy, "CodebaseModelPopulationTests"),
        new(23, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(24, CoverageKind.CoveredBy, "CodebaseModelPopulationTests"),
        new(25, CoverageKind.CoveredBy, "EngineProviderIntegrationTests"),
        new(26, CoverageKind.OutOfScope, "agent hook/tooling failure mode not runnable on win-x64 CI"),
        new(27, CoverageKind.OutOfScope, "cop init agent-hook setup is installation workflow coverage"),
        new(28, CoverageKind.OutOfScope, "win-arm64 packaging/provider load issue not runnable on win-x64 CI"),
        new(29, CoverageKind.OutOfScope, "feature request for cop init hook integration"),
        new(30, CoverageKind.CoveredBy, "CodebaseModelPopulationTests"),
        new(31, CoverageKind.CoveredBy, "CodebaseModelPopulationTests"),
        new(32, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(33, CoverageKind.CoveredBy, "EngineProviderIntegrationTests"),
        new(34, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(35, CoverageKind.CoveredBy, "EngineProviderIntegrationTests"),
        new(36, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(37, CoverageKind.ImplementedHere, "IssueRegressionTests.Issue037_SuccessfulForeachOutputExitsZero"),
        new(38, CoverageKind.ImplementedHere, "IssueRegressionTests.Issue038_BareTopLevelExpressionProducesOutput"),
        new(39, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(40, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(41, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(42, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(43, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(44, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(45, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(46, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
        new(47, CoverageKind.CoveredBy, "EngineProviderIntegrationTests"),
        new(48, CoverageKind.CoveredBy, "CodebaseModelPopulationTests"),
        new(49, CoverageKind.CoveredBy, "LanguageFeatureExecutionTests"),
    ];

    [Test]
    public void Issues001Through049_AllHaveRegressionCoverageStatus()
    {
        var duplicates = Coverage.GroupBy(c => c.Issue).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
        Assert.That(duplicates, Is.Empty, "Each issue must appear exactly once in the regression coverage index.");

        var coveredIssues = Coverage.Select(c => c.Issue).OrderBy(i => i).ToArray();
        Assert.That(coveredIssues, Is.EqualTo(Enumerable.Range(1, 49).ToArray()),
            "Regression coverage index must cover every filed issue from #1 through #49 with no gaps.");

        var untracked = Coverage.Where(c => string.IsNullOrWhiteSpace(c.Detail)).Select(c => c.Issue).ToArray();
        Assert.That(untracked, Is.Empty, "Every issue must be ImplementedHere, CoveredBy(suiteName), or OutOfScope(reason).");

        var invalidOutOfScope = Coverage
            .Where(c => c.Kind == CoverageKind.OutOfScope)
            .Select(c => c.Issue)
            .Except([2, 7, 11, 21, 26, 27, 28, 29])
            .ToArray();
        Assert.That(invalidOutOfScope, Is.Empty, "Only explicitly non-runnable install/platform/init issues may be out of scope.");
    }
}
