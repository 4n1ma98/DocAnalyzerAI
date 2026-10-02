namespace DocAnalyzerAI.Models;

public class PointItem
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string OriginalClause { get; set; } = string.Empty;
    public string? PotentialImpact { get; set; }
}

public class RiskFlag
{
    public string Clause { get; set; } = string.Empty;
    public string RiskLevel { get; set; } = "Medium"; // "Low", "Medium", "Critical"
    public string DangerExplanation { get; set; } = string.Empty;
}

public class CounterProposal
{
    public string CurrentClause { get; set; } = string.Empty;
    public string SuggestedAlternative { get; set; } = string.Empty;
    public string Justification { get; set; } = string.Empty;
}

public class AnalysisResult
{
    public string DetectedContractType { get; set; } = string.Empty;
    public string ApplicableJurisdiction { get; set; } = string.Empty;
    public string DocumentSummary { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public List<PointItem> Strengths { get; set; } = [];
    public List<PointItem> Weaknesses { get; set; } = [];
    public List<RiskFlag> RedFlags { get; set; } = [];
    public List<CounterProposal> NegotiationSuggestions { get; set; } = [];
    public List<string> AnalyzedAnnexes { get; set; } = [];
    public string? AnnexImpactSummary { get; set; }
}
