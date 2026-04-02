using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class InsightsChatService : IInsightsChatService
{
    private readonly IBusinessInsightsService _bizInsights;
    private readonly IFounderInsightsService _founderInsights;
    private readonly HttpClient _http;
    private readonly AiParserOptions _options;
    private readonly ILogger<InsightsChatService> _logger;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private const int MaxContextChars = 2000;

    private const string SystemPrompt = """
        Eres un copiloto de decisiones de negocio.
        Tu trabajo es decir qué está pasando, por qué importa, y qué hacer ahora.
        Responde en máximo 3 oraciones con esta estructura exacta:
        1. SITUACIÓN: qué está pasando (usa los datos).
        2. IMPACTO: por qué importa para el negocio.
        3. ACCIÓN: una sola cosa concreta que hacer hoy.
        No des múltiples opciones. No expliques de más. No repitas métricas sin interpretar.
        Usa tono decisivo: "Activa", "Corrige", "Enfócate en".
        Si no hay datos suficientes, responde: "No hay suficiente información para tomar una decisión todavía."
        """;

    public InsightsChatService(
        IBusinessInsightsService bizInsights,
        IFounderInsightsService founderInsights,
        HttpClient http,
        IOptions<AiParserOptions> options,
        ILogger<InsightsChatService> logger)
    {
        _bizInsights = bizInsights;
        _founderInsights = founderInsights;
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    private const string FounderSystemPrompt = """
        Eres un copiloto de decisiones a nivel portafolio para una plataforma multi-negocio.
        Tu trabajo es decir qué está pasando en el portafolio, por qué importa, y qué priorizar.
        Responde en máximo 3 oraciones: situación, impacto, acción.
        Usa conceptos de portafolio: "A nivel portafolio...", "El negocio que más atención requiere...", "Prioriza...".
        NO uses "tu negocio" ni "tu local". Habla como asesor ejecutivo.
        Si no hay datos suficientes, responde: "No hay suficiente información del portafolio todavía."
        """;

    private const int MinContextLength = 80;

    public async Task<InsightsChatResponse> AskAsync(InsightsChatRequest request, Guid? businessId, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var scope = request.Scope ?? "business";
        var questionLen = request.Question?.Length ?? 0;

        // 1. Build compact context from existing insights services
        string context;
        bool hasState = false, hasOpp = false, hasRisk = false, hasAction = false;
        try
        {
            if (scope == "founder")
            {
                var data = await _founderInsights.GetInsightsAsync(ct);
                context = BuildFounderContext(data);
                hasState = !string.IsNullOrWhiteSpace(data.PlatformStateTitle);
                hasOpp = !string.IsNullOrWhiteSpace(data.MainOpportunity);
                hasRisk = !string.IsNullOrWhiteSpace(data.MainRisk);
                hasAction = !string.IsNullOrWhiteSpace(data.PrimaryRecommendation?.Title);
            }
            else
            {
                if (!businessId.HasValue)
                {
                    LogResult(scope, "skipped_no_biz", sw, 0, questionLen, false, false, false, false);
                    return new InsightsChatResponse { Answer = "No hay contexto de negocio disponible.", Confidence = "low" };
                }

                var data = await _bizInsights.GetInsightsAsync(businessId.Value, 30, ct);
                context = BuildBusinessContext(data);
                hasState = !string.IsNullOrWhiteSpace(data.BusinessStateTitle);
                hasOpp = !string.IsNullOrWhiteSpace(data.MainOpportunity);
                hasRisk = !string.IsNullOrWhiteSpace(data.MainRisk);
                hasAction = !string.IsNullOrWhiteSpace(data.PrimaryRecommendation?.Title);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "INSIGHTS CHAT: failed to gather context for scope={Scope}", scope);
            LogResult(scope, "fallback_context_error", sw, 0, questionLen, false, false, false, false);
            return Fallback();
        }

        // 2. Low-context short-circuit: don't waste an OpenAI call on empty data
        if (context.Length < MinContextLength)
        {
            LogResult(scope, "skipped_low_context", sw, context.Length, questionLen, hasState, hasOpp, hasRisk, hasAction);
            return new InsightsChatResponse
            {
                Answer = scope == "founder"
                    ? "No hay suficiente información del portafolio para tomar una decisión todavía."
                    : "No hay suficiente información del negocio para tomar una decisión todavía.",
                Confidence = "low"
            };
        }

        // 3. Call OpenAI
        var apiKey = _options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("INSIGHTS CHAT: OPENAI_API_KEY not configured");
            LogResult(scope, "skipped_no_key", sw, context.Length, questionLen, hasState, hasOpp, hasRisk, hasAction);
            return Fallback();
        }

        var systemPrompt = scope == "founder" ? FounderSystemPrompt : SystemPrompt;
        var contextLabel = scope == "founder" ? "Datos del portafolio" : "Datos del negocio";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            var body = new
            {
                model = _options.Model,
                temperature = 0.3,
                max_tokens = 300,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = $"{contextLabel}:\n{context}\n\nPregunta: {request.Question}" }
                }
            };

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
            httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
            httpReq.Content = JsonContent.Create(body, options: JsonOpts);

            using var res = await _http.SendAsync(httpReq, cts.Token);

            if (!res.IsSuccessStatusCode)
            {
                var errBody = await res.Content.ReadAsStringAsync(cts.Token);
                _logger.LogWarning("INSIGHTS CHAT: OpenAI returned {Status}: {Body}", (int)res.StatusCode, errBody);
                LogResult(scope, "fallback_openai_error", sw, context.Length, questionLen, hasState, hasOpp, hasRisk, hasAction);
                return Fallback();
            }

            var raw = await res.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            LogResult(scope, "success", sw, context.Length, questionLen, hasState, hasOpp, hasRisk, hasAction);

            return new InsightsChatResponse
            {
                Answer = TrimToMaxSentences(answer.Trim(), 4),
                Confidence = "medium"
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("INSIGHTS CHAT: OpenAI call timed out");
            LogResult(scope, "fallback_timeout", sw, context.Length, questionLen, hasState, hasOpp, hasRisk, hasAction);
            return Fallback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "INSIGHTS CHAT: OpenAI call failed");
            LogResult(scope, "fallback_error", sw, context.Length, questionLen, hasState, hasOpp, hasRisk, hasAction);
            return Fallback();
        }
    }

    private void LogResult(string scope, string outcome, System.Diagnostics.Stopwatch sw, int contextLen, int questionLen,
        bool hasState, bool hasOpp, bool hasRisk, bool hasAction)
    {
        sw.Stop();
        _logger.LogInformation(
            "ASSISTANT: scope={Scope} outcome={Outcome} durationMs={Ms} contextLen={CtxLen} questionLen={QLen} state={State} opp={Opp} risk={Risk} action={Action}",
            scope, outcome, sw.ElapsedMilliseconds, contextLen, questionLen, hasState, hasOpp, hasRisk, hasAction);
    }

    // ── Compact context builders ──

    private static string BuildBusinessContext(BusinessInsightsResponse d)
    {
        var sb = new StringBuilder(1024);

        // State
        if (!string.IsNullOrWhiteSpace(d.BusinessStateTitle))
            sb.AppendLine($"ESTADO: {d.BusinessStateTitle}");
        if (!string.IsNullOrWhiteSpace(d.MainOpportunity))
            sb.AppendLine($"OPORTUNIDAD: {d.MainOpportunity}");
        if (!string.IsNullOrWhiteSpace(d.MainRisk))
            sb.AppendLine($"RIESGO: {d.MainRisk}");
        if (!string.IsNullOrWhiteSpace(d.PrimaryRecommendation.Title))
            sb.AppendLine($"ACCIÓN PRINCIPAL: {d.PrimaryRecommendation.Title} — {d.PrimaryRecommendation.Action}");

        // Metrics
        var m = d.Metrics;
        sb.AppendLine($"Pedidos completados: {m.CompletedOrders}");
        sb.AppendLine($"Ticket promedio: ${m.AverageTicket:F2}");
        sb.AppendLine($"Items por pedido: {m.AverageItemsPerOrder:F1}");
        sb.AppendLine($"Tasa de recompra: {m.RepeatCustomerRate}%");
        sb.AppendLine($"Tasa de conversión: {m.ConversionRate}%");
        sb.AppendLine($"Pagos pendientes: {m.PendingPayments}");

        // Top selling (max 3)
        if (d.TopSellingItems.Count > 0)
        {
            sb.AppendLine("Más vendidos:");
            foreach (var item in d.TopSellingItems.Take(3))
                sb.AppendLine($"  - {item.Name}: {item.TotalQuantity} unidades");
        }

        // Low performing (max 3)
        if (d.LowPerformingItems.Count > 0)
        {
            sb.AppendLine("Menor demanda:");
            foreach (var item in d.LowPerformingItems.Take(3))
                sb.AppendLine($"  - {item.Name}: {item.TotalQuantity} unidades");
        }

        // Peak hours (max 3)
        if (d.PeakHours.Count > 0)
        {
            sb.AppendLine("Horas pico:");
            foreach (var h in d.PeakHours.Take(3))
                sb.AppendLine($"  - {h.Hour:D2}:00 → {h.OrderCount} pedidos");
        }

        // Alerts (max 3)
        foreach (var a in d.Alerts.Take(3))
            sb.AppendLine($"ALERTA: {a}");

        // Insights (max 3)
        foreach (var i in d.Insights.Take(3))
            sb.AppendLine($"INSIGHT: {i.Message}");

        // Recommendations (max 3)
        foreach (var r in d.Recommendations.Take(3))
            sb.AppendLine($"RECOMENDACIÓN: {r}");

        return Cap(sb);
    }

    private static string BuildFounderContext(FounderInsightsResponse d)
    {
        var sb = new StringBuilder(1024);

        // State
        if (!string.IsNullOrWhiteSpace(d.PlatformStateTitle))
            sb.AppendLine($"ESTADO: {d.PlatformStateTitle}");
        if (!string.IsNullOrWhiteSpace(d.MainOpportunity))
            sb.AppendLine($"OPORTUNIDAD: {d.MainOpportunity}");
        if (!string.IsNullOrWhiteSpace(d.MainRisk))
            sb.AppendLine($"RIESGO: {d.MainRisk}");
        if (!string.IsNullOrWhiteSpace(d.PrimaryRecommendation.Title))
            sb.AppendLine($"ACCIÓN PRINCIPAL: {d.PrimaryRecommendation.Title} — {d.PrimaryRecommendation.Action}");

        // Summary
        sb.AppendLine($"Pedidos totales (30d): {d.Summary.TotalOrders}");
        sb.AppendLine($"Revenue total: ${d.Summary.TotalRevenue:F2}");
        sb.AppendLine($"Promedio pedidos/negocio: {d.Summary.AvgOrdersPerBusiness:F1}");

        // Top businesses (max 3)
        if (d.TopBusinesses.Count > 0)
        {
            sb.AppendLine("Negocios top:");
            foreach (var b in d.TopBusinesses.Take(3))
                sb.AppendLine($"  - {b.Name}: {b.Orders} pedidos");
        }

        // Alerts (max 3)
        foreach (var a in d.Alerts.Take(3))
            sb.AppendLine($"ALERTA: {a.Message}");

        // Insights (max 3)
        foreach (var i in d.Insights.Take(3))
            sb.AppendLine($"INSIGHT: {i.Message}");

        // Recommendations (max 3)
        foreach (var r in d.Recommendations.Take(3))
            sb.AppendLine($"RECOMENDACIÓN: {r.Message}");

        return Cap(sb);
    }

    private static string Cap(StringBuilder sb)
    {
        if (sb.Length <= MaxContextChars)
            return sb.ToString();

        // Truncate cleanly at last newline before limit
        var text = sb.ToString(0, MaxContextChars);
        var lastNewline = text.LastIndexOf('\n');
        return lastNewline > 0 ? text[..lastNewline] : text;
    }

    private static string TrimToMaxSentences(string text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var sentences = text.Split(new[] { ". ", ".\n" }, StringSplitOptions.RemoveEmptyEntries);
        if (sentences.Length <= max) return text;
        return string.Join(". ", sentences.Take(max).Select(s => s.TrimEnd('.'))) + ".";
    }

    private static InsightsChatResponse Fallback() => new()
    {
        Answer = "No hay suficiente información para tomar una decisión todavía.",
        Confidence = "low"
    };
}
