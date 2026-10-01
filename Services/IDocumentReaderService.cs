using Microsoft.AspNetCore.Components.Forms;

namespace DocAnalyzerAI.Services;

public interface IDocumentReaderService
{
    Task<string> ExtractTextAsync(IBrowserFile file, CancellationToken ct = default);
}
