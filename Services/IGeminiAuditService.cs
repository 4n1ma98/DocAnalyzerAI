using DocAnalyzerAI.Models;

namespace DocAnalyzerAI.Services;

public interface IGeminiAuditService
{
    Task<AnalysisResult> AuditDocumentAsync(string documentText, CancellationToken ct = default);
}
