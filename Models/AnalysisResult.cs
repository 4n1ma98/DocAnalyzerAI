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

public class RiskSubScores
{
    // Sub-puntaje 1: Riesgo Económico y Remuneración (Ponderación 30%)
    public int EconomicScore { get; set; }
    public string EconomicRationale { get; set; } = string.Empty;

    // Sub-puntaje 2: Riesgo de Jornada, Disponibilidad y Descanso (Ponderación 25%)
    public int WorkingHoursScore { get; set; }
    public string WorkingHoursRationale { get; set; } = string.Empty;

    // Sub-puntaje 3: Riesgo de Propiedad Intelectual, Confidencialidad y No Competencia (Ponderación 25%)
    public int IntellectualPropertyScore { get; set; }
    public string IntellectualPropertyRationale { get; set; } = string.Empty;

    // Sub-puntaje 4: Riesgo de Estabilidad, Subordinación y Terminación (Ponderación 20%)
    public int StabilityTerminationScore { get; set; }
    public string StabilityTerminationRationale { get; set; } = string.Empty;

    public int CalculateWeightedGlobalScore()
    {
        double weighted = (EconomicScore * 0.30) +
                          (WorkingHoursScore * 0.25) +
                          (IntellectualPropertyScore * 0.25) +
                          (StabilityTerminationScore * 0.20);
        return Math.Clamp((int)Math.Round(weighted), 0, 100);
    }
}

public class AnalysisResult
{
    public string DetectedContractType { get; set; } = string.Empty;
    public string ApplicableJurisdiction { get; set; } = string.Empty;
    public string DocumentSummary { get; set; } = string.Empty;
    public int RiskScore { get; set; }
    public RiskSubScores SubScores { get; set; } = new();
    public List<PointItem> Strengths { get; set; } = [];
    public List<PointItem> Weaknesses { get; set; } = [];
    public List<RiskFlag> RedFlags { get; set; } = [];
    public List<CounterProposal> NegotiationSuggestions { get; set; } = [];
    public List<string> AnalyzedAnnexes { get; set; } = [];
    public string? AnnexImpactSummary { get; set; }
}
