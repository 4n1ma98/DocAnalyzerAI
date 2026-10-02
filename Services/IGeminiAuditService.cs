using DocAnalyzerAI.Models;

namespace DocAnalyzerAI.Services;

public interface IGeminiAuditService
{
    Task<AnalysisResult> AuditDocumentAsync(string documentText, AuditOptions? options = null, CancellationToken ct = default);
}
