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
        Eres un copiloto de decisiones de negocio para restaurantes que venden por WhatsApp.
        Responde SOLO usando los datos proporcionados. NO inventes datos.
        Estilo: directo, estratégico, accionable. Siempre en español.
        Formato: máximo 3 oraciones. Empieza con el hallazgo clave, luego la acción.
        Usa frases como "Tu negocio muestra...", "La oportunidad más clara es...", "La acción recomendada es...".
        NO seas genérico. NO motives sin sustento. NO repitas métricas sin interpretar.
        Si los datos son insuficientes, dilo en una oración.
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

    public async Task<InsightsChatResponse> AskAsync(InsightsChatRequest request, Guid? businessId, CancellationToken ct)
    {
        // 1. Build compact context from existing insights services (no direct DB access)
        string context;
        try
        {
            if (request.Scope == "founder")
            {
                var data = await _founderInsights.GetInsightsAsync(ct);
                context = BuildFounderContext(data);
            }
            else
            {
                if (!businessId.HasValue)
                    return new InsightsChatResponse { Answer = "No hay contexto de negocio disponible.", Confidence = "low" };

                var data = await _bizInsights.GetInsightsAsync(businessId.Value, 30, ct);
                context = BuildBusinessContext(data);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "INSIGHTS CHAT: failed to gather context for scope={Scope}", request.Scope);
            return Fallback();
        }

        // 2. Call OpenAI with compact context
        var apiKey = _options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("INSIGHTS CHAT: OPENAI_API_KEY not configured");
            return Fallback();
        }

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
                    new { role = "system", content = SystemPrompt },
                    new { role = "user", content = $"Datos del negocio:\n{context}\n\nPregunta: {request.Question}" }
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
                return Fallback();
            }

            var raw = await res.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(raw);
            var answer = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";

            _logger.LogInformation("INSIGHTS CHAT: answered for scope={Scope} contextLen={Len}",
                request.Scope, context.Length);

            return new InsightsChatResponse
            {
                Answer = answer.Trim(),
                Confidence = "medium"
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("INSIGHTS CHAT: OpenAI call timed out");
            return Fallback();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "INSIGHTS CHAT: OpenAI call failed");
            return Fallback();
        }
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

    private static InsightsChatResponse Fallback() => new()
    {
        Answer = "Puedo ver los insights del panel, pero no pude generar una respuesta en este momento.",
        Confidence = "low"
    };
}
