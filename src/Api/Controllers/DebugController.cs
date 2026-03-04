using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("debug")]
public class DebugController : ControllerBase
{
    [HttpGet("version")]
    public IActionResult Version()
    {
        // Render / Docker suele exponer RENDER_GIT_COMMIT, si no, mostramos "unknown"
        var sha = Environment.GetEnvironmentVariable("RENDER_GIT_COMMIT")
                  ?? Environment.GetEnvironmentVariable("GIT_COMMIT")
                  ?? "unknown";

        return Ok(new
        {
            commit = sha,
            utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
        });
    }
}
