using System.Text.Json;
using Maddalena.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace Maddalena.Controllers;

// Download di Sperone sotto il NOSTRO dominio. Due endpoint:
//  - GET /api/download/sperone      elenco JSON (versione + pacchetti + dimensioni), per le pagine statiche;
//  - GET /download/sperone/{platform} proxy binario: scarica l'asset da GitHub e lo ripassa in streaming.
// Il proxy usa l'URL stabile /releases/latest/download/<file> (niente API GitHub, niente rate limit),
// mentre l'elenco legge l'API GitHub una volta e la tiene in cache (per versione e dimensioni non c'e'
// scorciatoia). Cosi' il pulsante "Scarica" punta a maddalena.me e segue sempre l'ultima release.
[ApiController]
[Route("download/sperone")]
public sealed class DownloadController(IHttpClientFactory httpClientFactory, IMemoryCache cache) : ControllerBase
{
    // I pacchetti pubblicati nella release, in ordine, uno per piattaforma. Unica fonte dei nomi asset:
    // per aggiungere una piattaforma (es. linux-arm64) basta una riga qui.
    private static readonly (string Platform, string Asset)[] Downloads =
    [
        ("win", "sperone-win-x64.zip"),
        ("linux", "sperone-linux-x64.zip"),
        ("mac", "sperone-osx-arm64.zip"),
    ];

    // GitHub redirige questo verso l'asset dell'ultima release, qualunque sia il tag corrente.
    private const string LatestDownloadBase =
        "https://github.com/matteofabbri/sperone-releases/releases/latest/download/";

    private const string LatestReleaseApi =
        "repos/matteofabbri/sperone-releases/releases/latest";

    private const string CacheKey = "sperone-latest-downloads";

    // Elenco dei download: versione dell'ultima release e un pacchetto (nome, dimensione, url) per
    // piattaforma. In cache 15 minuti per non superare il rate limit dell'API GitHub.
    [HttpGet("~/api/download/sperone")]
    [ProducesResponseType<SperoneDownloadsResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        if (cache.TryGetValue(CacheKey, out SperoneDownloadsResponse? cached) && cached is not null)
            return Ok(cached);

        var fresh = await FetchLatestAsync(ct);
        if (fresh is null)
            return StatusCode(StatusCodes.Status502BadGateway, "Could not read the latest release from GitHub.");

        cache.Set(CacheKey, fresh, TimeSpan.FromMinutes(15));
        return Ok(fresh);
    }

    // Scarica l'ultima Sperone per la piattaforma indicata: win | linux | mac (osx). Fa da proxy: il
    // browser scarica da maddalena.me, noi ripassiamo i byte dell'asset GitHub in streaming (i pacchetti
    // pesano ~60-100 MB, quindi niente buffering in memoria). Anonimo: il tier free scarica senza account.
    [HttpGet("{platform}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Get(string platform, CancellationToken ct)
    {
        string? asset = AssetFor(platform);
        if (asset is null)
            return NotFound($"Unknown platform '{platform}'. Use one of: win, linux, mac.");

        var client = httpClientFactory.CreateClient("github-releases");
        using var upstream = await client.GetAsync(
            LatestDownloadBase + asset, HttpCompletionOption.ResponseHeadersRead, ct);

        if (!upstream.IsSuccessStatusCode)
            return StatusCode(StatusCodes.Status502BadGateway,
                $"Could not fetch the latest release from GitHub (HTTP {(int)upstream.StatusCode}).");

        // Propaga tipo e dimensione: la Content-Length fa vedere al browser la barra di avanzamento.
        Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/zip";
        if (upstream.Content.Headers.ContentLength is long length)
            Response.ContentLength = length;
        // asset viene da una whitelist fissa: nessun rischio di header injection nel filename.
        Response.Headers.ContentDisposition = $"attachment; filename=\"{asset}\"";

        await using var body = await upstream.Content.ReadAsStreamAsync(ct);
        await body.CopyToAsync(Response.Body, ct);
        return new EmptyResult();
    }

    // Nome esatto dell'asset per un token di piattaforma; "osx" e' un alias di "mac". null se sconosciuto.
    private static string? AssetFor(string platform)
    {
        string p = platform.Equals("osx", StringComparison.OrdinalIgnoreCase) ? "mac" : platform;
        foreach (var (candidate, asset) in Downloads)
            if (candidate.Equals(p, StringComparison.OrdinalIgnoreCase))
                return asset;
        return null;
    }

    // Legge l'ultima release dall'API GitHub e ne ricava versione + dimensioni. null su qualunque
    // errore (rete, rate limit, JSON): il chiamante risponde 502 SENZA mettere il fallimento in cache.
    private async Task<SperoneDownloadsResponse?> FetchLatestAsync(CancellationToken ct)
    {
        try
        {
            var client = httpClientFactory.CreateClient("github-api");
            using var resp = await client.GetAsync(LatestReleaseApi, ct);
            if (!resp.IsSuccessStatusCode)
                return null;

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string version = root.TryGetProperty("tag_name", out var tag) ? tag.GetString() ?? "" : "";

            // Indicizza le dimensioni per nome asset, poi le abbina alle piattaforme che conosciamo.
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                    if (a.TryGetProperty("name", out var name) && name.GetString() is string n
                        && a.TryGetProperty("size", out var size))
                        sizes[n] = size.GetInt64();

            var list = Downloads.Select(d => new SperoneDownload
            {
                Platform = d.Platform,
                Name = d.Asset,
                Size = sizes.TryGetValue(d.Asset, out long s) ? s : 0,
                Url = $"/download/sperone/{d.Platform}",
            }).ToList();

            return new SperoneDownloadsResponse { Version = version, Assets = list };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return null;
        }
    }
}
