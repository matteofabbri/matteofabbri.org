using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;

namespace MainSite.Controllers;

// Media della pagina 404 caricati da /admin/404. Pubblici, ma solo dalla
// cartella dedicata e con i formati consentiti.
[ApiController]
[Route("404-media")]
public sealed class NotFoundMediaController(NotFoundMediaStore media) : ControllerBase
{
    [HttpGet("{fileName}")]
    public IActionResult Get(string fileName)
    {
        string? path = media.ResolvePath(fileName);
        string? contentType = media.ContentTypeFor(fileName);
        if (path is null || contentType is null)
            return NotFound();

        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        return PhysicalFile(path, contentType);
    }
}
