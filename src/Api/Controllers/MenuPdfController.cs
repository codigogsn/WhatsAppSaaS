using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using WhatsAppSaaS.Api.Auth;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Infrastructure.Persistence;

namespace WhatsAppSaaS.Api.Controllers;

[ApiController]
[Authorize]
public sealed class MenuPdfController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<MenuPdfController> _logger;
    private const long MaxFileSize = 10 * 1024 * 1024; // 10 MB

    public MenuPdfController(AppDbContext db, IConfiguration config, ILogger<MenuPdfController> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Public endpoint: serves the menu PDF for a business. Used by WhatsApp document messages.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("api/menu-pdf/{businessId:guid}")]
    [ResponseCache(Duration = 300)] // 5 min cache
    public async Task<IActionResult> Serve(Guid businessId, CancellationToken ct)
    {
        var pdf = await _db.MenuPdfs
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.BusinessId == businessId, ct);

        if (pdf is null)
            return NotFound();

        return File(pdf.Data, pdf.ContentType, "menu.pdf");
    }

    /// <summary>
    /// Upload or replace a menu PDF for a business. Requires X-Admin-Key.
    /// </summary>
    [HttpPost("api/admin/businesses/{businessId:guid}/menu-pdf")]
    [EnableRateLimiting("admin")]
    [RequestSizeLimit(MaxFileSize)]
    public async Task<IActionResult> Upload(Guid businessId, IFormFile file, CancellationToken ct)
    {
        if (!await AuthorizeBusinessAsync(businessId, ct))
            return Unauthorized();

        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No file provided" });

        if (file.Length > MaxFileSize)
            return BadRequest(new { error = "File too large (max 10 MB)" });

        var contentType = file.ContentType?.ToLowerInvariant() ?? "";
        if (contentType != "application/pdf")
            return BadRequest(new { error = "Only PDF files are accepted" });

        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var data = ms.ToArray();

        // Upsert: replace existing or create new
        var existing = await _db.MenuPdfs
            .FirstOrDefaultAsync(p => p.BusinessId == businessId, ct);

        if (existing is not null)
        {
            existing.Data = data;
            existing.ContentType = "application/pdf";
            existing.UploadedAtUtc = DateTime.UtcNow;
        }
        else
        {
            _db.MenuPdfs.Add(new MenuPdf
            {
                BusinessId = businessId,
                Data = data,
                ContentType = "application/pdf",
                UploadedAtUtc = DateTime.UtcNow
            });
        }

        // Build the public serve URL and update via raw SQL to avoid legacy column issues
        var baseUrl = ResolveBaseUrl();
        var menuPdfUrl = $"{baseUrl}/api/menu-pdf/{businessId}";
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Businesses" SET "MenuPdfUrl" = {menuPdfUrl} WHERE "Id" = {businessId}""", ct);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Menu PDF endpoint error");
            return StatusCode(500, new { error = "Unexpected server error" });
        }

        return Ok(new
        {
            menuPdfUrl,
            fileName = file.FileName,
            sizeBytes = data.Length,
            uploadedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Delete the menu PDF for a business (reverts to global fallback).
    /// </summary>
    [HttpDelete("api/admin/businesses/{businessId:guid}/menu-pdf")]
    [EnableRateLimiting("admin")]
    public async Task<IActionResult> Delete(Guid businessId, CancellationToken ct)
    {
        if (!await AuthorizeBusinessAsync(businessId, ct))
            return Unauthorized();

        var existing = await _db.MenuPdfs
            .FirstOrDefaultAsync(p => p.BusinessId == businessId, ct);

        if (existing is not null)
            _db.MenuPdfs.Remove(existing);

        // Update via raw SQL to avoid legacy column issues
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""UPDATE "Businesses" SET "MenuPdfUrl" = NULL WHERE "Id" = {businessId}""", ct);

        await _db.SaveChangesAsync(ct);

        return Ok(new { message = "Menu PDF deleted. Business will use default menu." });
    }

    private string ResolveBaseUrl()
    {
        return _config["WhatsApp:PublicBaseUrl"]
            ?? Environment.GetEnvironmentVariable("PUBLIC_BASE_URL")
            ?? Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL")
            ?? Request.Scheme + "://" + Request.Host;
    }

    private async Task<bool> AuthorizeBusinessAsync(Guid businessId, CancellationToken ct)
    {
        // Path 1: JWT authorization
        if (AdminAuth.IsJwtAuthorizedForBusiness(User, businessId))
        {
            return await _db.Businesses.AnyAsync(b => b.Id == businessId, ct);
        }

        // Path 2: X-Admin-Key
        if (!Request.Headers.TryGetValue("X-Admin-Key", out var headerKey))
            return false;

        var key = headerKey.ToString().Trim();
        if (string.IsNullOrWhiteSpace(key))
            return false;

        // Accept global admin key (all known config sources)
        var globalKey = (_config["ADMIN_KEY"]
            ?? Environment.GetEnvironmentVariable("ADMIN_KEY")
            ?? Environment.GetEnvironmentVariable("WHATSAPP_ADMIN_KEY")
            ?? _config["WhatsApp:AdminKey"])?.Trim();
        if (!string.IsNullOrWhiteSpace(globalKey) && SafeEquals(key, globalKey))
            return await _db.Businesses.AnyAsync(b => b.Id == businessId, ct);

        // Accept per-business admin key — project only AdminKey to avoid legacy columns
        var bizKey = await _db.Businesses
            .Where(b => b.Id == businessId)
            .Select(b => b.AdminKey)
            .FirstOrDefaultAsync(ct);
        if (bizKey is not null && !string.IsNullOrWhiteSpace(bizKey) && SafeEquals(key, bizKey.Trim()))
            return true;

        return false;
    }

    private static bool SafeEquals(string a, string b)
        => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
