using DocAnalyzerAI.Models;

namespace DocAnalyzerAI.Services;

public interface IGeminiAuditService
{
    Task<AnalysisResult> AuditDocumentAsync(string documentText, AuditOptions? options = null, CancellationToken ct = default);

    Task<string> AskContractQuestionAsync(
        string documentText,
        AnalysisResult? analysisResult,
        List<ContractChatMessage> conversationHistory,
        string userQuestion,
        AuditOptions? options = null,
        CancellationToken ct = default);
}
