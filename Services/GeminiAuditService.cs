using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DocAnalyzerAI.Models;

namespace DocAnalyzerAI.Services;

public class GeminiAuditService : IGeminiAuditService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiAuditService> _logger;

    public const string HttpClientName = "GeminiClient";

    public GeminiAuditService(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<GeminiAuditService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<AnalysisResult> AuditDocumentAsync(string documentText, CancellationToken ct = default)
    {
        var apiKey = _configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("TU_API_KEY"))
        {
            throw new InvalidOperationException("La API Key de Google Gemini no está configurada. Por favor ejecute en terminal: dotnet user-secrets set \"Gemini:ApiKey\" \"TU_KEY\" o agréguela en appsettings.json.");
        }

        var configuredModel = _configuration["Gemini:Model"] ?? "gemini-3.5-flash-lite";
        var candidateModels = new[] 
        { 
            configuredModel, 
            "gemini-3.5-flash-lite",
            "gemini-flash-lite-latest",
            "gemini-3.5-flash", 
            "gemini-flash-latest", 
            "gemini-3.8-flash",
            "gemini-pro-latest"
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
        var client = _httpClientFactory.CreateClient(HttpClientName);

        var systemPrompt = BuildSystemPrompt();
        var requestPayload = new
        {
            system_instruction = new
            {
                parts = new[]
                {
                    new { text = systemPrompt }
                }
            },
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new { text = $"Por favor audita y analiza críticamente el siguiente documento legal/comercial. Recuerda responder obligatoriamente 100% en idioma español:\n\n{documentText}" }
                    }
                }
            },
            generationConfig = new
            {
                response_mime_type = "application/json",
                temperature = 0.2
            }
        };

        HttpResponseMessage? response = null;
        string lastError = string.Empty;

        foreach (var model in candidateModels)
        {
            var requestUri = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
            const int maxRetries = 2;
            var retryDelay = TimeSpan.FromSeconds(2);

            for (int attempt = 1; attempt <= maxRetries; attempt++)
            {
                using var modelCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                modelCts.CancelAfter(TimeSpan.FromSeconds(28)); // Máximo 28s por intento individual para no bloquear la app

                try
                {
                    var jsonContent = new StringContent(
                        JsonSerializer.Serialize(requestPayload),
                        Encoding.UTF8,
                        "application/json"
                    );

                    response = await client.PostAsync(requestUri, jsonContent, modelCts.Token);

                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    if ((response.StatusCode == HttpStatusCode.TooManyRequests || 
                         (int)response.StatusCode == 503) && attempt < maxRetries)
                    {
                        _logger.LogWarning("Gemini API ({Model}) respondió con {StatusCode}. Reintento {Attempt}/{MaxRetries} en {Delay}s...",
                            model, response.StatusCode, attempt, maxRetries, retryDelay.TotalSeconds);
                        await Task.Delay(retryDelay, ct);
                        retryDelay *= 2;
                        continue;
                    }

                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    lastError = errorBody;
                    _logger.LogWarning("Gemini API ({Model}) respondió con {StatusCode}: {ErrorBody}", model, response.StatusCode, errorBody);

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new InvalidOperationException($"Error de autenticación con Google Gemini ({response.StatusCode}). Verifique que su API Key sea válida y cuente con permisos activos en Google AI Studio.");
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound || 
                        (int)response.StatusCode == 503 || 
                        response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        break; // intentar de inmediato con siguiente modelo candidato disponible
                    }

                    throw new InvalidOperationException($"Error de Gemini API ({response.StatusCode}): {errorBody}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = $"El modelo '{model}' no respondió dentro del tiempo límite de 28 segundos (posible saturación o cola de espera de Google).";
                    _logger.LogWarning("Tiempo de espera agotado (28s) para {Model}. Probando automáticamente el siguiente modelo de respaldo...", model);
                    break; // Salta de inmediato al siguiente modelo
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "Error de red al invocar Gemini API con {Model}. Reintento {Attempt}/{MaxRetries}...", model, attempt, maxRetries);
                    await Task.Delay(retryDelay, ct);
                    retryDelay *= 2;
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex.Message;
                    _logger.LogWarning(ex, "Error persistente en {Model}. Probando siguiente modelo...", model);
                    break;
                }
            }

            if (response != null && response.IsSuccessStatusCode)
            {
                break;
            }
        }

        if (response == null || !response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"No fue posible completar la auditoría tras intentar con modelos disponibles de Gemini. Último error: {lastError}");
        }

        var responseString = await response.Content.ReadAsStringAsync(ct);
        return ParseGeminiResponse(responseString);
    }

    private static string BuildSystemPrompt()
    {
        return """
            Eres un Abogado Corporativo Senior, Auditor de Contratos de élite y Especialista en Derecho Laboral y Comercial Colombiano, con profundo dominio de la normativa vigente actualizada al año 2026 (Constitución Política de Colombia, Código Sustantivo del Trabajo - CST, sentencias de unificación de la Corte Suprema de Justicia - Sala Laboral y Corte Constitucional, y leyes laborales complementarias).

            Tu misión es realizar una auditoría rigurosa, exhaustiva y crítica del documento proporcionado (contratos laborales, contratos de prestación de servicios, acuerdos de confidencialidad, convenios de cesión o términos contractuales). Debes velar prioritariamente por la parte contratada/débil de la relación jurídica, identificando cláusulas abusivas, asimetrías leoninas, renuncias ilegales a derechos y contingencias legales.

            REGLA DE IDIOMA ESTRICTA:
            Toda tu respuesta (resumen, títulos, descripciones, impactos, citas de riesgo, redacciones sugeridas y justificaciones) DEBE ESTAR OBLIGATORIAMENTE EN ESPAÑOL. No uses inglés en ninguna sección del análisis.

            MARCO LEGAL COLOMBIANO OBLIGATORIO APLICABLE A CONTRATOS (VIGENCIA 2026):
            1. JORNADA MÁXIMA LEGAL (Ley 2101 de 2021 actualizada a 2026): La jornada ordinaria máxima en Colombia es de 42 horas semanales (tras la reducción gradual que alcanzó las 42 horas). Todo pacto que imponga jornadas superiores sin liquidar horas extras diurnas, nocturnas, dominicales o festivos viola la ley.
            2. PERSONAL DE DIRECCIÓN, CONFIANZA Y MANEJO (Arts. 32 y 162 CST): Su calificación requiere facultades reales de representación patronal y mando directivo. Es ineficaz e ilegal rotular a profesionales técnicos (como desarrolladores de software, ingenieros, analistas) como "de confianza" con el único fin de eludir la jornada máxima y el pago de trabajo suplementario.
            3. DISPONIBILIDAD, GUARDIAS Y DESCONEXIÓN LABORAL (Ley 2191 de 2022 y Sentencia CSJ SL5584-2017): La disponibilidad pasiva con tiempos de respuesta estrictos (on-call/SLA) que restrinja la libertad de desplazamiento del trabajador constituye tiempo de trabajo o disponibilidad que debe ser remunerada. El derecho a la desconexión laboral es de orden público e irrenunciable.
            4. DESALARIZACIÓN Y LÍMITE A BENEFICIOS (Art. 128 CST y Ley 1393 de 2010 Art. 30): Los pagos no salariales no pueden exceder el 40% del total devengado para el cálculo de aportes a seguridad social (IBC). Además, por el principio de primacía de la realidad (Art. 53 CP), cualquier bono habitual o condicionado al rendimiento técnico u ordinario es salario en estricto sentido.
            5. PROHIBICIÓN DE RETENCIONES Y DESCUENTOS ILEGALES (Arts. 28, 149 y 150 CST): El trabajador no asume riesgos ni pérdidas operacionales de la empresa (bugs, fallas de código, consumos de nube como AWS/Azure). Cualquier retención o deducción requiere mandamiento judicial o autorización previa, expresa, libre y por escrito para cada caso posterior a los hechos.
            6. PROPIEDAD INTELECTUAL Y PROYECTOS PERSONALES (Ley 23 de 1982, Decisión Andina 351 y Ley 1450 de 2011 Art. 28): La cesión automática de derechos patrimoniales solo aplica a obras o software creados en cumplimiento estricto del contrato y funciones pactadas. Toda cláusula que pretenda apropiarse de creaciones personales hechas en fines de semana, tiempo libre o sin herramientas de la empresa es nula y abusiva.
            7. CLÁUSULAS DE NO COMPETENCIA POST-CONTRACTUAL (Art. 44 CST y Art. 25 CP): No pueden exceder un (1) año tras la terminación y exigen obligatoriamente una contraprestación o compensación económica periódica a favor del extrabajador; de lo contrario, son nulas por violar el derecho fundamental al trabajo.
            8. DEBIDO PROCESO Y DESCARGOS (Art. 115 CST y Sentencia C-593/14): Es nula la cláusula que prevea terminación unilateral con justa causa automática por métricas (e.g. story points, bugs, PRs) sin garantizar previamente el derecho de contradicción y la diligencia formal de descargos.
            9. DERECHOS MÍNIMOS E IRRENUNCIABLES (Art. 13 y 14 CST): Las disposiciones legales son de orden público y cualquier pacto en contrario que desmejore los mínimos legales carece de todo efecto jurídico.

            DEBES responder OBLIGATORIAMENTE en formato JSON con la siguiente estructura exacta y en idioma español:

            {
              "documentSummary": "Resumen ejecutivo claro y formal del documento en 2 a 3 párrafos explicando su objeto, partes intervinientes, alcance y régimen jurídico aplicable (mencionando si se ajusta o vulnera la normativa laboral colombiana a 2026).",
              "riskScore": 0 a 100, // Número entero. 0-29: Bajo riesgo / equilibrado. 30-69: Riesgo moderado / requiere ajustes. 70-100: Riesgo crítico / altamente desfavorable, leonino o con violaciones al régimen laboral colombiano.
              "strengths": [
                {
                  "title": "Título sintético del punto fuerte",
                  "description": "Explicación detallada de por qué este punto o cláusula beneficia y protege a la parte contratante",
                  "originalClause": "Cita textual o extracto exacto del documento"
                }
              ],
              "weaknesses": [
                {
                  "title": "Título sintético de la debilidad o ambigüedad",
                  "potentialImpact": "Impacto negativo o contingencia operativa/financiera que podría desencadenar",
                  "originalClause": "Cita textual o extracto exacto del documento"
                }
              ],
              "redFlags": [
                {
                  "clause": "Cita textual o referencia específica de la cláusula de peligro",
                  "riskLevel": "Bajo | Medio | Crítico",
                  "dangerExplanation": "Explicación contundente con cita expresa del artículo legal o principio vulnerado (ej. Art. 162 CST, Ley 2101/2021 de jornada 42h, Art. 149 CST deducciones ilegales, Art. 128 CST desalarización, etc.)"
                }
              ],
              "negotiationSuggestions": [
                {
                  "currentClause": "Cita o resumen exacto de la cláusula actual desfavorable o ilegal",
                  "suggestedAlternative": "Redacción alternativa ajustada a derecho colombiano lista para proponer en la negociación",
                  "justification": "Argumento legal sólido basado en la normativa colombiana vigente a 2026 para sustentar el cambio"
                }
              ]
            }

            Reglas obligatorias:
            1. Toda la respuesta DEBE estar 100% en idioma español sin excepción.
            2. No inventes cláusulas. Todo debe fundamentarse en el contenido real del documento auditado.
            3. Cita de manera precisa las leyes colombianas pertinentes en las justificaciones y alertas.
            4. Devuelve ÚNICAMENTE el objeto JSON válido, sin delimitadores adicionales como ```json ... ``` ni texto introductorio o de cierre.
            """;
    }

    private static AnalysisResult ParseGeminiResponse(string rawApiResponse)
    {
        using var jsonDoc = JsonDocument.Parse(rawApiResponse);
        var root = jsonDoc.RootElement;

        if (!root.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("Gemini no generó ninguna respuesta para el documento analizado.");
        }

        var firstCandidate = candidates[0];
        if (!firstCandidate.TryGetProperty("content", out var content) ||
            !content.TryGetProperty("parts", out var parts) ||
            parts.GetArrayLength() == 0)
        {
            throw new InvalidOperationException("La respuesta de Gemini no contiene partes de contenido válidas.");
        }

        var rawText = parts[0].GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            throw new InvalidOperationException("El contenido generado por Gemini está vacío.");
        }

        var cleanedJson = CleanJsonText(rawText);

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var result = JsonSerializer.Deserialize<AnalysisResult>(cleanedJson, options);
        if (result == null)
        {
            throw new InvalidOperationException("No fue posible deserializar la auditoría de Gemini en el modelo AnalysisResult.");
        }

        // Asegurar que las listas no sean nulas
        result.Strengths ??= [];
        result.Weaknesses ??= [];
        result.RedFlags ??= [];
        result.NegotiationSuggestions ??= [];

        // Clamping de RiskScore entre 0 y 100
        result.RiskScore = Math.Clamp(result.RiskScore, 0, 100);

        return result;
    }

    private static string CleanJsonText(string text)
    {
        var trimmed = text.Trim();

        // Si viene rodeado de bloques de código markdown ```json ... ```
        if (trimmed.StartsWith("```"))
        {
            var firstNewline = trimmed.IndexOf('\n');
            if (firstNewline != -1)
            {
                trimmed = trimmed[(firstNewline + 1)..];
            }

            if (trimmed.EndsWith("```"))
            {
                trimmed = trimmed[..^3];
            }
        }

        return trimmed.Trim();
    }
}
