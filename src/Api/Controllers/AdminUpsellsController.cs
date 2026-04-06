using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Route("api/admin/upsells")]
[EnableRateLimiting("admin")]
public sealed class AdminUpsellsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<AdminUpsellsController> _logger;

    public AdminUpsellsController(AppDbContext db, IConfiguration config, ILogger<AdminUpsellsController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    // ── Auth (same pattern as AdminMenuController) ──

    private async Task<bool> IsAuthorizedForBusinessAsync(Guid businessId, CancellationToken ct)
    {
        if (AdminAuth.IsJwtAuthorizedForBusiness(User, businessId))
            return true;
        if (AdminAuth.IsGlobalAdminKey(Request, _config))
            return true;
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var hk) || string.IsNullOrWhiteSpace(hk))
            return false;
        try
        {
            var conn = _db.Database.GetDbConnection();
            if (conn.State != System.Data.ConnectionState.Open)
                await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT "AdminKey" FROM "Businesses"
                WHERE "Id"::text = @bid AND "IsActive"::boolean = true
                LIMIT 1
            """;
            var p = cmd.CreateParameter();
            p.ParameterName = "bid";
            p.Value = businessId.ToString();
            cmd.Parameters.Add(p);
            var result = await cmd.ExecuteScalarAsync(ct);
            if (result is null or DBNull) return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(hk.ToString().Trim()),
                Encoding.UTF8.GetBytes(result.ToString()?.Trim() ?? ""));
        }
        catch { return false; }
    }

    // ── DTOs ──

    public sealed class CreateUpsellRequest
    {
        public Guid SourceCategoryId { get; set; }
        public Guid? SuggestedMenuItemId { get; set; }
        public string? SuggestionLabel { get; set; }
        public string? CustomMessage { get; set; }
        public int SortOrder { get; set; }
    }

    public sealed class UpdateUpsellRequest
    {
        public Guid SourceCategoryId { get; set; }
        public Guid? SuggestedMenuItemId { get; set; }
        public string? SuggestionLabel { get; set; }
        public string? CustomMessage { get; set; }
        public bool IsActive { get; set; } = true;
        public int SortOrder { get; set; }
    }

    // ── Endpoints ──

    // GET /api/admin/upsells?businessId=xxx
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var rules = await _db.UpsellRules
            .Where(u => u.BusinessId == businessId)
            .OrderBy(u => u.SortOrder).ThenBy(u => u.SuggestionLabel)
            .Select(u => new
            {
                id = u.Id,
                sourceCategoryId = u.SourceCategoryId,
                sourceCategoryName = u.SourceCategory != null ? u.SourceCategory.Name : null,
                suggestedMenuItemId = u.SuggestedMenuItemId,
                suggestedMenuItemName = u.SuggestedMenuItem != null ? u.SuggestedMenuItem.Name : null,
                suggestionLabel = u.SuggestionLabel,
                customMessage = u.CustomMessage,
                isActive = u.IsActive,
                sortOrder = u.SortOrder
            })
            .ToListAsync(ct);

        return Ok(rules);
    }

    // GET /api/admin/upsells/{id}?businessId=xxx
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, [FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var rule = await _db.UpsellRules
            .Where(u => u.Id == id && u.BusinessId == businessId)
            .Select(u => new
            {
                id = u.Id,
                sourceCategoryId = u.SourceCategoryId,
                sourceCategoryName = u.SourceCategory != null ? u.SourceCategory.Name : null,
                suggestedMenuItemId = u.SuggestedMenuItemId,
                suggestedMenuItemName = u.SuggestedMenuItem != null ? u.SuggestedMenuItem.Name : null,
                suggestionLabel = u.SuggestionLabel,
                customMessage = u.CustomMessage,
                isActive = u.IsActive,
                sortOrder = u.SortOrder
            })
            .FirstOrDefaultAsync(ct);

        return rule is null ? NotFound() : Ok(rule);
    }

    // POST /api/admin/upsells?businessId=xxx
    [HttpPost]
    public async Task<IActionResult> Create([FromQuery] Guid businessId, [FromBody] CreateUpsellRequest req, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();
        if (req.SourceCategoryId == Guid.Empty)
            return BadRequest("SourceCategoryId is required.");
        if (req.SuggestedMenuItemId is null && string.IsNullOrWhiteSpace(req.SuggestionLabel))
            return BadRequest("Either SuggestedMenuItemId or SuggestionLabel is required.");

        var rule = new UpsellRule
        {
            BusinessId = businessId,
            SourceCategoryId = req.SourceCategoryId,
            SuggestedMenuItemId = req.SuggestedMenuItemId,
            SuggestionLabel = req.SuggestionLabel?.Trim(),
            CustomMessage = req.CustomMessage?.Trim(),
            SortOrder = req.SortOrder,
            IsActive = true
        };

        _db.UpsellRules.Add(rule);
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = rule.Id });
    }

    // PUT /api/admin/upsells/{id}?businessId=xxx
    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromQuery] Guid businessId, [FromBody] UpdateUpsellRequest req, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();
        if (req.SourceCategoryId == Guid.Empty)
            return BadRequest("SourceCategoryId is required.");

        var rule = await _db.UpsellRules
            .FirstOrDefaultAsync(u => u.Id == id && u.BusinessId == businessId, ct);

        if (rule is null) return NotFound();

        rule.SourceCategoryId = req.SourceCategoryId;
        rule.SuggestedMenuItemId = req.SuggestedMenuItemId;
        rule.SuggestionLabel = req.SuggestionLabel?.Trim();
        rule.CustomMessage = req.CustomMessage?.Trim();
        rule.SortOrder = req.SortOrder;
        rule.IsActive = req.IsActive;

        await _db.SaveChangesAsync(ct);
        return Ok(new { id = rule.Id });
    }

    // PATCH /api/admin/upsells/{id}/toggle?businessId=xxx
    [HttpPatch("{id:guid}/toggle")]
    public async Task<IActionResult> Toggle(Guid id, [FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var rule = await _db.UpsellRules
            .FirstOrDefaultAsync(u => u.Id == id && u.BusinessId == businessId, ct);

        if (rule is null) return NotFound();

        rule.IsActive = !rule.IsActive;
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = rule.Id, isActive = rule.IsActive });
    }

    // DELETE /api/admin/upsells/{id}?businessId=xxx
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] Guid businessId, CancellationToken ct)
    {
        if (!await IsAuthorizedForBusinessAsync(businessId, ct)) return Unauthorized();

        var rule = await _db.UpsellRules
            .FirstOrDefaultAsync(u => u.Id == id && u.BusinessId == businessId, ct);

        if (rule is null) return NotFound();

        _db.UpsellRules.Remove(rule);
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }
}
