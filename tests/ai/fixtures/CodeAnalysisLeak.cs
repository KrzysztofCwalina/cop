namespace AiFixture;

public class CodeReviewViolation
{
    public int Severity;
    public string Message = "";
}

public class StyleLintAnalyzer
{
    public string RuleId = "";
    public int Severity;
}

public class CodeAnalysisRule
{
    public string Description = "";
}
