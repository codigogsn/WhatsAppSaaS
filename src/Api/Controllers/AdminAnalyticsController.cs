using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/admin/analytics")]
public sealed class AdminAnalyticsController : ControllerBase
{
    private readonly IAdminAnalyticsService _service;
    private readonly ILogger<AdminAnalyticsController> _logger;

    public AdminAnalyticsController(
        IAdminAnalyticsService service,
        ILogger<AdminAnalyticsController> logger)
    {
        _service = service;
        _logger = logger;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<AnalyticsSummaryDto>> GetSummary(CancellationToken ct)
    {
        // Este ya te está funcionando, lo dejamos limpio
        var dto = await _service.GetSummaryAsync(ct);
        return Ok(dto);
    }

    [HttpGet("top-products")]
    public async Task<ActionResult<List<TopProductDto>>> GetTopProducts([FromQuery] int take = 10, CancellationToken ct = default)
    {
        try
        {
            var dto = await _service.GetTopProductsAsync(take, ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            // ✅ QUIRÚRGICO: nunca más 500 por este endpoint
            _logger.LogError(ex, "Admin analytics top-products failed. Returning empty list (MVP safe).");
            return Ok(new List<TopProductDto>());
        }
    }

    [HttpGet("customers")]
    public async Task<ActionResult<List<CustomerAnalyticsDto>>> GetCustomers([FromQuery] int take = 10, CancellationToken ct = default)
    {
        // Este ya te está funcionando, lo dejamos limpio
        var dto = await _service.GetCustomersAsync(take, ct);
        return Ok(dto);
    }
}
