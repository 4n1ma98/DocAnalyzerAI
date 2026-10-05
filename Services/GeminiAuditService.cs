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

    public async Task<AnalysisResult> AuditDocumentAsync(string documentText, AuditOptions? options = null, CancellationToken ct = default)
    {
        var apiKey = _configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("TU_API_KEY"))
        {
            throw new InvalidOperationException("La API Key de Google Gemini no está configurada. Por favor ejecute en terminal: dotnet user-secrets set \"Gemini:ApiKey\" \"TU_KEY\" o agréguela en appsettings.json.");
        }

        options ??= new AuditOptions();

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

        var documentContentBuilder = new StringBuilder();
        documentContentBuilder.AppendLine("=== DOCUMENTO PRINCIPAL (CONTRATO) ===");
        documentContentBuilder.AppendLine(documentText);

        if (options.Annexes.Count > 0)
        {
            documentContentBuilder.AppendLine();
            for (int i = 0; i < options.Annexes.Count; i++)
            {
                var annex = options.Annexes[i];
                documentContentBuilder.AppendLine($"=== ANEXO / OTRO SÍ N° {i + 1}: {annex.FileName} ===");
                documentContentBuilder.AppendLine(annex.Text);
                documentContentBuilder.AppendLine();
            }
        }

        var fullDocumentPayload = documentContentBuilder.ToString();
        var annexNote = options.Annexes.Count > 0 
            ? $"IMPORTANTE: Se han adjuntado {options.Annexes.Count} Anexo(s) / 'Otro Sí'. Evalúa de manera prioritaria su impacto jurídico modificatorio respecto al contrato principal. " 
            : string.Empty;

        var systemPrompt = BuildSystemPrompt(options);
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
                        new { text = $"Por favor audita y analiza críticamente el siguiente documento bajo la jurisdicción de {options.GetSelectedCountry().Name} y la perspectiva indicada. {annexNote}Recuerda responder obligatoriamente 100% en idioma español:\n\n{fullDocumentPayload}" }
                    }
                }
            },
            generationConfig = new
            {
                response_mime_type = "application/json",
                response_schema = BuildResponseSchema(),
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
                modelCts.CancelAfter(TimeSpan.FromSeconds(28));

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
                        break;
                    }

                    throw new InvalidOperationException($"Error de Gemini API ({response.StatusCode}): {errorBody}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = $"El modelo '{model}' no respondió dentro del tiempo límite de 28 segundos (posible saturación o cola de espera de Google).";
                    _logger.LogWarning("Tiempo de espera agotado (28s) para {Model}. Probando automáticamente el siguiente modelo de respaldo...", model);
                    break;
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
        return ParseGeminiResponse(responseString, options);
    }

    private static string BuildSystemPrompt(AuditOptions options)
    {
        var country = options.GetSelectedCountry();
        var perspectiveTitle = options.Perspective == AuditPerspective.Worker 
            ? "DEFENSA DEL TRABAJADOR / CONTRATISTA (PARTE DÉBIL O PRESTADORA)" 
            : "BLINDAJE DE LA EMPRESA / CONTRATANTE (PARTE EMPLEADORA O CONTRATANTE)";

        var perspectiveMission = options.Perspective == AuditPerspective.Worker
            ? "Debes velar prioritariamente por la parte contratada/prestadora de la relación jurídica, identificando cláusulas abusivas, sobrecargas de jornada desmedidas, asimetrías leoninas, deducciones ilegales de remuneración, cesiones excesivas de propiedad intelectual y renuncias indebidas a derechos de orden público, redactando contraofertas que equilibren el acuerdo."
            : "Debes velar prioritariamente por la seguridad jurídica y protección corporativa de la empresa contratante, identificando contingencias legales, riesgos de demandas por 'contrato realidad' o subordinación encubierta, vacíos en protección de secretos comerciales y cesión de propiedad intelectual, ambigüedades en penalidades o entregables, redactando cláusulas sólidas y ejecutables judicialmente sin violar normas de orden público.";

        var legalFrameworkSpecifics = GetLegalFrameworkText(options.CountryCode);
        var annexAuditInstructions = options.Annexes.Count > 0
            ? """

            AUDITORÍA INTEGRAL DE CONTRATO Y OTRO SÍ / ANEXOS MODIFICATORIOS:
            El usuario ha proporcionado el Contrato Principal junto con uno o más Anexos o 'Otro Sí' modificatorios.
            Debes realizar una auditoría armónica e integrada de todo el acuerdo contractual:
            1. Analiza de manera prioritaria cómo cada 'Otro Sí' o Anexo modifica, deroga, adiciona o altera las cláusulas y condiciones del contrato original (por ejemplo: cambios de remuneración, pactos de exclusividad sobrevinientes, extensión de jornada, teletrabajo, penalidades o prórrogas).
            2. En el campo "annexImpactSummary", redacta una síntesis ejecutiva clara del impacto legal neto de los anexos (detalla si las adendas empeoran, equilibran o benefician la situación jurídica de la parte evaluada, qué cláusulas originales fueron sustituidas y si se vulneran normas de orden público).
            3. Incluye los nombres exactos de los anexos evaluados en el array "analyzedAnnexes".
            4. Refleja en el RiskScore y en todas las pestañas (Puntos Fuertes, Débiles, Red Flags y Sugerencias de Negociación) las nuevas contingencias y cláusulas derivadas de los 'Otro Sí'.
            """
            : string.Empty;

        return $$"""
            Eres un Abogado Corporativo Senior, Auditor de Contratos de élite y Especialista en Derecho Laboral y Comercial Internacional, con profundo dominio de la normativa vigente en {{country.Name}} actualizada al año 2026.

            JURISDICCIÓN APLICABLE: {{country.Name}} (Marco normativo: {{country.LegalFramework}}).
            PERSPECTIVA DE AUDITORÍA ASUMIDA: {{perspectiveTitle}}.
            {{perspectiveMission}}
            {{annexAuditInstructions}}

            DETECCIÓN AUTOMÁTICA OBLIGATORIA DEL TIPO DE CONTRATO:
            Debes examinar rigurosamente el texto del documento para identificar y clasificar con precisión técnica su tipología jurídica (por ejemplo: 'Contrato de Trabajo a Término Indefinido', 'Contrato de Trabajo a Término Fijo', 'Contrato de Prestación de Servicios Profesionales por Honorarios', 'Acuerdo de Confidencialidad y No Divulgación (NDA)', 'Contrato de Obra o Labor', 'Contrato de Desarrollo de Software B2B', etc.). Registra el resultado en la propiedad "detectedContractType".

            REGLA DE IDIOMA ESTRICTA:
            Toda tu respuesta (resumen, títulos, descripciones, impactos, citas de riesgo, redacciones sugeridas y justificaciones) DEBE ESTAR OBLIGATORIAMENTE EN ESPAÑOL. No uses inglés en ninguna sección del análisis.

            MARCO LEGAL APLICABLE (VIGENCIA 2026):
            {{legalFrameworkSpecifics}}

            DEBES responder OBLIGATORIAMENTE en formato JSON con la siguiente estructura exacta y en idioma español:

            {
              "detectedContractType": "Nombre formal del tipo de contrato detectado automáticamente",
              "applicableJurisdiction": "{{country.Name}} - Normativa Laboral / Comercial 2026",
              "analyzedAnnexes": ["Nombre_del_anexo_1.pdf"], // Lista de nombres de anexos u otro sí evaluados (vacío si no hay)
              "annexImpactSummary": "Síntesis del impacto legal de los anexos/otro sí sobre el contrato original (o null si no hay anexos)",
              "documentSummary": "Resumen ejecutivo claro y formal del documento en 2 a 3 párrafos explicando su objeto, partes intervinientes, alcance y régimen jurídico aplicable (mencionando si se ajusta o vulnera la normativa aplicable a 2026).",
              "riskScore": 0 a 100, // Número entero. Refleja la severidad global calculada a partir de los 4 sub-puntajes.
              "subScores": {
                "economicScore": 0 a 100, // Riesgo Económico y Remuneración (30% ponderación). Deducciones, desalarización, costos asumidos.
                "economicRationale": "Explicación legal sintética de por qué se asignó este puntaje económico.",
                "workingHoursScore": 0 a 100, // Riesgo de Jornada y Disponibilidad (25% ponderación). Horas extras, límite 2026, guardias on-call, desconexión.
                "workingHoursRationale": "Explicación legal sintética de la contingencia en jornada y descanso.",
                "intellectualPropertyScore": 0 a 100, // Riesgo de PI y No Competencia (25% ponderación). Cesión de inventos fuera de horario, pactos leoninos sin pago.
                "intellectualPropertyRationale": "Explicación legal sintética de la desproporción en PI o pacto de no competencia.",
                "stabilityTerminationScore": 0 a 100, // Riesgo de Estabilidad y Terminación (20% ponderación). Causales, descargos, preavisos, contrato realidad.
                "stabilityTerminationRationale": "Explicación legal sintética sobre causales de despido o contingencias de subordinación."
              },
              "strengths": [
                {
                  "title": "Título sintético del punto fuerte",
                  "description": "Explicación detallada de por qué este punto o cláusula beneficia y protege a la parte evaluada",
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
                  "dangerExplanation": "Explicación contundente con cita expresa de la ley o principio vulnerado en la jurisdicción aplicable"
                }
              ],
              "negotiationSuggestions": [
                {
                  "currentClause": "Cita o resumen exacto de la cláusula actual desfavorable o desbalanceada",
                  "suggestedAlternative": "Redacción alternativa ajustada a derecho lista para proponer en la negociación",
                  "justification": "Argumento legal sólido basado en la normativa vigente a 2026 para sustentar el cambio"
                }
              ]
            }

            Reglas obligatorias:
            1. Toda la respuesta DEBE estar 100% en idioma español sin excepción.
            2. No inventes cláusulas. Todo debe fundamentarse en el contenido real del documento auditado.
            3. Cita de manera precisa las leyes y artículos pertinentes en las justificaciones y alertas.
            4. Devuelve ÚNICAMENTE el objeto JSON válido, sin delimitadores adicionales como ```json ... ``` ni texto introductorio o de cierre.
            """;
    }

    private static string GetLegalFrameworkText(string countryCode) => countryCode.ToUpperInvariant() switch
    {
        "CO" => """
            1. JORNADA MÁXIMA LEGAL (Ley 2101 de 2021 actualizada a 2026): La jornada ordinaria máxima es de 42 horas semanales. Todo pacto superior sin recargos extras, nocturnos o dominicales viola la ley.
            2. PERSONAL DE DIRECCIÓN, CONFIANZA Y MANEJO (Arts. 32 y 162 CST): Requiere mando directivo real. Es nulo rotular a roles técnicos (como desarrolladores, ingenieros, analistas) para evadir jornada y extras.
            3. DISPONIBILIDAD Y DESCONEXIÓN LABORAL (Ley 2191 de 2022 y Sentencia CSJ SL5584-2017): Las guardias on-call restrictivas constituyen tiempo de trabajo o disponibilidad remunerada. Desconexión es irrenunciable.
            4. DESALARIZACIÓN Y LÍMITE A BENEFICIOS (Art. 128 CST y Ley 1393/2010 Art. 30): Los pagos no salariales no pueden exceder el 40% del IBC. Bonos habituales por desempeño son salario (Art. 53 CP).
            5. PROHIBICIÓN DE RETENCIONES Y DESCUENTOS ILEGALES (Arts. 28, 149 y 150 CST): El trabajador no asume riesgos o pérdidas de la empresa (bugs, nube AWS/Azure). Descuentos exigen autorización libre previa.
            6. PROPIEDAD INTELECTUAL (Ley 23 de 1982 y Ley 1450 de 2011 Art. 28): La cesión automática solo aplica a creaciones hechas en estricto cumplimiento del contrato y horario laboral.
            7. NO COMPETENCIA POST-CONTRACTUAL (Art. 44 CST y Art. 25 CP): No puede exceder 1 año y exige obligatoriamente compensación económica periódica; sin pago es nula.
            8. DEBIDO PROCESO Y DESCARGOS (Art. 115 CST y C-593/14): Terminación por métricas requiere garantizar descargos previos.
            """,
        "MX" => """
            1. JORNADA MÁXIMA Y HORAS EXTRAORDINARIAS (Art. 58-68 LFT): Diurna 48h semanales, nocturna 42h, mixta 45h. Horas extras se pagan con 100% y 200% de recargo según límites legales.
            2. TELETRABAJO (NOM-037-STPS-2023 y Art. 330-E LFT): El patrón debe asumir costos de luz e internet y proporcionar equipo ergonómico. Derecho a la desconexión digital irrenunciable.
            3. IRRENUNCIABILIDAD DE DERECHOS (Art. 33 y 123 Constitucional): Cualquier cláusula que implique renuncia a liquidación, vacaciones dignas (mínimo 12 días primer año), aguinaldo o PTU carece de validez.
            4. PROPIEDAD INTELECTUAL Y PATENTES (Art. 163 LFT y LFPPI): Las invenciones de servicio pertenecen al patrón, pero el trabajador tiene derecho a compensación complementaria si la aportación supera el salario.
            5. CLÁUSULAS DE NO COMPETENCIA POST-EMPLEO: Severamente restringidas por el Art. 5 Constitucional (libertad de trabajo), requiriendo delimitación geográfica, temporal y remuneración para subsistir.
            """,
        "ES" => """
            1. JORNADA MÁXIMA Y REGISTRO HORARIO (Art. 34-35 ET): Jornada ordinaria máxima legal de 40 horas semanales promedio. Registro diario de jornada obligatorio; no registrar horas extra genera presunción favorable al empleado.
            2. DESCONEXIÓN DIGITAL (Art. 88 Ley Orgánica 3/2018 LOPDGDD y Art. 20 bis ET): Derecho incondicional a no atender comunicaciones fuera de jornada.
            3. PACTO DE NO COMPETENCIA POSTCONTRACTUAL (Art. 21.2 ET): Requiere obligatoriamente dos requisitos concurrentes: efectivo interés comercial y compensación económica adecuada. Sin compensación es radicalmente nulo.
            4. PROPIEDAD INTELECTUAL (Real Decreto Legislativo 1/1996 Ley de Propiedad Intelectual Arts. 8 y 51): Cesión de derechos limitada a lo necesario para la actividad habitual de la empresa.
            5. CONDICIONES MÍNIMAS INDISPONIBLES (Art. 3.5 ET): Los trabajadores no pueden disponer válidamente de los derechos reconocidos por normas legales o convenios colectivos.
            """,
        "US" => """
            1. FAIR LABOR STANDARDS ACT (FLSA): Exempt vs. Non-Exempt classification requires meeting both the salary threshold and the duties test. Misclassifying technical roles to avoid overtime violates federal law.
            2. NON-COMPETE CLAUSES: Scrutinized under FTC guidelines and state laws (e.g., California Business and Professions Code §16600 voids non-competes). Must be reasonable in geography and duration.
            3. INTELLECTUAL PROPERTY ASSIGNMENT: Inventions Assignment Agreements cannot capture employee inventions made on their own time without employer resources, unless directly related to employer's business.
            4. INDEPENDENT CONTRACTOR CLASSIFICATION: Scrutinized under economic reality test (control, investment, opportunity for profit/loss). Misclassification triggers severe tax and wage liabilities.
            5. AT-WILL EMPLOYMENT EXCEPTIONS: Implied contracts, covenant of good faith and fair dealing, public policy exceptions (whistleblowing, retaliation).
            """,
        "CL" => """
            1. JORNADA ORDINARIA DE 40 HORAS (Ley 21.561 vigencia gradual 2026): Reducción efectiva de jornada. Exclusión del Art. 22 inciso 2 restringida estrictamente a gerentes y cargos con poder de representación.
            2. LEY KARIN (Ley 21.643): Obligación estricta de protocolos de prevención y debido proceso en acoso laboral o sexual.
            3. PROHIBICIÓN DE RENUNCIA (Art. 5 Código del Trabajo): Los derechos establecidos por las leyes laborales son irrenunciables mientras subsista el contrato de trabajo.
            4. REMUNERACIONES Y DEDUCCIONES (Art. 58 Código del Trabajo): Límites estrictos a retenciones; el empleador no puede descontar pérdidas de negocio ni daños sin autorización expresa calificada.
            """,
        "AR" => """
            1. PRINCIPIO PROTECTORIO E IRRENUNCIABILIDAD (Art. 12 Ley de Contrato de Trabajo N° 20.744): Será nulo todo pacto que suprima o reduzca derechos previstos en la ley o convenios colectivos.
            2. JORNADA MÁXIMA LEGAL (Ley 11.544): Máximo de 8 horas diarias o 48 horas semanales. Límites a horas extraordinarias y recargos del 50% y 100%.
            3. DERECHO A LA INTEGRIDAD DE LA REMUNERACIÓN (Art. 131-133 LCT): Prohibición de deducciones, retenciones o compensaciones salvo excepciones taxativas legales.
            4. FACULTADES DE DIRECCIÓN Y MODIFICACIÓN (Ius Variandi Art. 66 LCT): Las modificaciones contractuales no pueden ocasionar perjuicio moral ni patrimonial al trabajador.
            """,
        "PE" => """
            1. JORNADA MÁXIMA CONSTITUCIONAL (Art. 25 CP y D.S. 007-2002-TR): Máximo de 8 horas diarias o 48 horas semanales. El trabajo en sobretiempo es voluntario y remunerado con sobretasa legal.
            2. TELETRABAJO (Ley 31572): Derecho a la desconexión digital de al menos 12 horas continuas; empleador asume equipos y compensación de gastos en ausencia de pacto en contrario.
            3. DESNATURALIZACIÓN DE CONTRATOS CIVILES: Por principio de primacía de la realidad, la locación de servicios con horario o supervisión directa se presume relación laboral sujeta al D.Leg. 728.
            4. IRRENUNCIABILIDAD DE BENEFICIOS SOCIALES: CTS, gratificaciones, vacaciones de 30 días y asignación familiar son derechos imperativos indisponibles.
            """,
        _ => """
            1. PRINCIPIOS GENERALES DE LA ORGANIZACIÓN INTERNACIONAL DEL TRABAJO (OIT): Jornada máxima razonable, descanso semanal, remuneración digna y protección contra el despido injustificado.
            2. PRINCIPIOS UNIDROIT SOBRE CONTRATOS COMERCIALES INTERNACIONALES: Deber de buena fe y lealtad negocial (Art. 1.7), prohibición de cláusulas sorpresivas y leoninas (Art. 2.1.20).
            3. EQUILIBRIO CONTRACTUAL Y PROPIEDAD INTELECTUAL: La cesión de derechos debe ser proporcional y razonable, sin expropiar creaciones personales ajenas al objeto contractual.
            """
    };

    private static AnalysisResult ParseGeminiResponse(string rawApiResponse, AuditOptions options)
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

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        var result = JsonSerializer.Deserialize<AnalysisResult>(cleanedJson, jsonOptions);
        if (result == null)
        {
            throw new InvalidOperationException("No fue posible deserializar la auditoría de Gemini en el modelo AnalysisResult.");
        }

        result.Strengths ??= [];
        result.Weaknesses ??= [];
        result.RedFlags ??= [];
        result.NegotiationSuggestions ??= [];
        result.AnalyzedAnnexes ??= [];

        if (result.AnalyzedAnnexes.Count == 0 && options.Annexes.Count > 0)
        {
            result.AnalyzedAnnexes = options.Annexes.Select(a => a.FileName).ToList();
        }

        if (string.IsNullOrWhiteSpace(result.ApplicableJurisdiction))
        {
            result.ApplicableJurisdiction = options.GetSelectedCountry().Name;
        }

        if (string.IsNullOrWhiteSpace(result.DetectedContractType))
        {
            result.DetectedContractType = "Contrato / Acuerdo Detectado";
        }

        result.SubScores ??= new RiskSubScores();
        result.SubScores.EconomicScore = Math.Clamp(result.SubScores.EconomicScore, 0, 100);
        result.SubScores.WorkingHoursScore = Math.Clamp(result.SubScores.WorkingHoursScore, 0, 100);
        result.SubScores.IntellectualPropertyScore = Math.Clamp(result.SubScores.IntellectualPropertyScore, 0, 100);
        result.SubScores.StabilityTerminationScore = Math.Clamp(result.SubScores.StabilityTerminationScore, 0, 100);

        // Sincronizar el score global con la ponderación exacta calculada
        var weightedGlobalScore = result.SubScores.CalculateWeightedGlobalScore();
        result.RiskScore = weightedGlobalScore > 0 ? weightedGlobalScore : Math.Clamp(result.RiskScore, 0, 100);

        return result;
    }

    private static object BuildResponseSchema()
    {
        return new
        {
            type = "OBJECT",
            properties = new
            {
                detectedContractType = new { type = "STRING" },
                applicableJurisdiction = new { type = "STRING" },
                documentSummary = new { type = "STRING" },
                riskScore = new { type = "INTEGER" },
                subScores = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        economicScore = new { type = "INTEGER" },
                        economicRationale = new { type = "STRING" },
                        workingHoursScore = new { type = "INTEGER" },
                        workingHoursRationale = new { type = "STRING" },
                        intellectualPropertyScore = new { type = "INTEGER" },
                        intellectualPropertyRationale = new { type = "STRING" },
                        stabilityTerminationScore = new { type = "INTEGER" },
                        stabilityTerminationRationale = new { type = "STRING" }
                    },
                    required = new[]
                    {
                        "economicScore", "economicRationale",
                        "workingHoursScore", "workingHoursRationale",
                        "intellectualPropertyScore", "intellectualPropertyRationale",
                        "stabilityTerminationScore", "stabilityTerminationRationale"
                    }
                },
                analyzedAnnexes = new
                {
                    type = "ARRAY",
                    items = new { type = "STRING" }
                },
                annexImpactSummary = new { type = "STRING" },
                strengths = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            title = new { type = "STRING" },
                            description = new { type = "STRING" },
                            originalClause = new { type = "STRING" }
                        },
                        required = new[] { "title", "description", "originalClause" }
                    }
                },
                weaknesses = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            title = new { type = "STRING" },
                            potentialImpact = new { type = "STRING" },
                            originalClause = new { type = "STRING" }
                        },
                        required = new[] { "title", "potentialImpact", "originalClause" }
                    }
                },
                redFlags = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            clause = new { type = "STRING" },
                            riskLevel = new { type = "STRING" },
                            dangerExplanation = new { type = "STRING" }
                        },
                        required = new[] { "clause", "riskLevel", "dangerExplanation" }
                    }
                },
                negotiationSuggestions = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            currentClause = new { type = "STRING" },
                            suggestedAlternative = new { type = "STRING" },
                            justification = new { type = "STRING" }
                        },
                        required = new[] { "currentClause", "suggestedAlternative", "justification" }
                    }
                }
            },
            required = new[]
            {
                "detectedContractType",
                "applicableJurisdiction",
                "documentSummary",
                "riskScore",
                "subScores",
                "strengths",
                "weaknesses",
                "redFlags",
                "negotiationSuggestions"
            }
        };
    }

    private static string CleanJsonText(string text)
    {
        var trimmed = text.Trim();

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

    public async Task<string> AskContractQuestionAsync(
        string documentText,
        AnalysisResult? analysisResult,
        List<ContractChatMessage> conversationHistory,
        string userQuestion,
        AuditOptions? options = null,
        CancellationToken ct = default)
    {
        var apiKey = _configuration["Gemini:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Contains("TU_API_KEY"))
        {
            throw new InvalidOperationException("La API Key de Google Gemini no está configurada. Por favor ejecute en terminal: dotnet user-secrets set \"Gemini:ApiKey\" \"TU_KEY\" o agréguela en appsettings.json.");
        }

        options ??= new AuditOptions();

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

        var systemPrompt = BuildChatSystemPrompt(documentText, analysisResult, options);

        var contentsList = new List<object>();

        if (conversationHistory != null)
        {
            foreach (var msg in conversationHistory)
            {
                if (string.IsNullOrWhiteSpace(msg.Content) || msg.IsError) continue;

                var role = msg.Role == ChatSenderRole.User ? "user" : "model";
                contentsList.Add(new
                {
                    role = role,
                    parts = new[] { new { text = msg.Content } }
                });
            }
        }

        contentsList.Add(new
        {
            role = "user",
            parts = new[] { new { text = userQuestion } }
        });

        var requestPayload = new
        {
            system_instruction = new
            {
                parts = new[]
                {
                    new { text = systemPrompt }
                }
            },
            contents = contentsList,
            generationConfig = new
            {
                temperature = 0.3,
                max_output_tokens = 2048
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
                modelCts.CancelAfter(TimeSpan.FromSeconds(28));

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
                        _logger.LogWarning("Gemini API ({Model}) respondió con {StatusCode} en Chat. Reintento {Attempt}/{MaxRetries} en {Delay}s...",
                            model, response.StatusCode, attempt, maxRetries, retryDelay.TotalSeconds);
                        await Task.Delay(retryDelay, ct);
                        retryDelay *= 2;
                        continue;
                    }

                    var errorBody = await response.Content.ReadAsStringAsync(ct);
                    lastError = errorBody;
                    _logger.LogWarning("Gemini API ({Model}) respondió con {StatusCode} en Chat: {ErrorBody}", model, response.StatusCode, errorBody);

                    if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden)
                    {
                        throw new InvalidOperationException($"Error de autenticación con Google Gemini ({response.StatusCode}). Verifique que su API Key sea válida.");
                    }

                    if (response.StatusCode == HttpStatusCode.NotFound || 
                        (int)response.StatusCode == 503 || 
                        response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        break;
                    }

                    throw new InvalidOperationException($"Error de Gemini API en Chat ({response.StatusCode}): {errorBody}");
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = $"El modelo '{model}' no respondió dentro del tiempo límite de 28 segundos.";
                    _logger.LogWarning("Timeout (28s) para {Model} en Chat. Probando siguiente modelo...", model);
                    break;
                }
                catch (HttpRequestException ex) when (attempt < maxRetries)
                {
                    _logger.LogWarning(ex, "Error de red con {Model} en Chat. Reintento {Attempt}/{MaxRetries}...", model, attempt, maxRetries);
                    await Task.Delay(retryDelay, ct);
                    retryDelay *= 2;
                }
                catch (HttpRequestException ex)
                {
                    lastError = ex.Message;
                    _logger.LogWarning(ex, "Error persistente en {Model} para Chat. Probando siguiente modelo...", model);
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
            throw new InvalidOperationException($"No fue posible obtener respuesta de Gemini. Último error: {lastError}");
        }

        var responseString = await response.Content.ReadAsStringAsync(ct);
        using var jsonDoc = JsonDocument.Parse(responseString);
        if (jsonDoc.RootElement.TryGetProperty("candidates", out var candidates) && 
            candidates.GetArrayLength() > 0 &&
            candidates[0].TryGetProperty("content", out var content) &&
            content.TryGetProperty("parts", out var parts) &&
            parts.GetArrayLength() > 0 &&
            parts[0].TryGetProperty("text", out var textElement))
        {
            return textElement.GetString() ?? "No se generó contenido de respuesta.";
        }

        return "Gemini completó el proceso pero no se encontró texto en la respuesta.";
    }

    private static string BuildChatSystemPrompt(string documentText, AnalysisResult? analysisResult, AuditOptions options)
    {
        var country = options.GetSelectedCountry();
        var perspectiveTitle = options.Perspective == AuditPerspective.Worker 
            ? "DEFENSA DEL TRABAJADOR / CONTRATISTA (PARTE DÉBIL O PRESTADORA)" 
            : "BLINDAJE DE LA EMPRESA / CONTRATANTE (PARTE EMPLEADORA O CONTRATANTE)";

        var sb = new StringBuilder();
        sb.AppendLine("ERES EL ASISTENTE JURÍDICO INTELIGENTE Y COPILOTO DE AUDITORÍA CONTRACTUAL DE 'DocAnalyzer AI (Lex Audit)'.");
        sb.AppendLine("Tu misión es responder preguntas, aclarar cláusulas, señalar riesgos y brindar orientación estratégica sobre el contrato y anexos que fueron analizados.");
        sb.AppendLine();
        sb.AppendLine("=== DIRECTRICES FUNDAMENTALES ===");
        sb.AppendLine($"1. JURISDICCIÓN: {country.Name} ({country.LegalFramework}). Responde conforme a la legislación y jurisprudencia de este marco legal.");
        sb.AppendLine($"2. PERSPECTIVA ASISTIDA: {perspectiveTitle}.");
        sb.AppendLine("3. FUNDAMENTACIÓN ESTRICTA: Basa tus respuestas rigurosamente en las cláusulas del documento proporcionado. Siempre que aplique, cita la cláusula explícita (ejemplo: '[Cláusula 8.1: Terminación anticipada]').");
        sb.AppendLine("4. ESTRUCTURA Y TONO: Tono profesional, claro, accesible y ejecutivo. Usa negritas y viñetas para hacer la lectura ágil.");
        sb.AppendLine("5. IDIOMA: Responde obligatoriamente 100% en idioma español.");
        sb.AppendLine("6. ALCANCE: Si el contrato no estipula algo sobre lo que el usuario pregunta, indícalo claramente como un vacío o laguna contractual y advierte sobre el riesgo asociado.");
        sb.AppendLine();

        if (analysisResult != null)
        {
            sb.AppendLine("=== RESULTADO DEL DICTAMEN DE AUDITORÍA PREVIO ===");
            sb.AppendLine($"- Tipo de Contrato: {analysisResult.DetectedContractType}");
            sb.AppendLine($"- Nivel de Riesgo Global: {analysisResult.RiskScore}/100");
            sb.AppendLine($"- Resumen Ejecutivo: {analysisResult.DocumentSummary}");
            if (analysisResult.RedFlags.Count > 0)
            {
                sb.AppendLine($"- Red Flags Críticas: {analysisResult.RedFlags.Count} detectadas.");
            }
            sb.AppendLine();
        }

        sb.AppendLine("=== DOCUMENTO PRINCIPAL (CONTRATO) ===");
        sb.AppendLine(documentText);
        sb.AppendLine();

        if (options.Annexes.Count > 0)
        {
            sb.AppendLine("=== ANEXOS / 'OTRO SÍ' ADJUNTOS ===");
            for (int i = 0; i < options.Annexes.Count; i++)
            {
                var annex = options.Annexes[i];
                sb.AppendLine($"--- Anexo {i + 1}: {annex.FileName} ---");
                sb.AppendLine(annex.Text);
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }
}
