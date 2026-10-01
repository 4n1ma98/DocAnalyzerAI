using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.AspNetCore.Components.Forms;
using UglyToad.PdfPig;

namespace DocAnalyzerAI.Services;

public partial class DocumentReaderService : IDocumentReaderService
{
    public const long MaxFileSizeInBytes = 15 * 1024 * 1024; // 15 MB
    private static readonly string[] AllowedExtensions = [".pdf", ".docx"];

    public async Task<string> ExtractTextAsync(IBrowserFile file, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(file);

        var extension = Path.GetExtension(file.Name).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
        {
            throw new InvalidOperationException($"Formato de archivo no soportado ({extension}). Solo se admiten archivos .pdf y .docx.");
        }

        if (file.Size > MaxFileSizeInBytes)
        {
            throw new InvalidOperationException($"El archivo excede el tamaño máximo permitido de {MaxFileSizeInBytes / (1024 * 1024)} MB.");
        }

        using var memoryStream = new MemoryStream();
        await using (var fileStream = file.OpenReadStream(MaxFileSizeInBytes, ct))
        {
            await fileStream.CopyToAsync(memoryStream, ct);
        }

        memoryStream.Position = 0;

        string extractedText = extension switch
        {
            ".pdf" => ExtractFromPdf(memoryStream),
            ".docx" => ExtractFromDocx(memoryStream),
            _ => throw new NotSupportedException($"Extensión no admitida: {extension}")
        };

        var sanitized = SanitizeText(extractedText);

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new InvalidOperationException("No se pudo extraer texto del documento. Verifique que no sea un archivo escaneado solo con imágenes o un documento vacío.");
        }

        return sanitized;
    }

    private static string ExtractFromPdf(Stream stream)
    {
        var sb = new StringBuilder();
        using var pdfDocument = PdfDocument.Open(stream);

        foreach (var page in pdfDocument.GetPages())
        {
            var text = page.Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                sb.AppendLine(text);
            }
        }

        return sb.ToString();
    }

    private static string ExtractFromDocx(Stream stream)
    {
        var sb = new StringBuilder();
        using var wordDoc = WordprocessingDocument.Open(stream, false);

        var body = wordDoc.MainDocumentPart?.Document?.Body;
        if (body is not null)
        {
            foreach (var paragraph in body.Descendants<Paragraph>())
            {
                var text = paragraph.InnerText;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.AppendLine(text);
                }
            }
        }

        return sb.ToString();
    }

    private static string SanitizeText(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        // Remover caracteres de control y nulos excepto saltos de línea y tabulaciones
        var clean = CleanControlCharsRegex().Replace(rawText, " ");

        // Normalizar saltos de línea múltiples consecutivos
        clean = MultipleNewlinesRegex().Replace(clean, "\n\n");

        return clean.Trim();
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex CleanControlCharsRegex();

    [GeneratedRegex(@"(\r?\n\s*){3,}")]
    private static partial Regex MultipleNewlinesRegex();
}
