using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Infrastructure.Services;

public sealed class FounderInsightsService : IFounderInsightsService
{
    private readonly AppDbContext _db;
    private readonly ILogger<FounderInsightsService> _logger;

    public FounderInsightsService(AppDbContext db, ILogger<FounderInsightsService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<FounderInsightsResponse> GetInsightsAsync(CancellationToken ct)
    {
        var windowStart = DateTime.UtcNow.AddDays(-30);

        // ── 1. Orders by business (last 30 days, completed) ──
        var bizOrders = await _db.Orders.AsNoTracking()
            .Where(o => o.CheckoutCompleted && o.CreatedAtUtc >= windowStart && o.BusinessId.HasValue)
            .GroupBy(o => o.BusinessId!.Value)
            .Select(g => new { BusinessId = g.Key, Count = g.Count(), Revenue = g.Sum(o => o.TotalAmount ?? 0m) })
            .OrderByDescending(x => x.Count)
            .Take(1000)
            .ToListAsync(ct);

        var totalOrders = bizOrders.Sum(b => b.Count);
        var totalRevenue = bizOrders.Sum(b => b.Revenue);
        var bizCount = bizOrders.Count;
        var avgOrdersPerBiz = bizCount > 0 ? Math.Round((decimal)totalOrders / bizCount, 1) : 0m;

        // ── 2. Business names lookup ──
        var bizIds = bizOrders.Select(b => b.BusinessId).ToHashSet();
        var bizNames = await _db.Businesses.AsNoTracking()
            .Where(b => bizIds.Contains(b.Id))
            .OrderBy(b => b.Id)
            .Select(b => new { b.Id, b.Name })
            .Take(1000)
            .ToListAsync(ct);
        var nameMap = bizNames.ToDictionary(b => b.Id, b => b.Name);

        // ── 3. Top businesses by orders ──
        var topBusinesses = bizOrders.Take(5)
            .Select(b => new FounderBusinessRank
            {
                BusinessId = b.BusinessId,
                Name = nameMap.GetValueOrDefault(b.BusinessId, "Unknown"),
                Orders = b.Count
            }).ToList();

        // ── 4. Repeat rate per business ──
        var customerStats = await _db.Customers.AsNoTracking()
            .Where(c => c.BusinessId.HasValue && bizIds.Contains(c.BusinessId.Value))
            .GroupBy(c => c.BusinessId!.Value)
            .Select(g => new
            {
                BusinessId = g.Key,
                Total = g.Count(),
                Repeat = g.Count(c => c.OrdersCount > 1)
            })
            .OrderBy(x => x.BusinessId)
            .Take(1000)
            .ToListAsync(ct);

        var repeatByBiz = customerStats
            .Where(c => c.Total >= 5)
            .Select(c => new
            {
                c.BusinessId,
                Rate = Math.Round((decimal)c.Repeat / c.Total * 100, 1),
                Name = nameMap.GetValueOrDefault(c.BusinessId, "Unknown")
            }).ToList();

        // ── 5. Pending payments per business ──
        var pendingByBiz = await _db.Orders.AsNoTracking()
            .Where(o => o.BusinessId.HasValue && o.CheckoutCompleted
                     && o.PaymentProofMediaId != null && o.PaymentVerifiedAtUtc == null
                     && o.CreatedAtUtc >= windowStart)
            .GroupBy(o => o.BusinessId!.Value)
            .Select(g => new { BusinessId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(100)
            .ToListAsync(ct);

        // ── 6. Conversations per business (for conversion rate) ──
        var convByBiz = await _db.ConversationStates.AsNoTracking()
            .Where(cs => cs.BusinessId.HasValue && cs.UpdatedAtUtc >= windowStart)
            .GroupBy(cs => cs.BusinessId!.Value)
            .Select(g => new { BusinessId = g.Key, Count = g.Count() })
            .OrderBy(x => x.BusinessId)
            .Take(1000)
            .ToListAsync(ct);

        var convMap = convByBiz.ToDictionary(c => c.BusinessId, c => c.Count);
        var orderMap = bizOrders.ToDictionary(b => b.BusinessId, b => b.Count);

        var conversionByBiz = convByBiz
            .Where(c => c.Count >= 10)
            .Select(c =>
            {
                var orders = orderMap.GetValueOrDefault(c.BusinessId, 0);
                return new
                {
                    c.BusinessId,
                    Rate = Math.Round((decimal)orders / c.Count * 100, 1),
                    Name = nameMap.GetValueOrDefault(c.BusinessId, "Unknown")
                };
            }).ToList();

        // ── Build insights ──
        var alerts = new List<FounderAlert>();
        var insights = new List<FounderInsightEntry>();
        var recommendations = new List<FounderRecommendation>();

        // High pending payments
        foreach (var p in pendingByBiz.Where(p => p.Count >= 3).Take(3))
        {
            alerts.Add(new FounderAlert
            {
                Type = "high_pending_payments",
                Severity = p.Count >= 5 ? "high" : "medium",
                Message = $"{nameMap.GetValueOrDefault(p.BusinessId, "Business")} has {p.Count} unverified payments"
            });
        }

        // Low conversion
        foreach (var c in conversionByBiz.OrderBy(c => c.Rate).Take(3).Where(c => c.Rate < 10))
        {
            alerts.Add(new FounderAlert
            {
                Type = "low_conversion",
                Severity = "medium",
                Message = $"{c.Name} has {c.Rate}% conversion rate"
            });
        }

        // Top performer
        if (topBusinesses.Count > 0)
        {
            var top = topBusinesses[0];
            insights.Add(new FounderInsightEntry
            {
                Type = "top_performer",
                Message = $"{top.Name} leads with {top.Orders} orders in 30 days"
            });
        }

        // Strong repeat rate
        foreach (var r in repeatByBiz.OrderByDescending(r => r.Rate).Take(2).Where(r => r.Rate >= 40))
        {
            insights.Add(new FounderInsightEntry
            {
                Type = "strong_retention",
                Message = $"{r.Name} has {r.Rate}% repeat customer rate"
            });
        }

        // Weak repeat rate
        foreach (var r in repeatByBiz.OrderBy(r => r.Rate).Take(2).Where(r => r.Rate < 15))
        {
            insights.Add(new FounderInsightEntry
            {
                Type = "weak_retention",
                Message = $"{r.Name} has only {r.Rate}% repeat rate"
            });
            recommendations.Add(new FounderRecommendation
            {
                Type = "improve_retention",
                Message = $"Help {r.Name} set up loyalty incentives or follow-up messages"
            });
        }

        // Low conversion recommendation
        var worstConv = conversionByBiz.OrderBy(c => c.Rate).FirstOrDefault();
        if (worstConv != null && worstConv.Rate < 10)
        {
            recommendations.Add(new FounderRecommendation
            {
                Type = "improve_conversion",
                Message = $"Review {worstConv.Name} menu and bot flow — {worstConv.Rate}% conversion is below threshold"
            });
        }

        // Platform growth
        if (bizCount >= 3 && avgOrdersPerBiz >= 5)
        {
            insights.Add(new FounderInsightEntry
            {
                Type = "platform_health",
                Message = $"Platform averaging {avgOrdersPerBiz} orders/business across {bizCount} active businesses"
            });
        }

        // ── Derive platform state (deterministic) ──
        var (platformTitle, platformSummary) = DerivePlatformState(bizCount, totalOrders, avgOrdersPerBiz, alerts.Count);
        var mainOpp = topBusinesses.Count > 0
            ? $"Convertir a {topBusinesses[0].Name} en caso de éxito — lidera con {topBusinesses[0].Orders} pedidos."
            : "Onboardear nuevos negocios para diversificar el portafolio.";

        var mainRisk = alerts.Count > 0
            ? alerts[0].Message
            : (worstConv != null && worstConv.Rate < 10
                ? $"{worstConv.Name} tiene conversión crítica ({worstConv.Rate}%)."
                : "Sin riesgos críticos detectados en el portafolio.");

        var worstConvTuple = worstConv != null
            ? ((Guid BusinessId, decimal Rate, string Name)?)(worstConv.BusinessId, worstConv.Rate, worstConv.Name)
            : null;
        var primaryRec = DerivePlatformRecommendation(alerts, topBusinesses, worstConvTuple);

        _logger.LogInformation("FOUNDER INSIGHTS: generated — businesses={Biz} orders={Orders} revenue={Revenue} state={State}",
            bizCount, totalOrders, totalRevenue, platformTitle);

        return new FounderInsightsResponse
        {
            PlatformStateTitle = platformTitle,
            PlatformStateSummary = platformSummary,
            MainOpportunity = mainOpp,
            MainRisk = mainRisk,
            PrimaryRecommendation = primaryRec,
            Summary = new FounderInsightsSummary
            {
                TotalOrders = totalOrders,
                TotalRevenue = totalRevenue,
                AvgOrdersPerBusiness = avgOrdersPerBiz
            },
            Alerts = alerts.Take(5).ToList(),
            Insights = insights.Take(5).ToList(),
            Recommendations = recommendations.Take(5).ToList(),
            TopBusinesses = topBusinesses
        };
    }

    private static (string title, string summary) DerivePlatformState(int bizCount, int totalOrders, decimal avgPerBiz, int alertCount)
    {
        if (bizCount == 0)
            return ("Portafolio vacío", "No hay negocios activos con pedidos en los últimos 30 días.");
        if (alertCount >= 3)
            return ("Portafolio con atención requerida", $"{alertCount} alertas activas en {bizCount} negocios. Revisión prioritaria necesaria.");
        if (bizCount >= 3 && avgPerBiz >= 10)
            return ("Portafolio saludable con tracción", $"{bizCount} negocios activos promediando {avgPerBiz} pedidos cada uno.");
        if (bizCount >= 2 && avgPerBiz >= 5)
            return ("Portafolio estable con oportunidad de expansión", $"{bizCount} negocios generando actividad regular.");
        return ("Portafolio en desarrollo", $"{bizCount} negocio(s) activo(s) con {totalOrders} pedidos totales.");
    }

    private static FounderActionableRecommendation DerivePlatformRecommendation(
        List<FounderAlert> alerts, List<FounderBusinessRank> topBiz,
        (Guid BusinessId, decimal Rate, string Name)? worstConv)
    {
        if (alerts.Count > 0)
            return new FounderActionableRecommendation
            {
                Title = "Atender alerta prioritaria",
                Action = alerts[0].Message,
                Impact = "Resolver alertas activas previene pérdida de ingresos y clientes."
            };

        if (worstConv.HasValue && worstConv.Value.Rate < 10)
            return new FounderActionableRecommendation
            {
                Title = $"Mejorar conversión de {worstConv.Value.Name}",
                Action = $"Revisar menú y flujo del bot — {worstConv.Value.Rate}% de conversión está debajo del umbral.",
                Impact = "Duplicar conversión genera más pedidos sin inversión adicional en tráfico."
            };

        if (topBiz.Count > 0)
            return new FounderActionableRecommendation
            {
                Title = $"Replicar éxito de {topBiz[0].Name}",
                Action = "Documenta qué hace diferente este negocio y aplícalo a los demás.",
                Impact = "Escalar las mejores prácticas acelera el crecimiento del portafolio."
            };

        return new FounderActionableRecommendation
        {
            Title = "Expandir portafolio",
            Action = "Onboardea nuevos restaurantes para diversificar ingresos.",
            Impact = "Más negocios activos estabiliza la plataforma."
        };
    }
}
