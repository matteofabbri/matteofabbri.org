using MainSite.Blog;
using Microsoft.AspNetCore.Mvc;

namespace MainSite.Controllers;

// Le immagini caricate dall'editor. Pubbliche (le vedono i lettori), ma solo
// dalla cartella media e solo con i formati consentiti. I nomi sono unici, così
// si possono mettere in cache "per sempre".
[ApiController]
[Route("blog/media")]
public sealed class BlogMediaController(BlogMediaStore media) : ControllerBase
{
    [HttpGet("{fileName}")]
    public IActionResult Get(string fileName)
    {
        string? path = media.ResolvePath(fileName);
        string? contentType = media.ContentTypeFor(fileName);
        if (path is null || contentType is null)
            return NotFound();

        // Il tipo lo decidiamo noi dall'estensione e il browser non deve
        // indovinarlo: un file rinominato non diventa HTML eseguibile.
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        Response.Headers["Cache-Control"] = "public, max-age=31536000, immutable";
        return PhysicalFile(path, contentType);
    }
}
