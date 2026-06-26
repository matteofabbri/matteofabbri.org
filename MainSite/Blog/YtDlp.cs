using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// Configurazione del wrapper (sezione "YtDlp" in appsettings.json).
public sealed class YtDlpOptions
{
    public const string Section = "YtDlp";

    // Percorso esplicito dell'eseguibile. Vuoto = lo si cerca da solo: la
    // variabile d'ambiente YT_DLP_PATH, la copia scaricata, poi il PATH.
    public string ExecutablePath { get; set; } = "";

    // Se non c'è da nessuna parte, scarica il binario ufficiale per la
    // piattaforma corrente nella cartella del blog: una volta sola, poi resta.
    public bool AutoInstall { get; set; } = true;

    // Limite per singolo link: un video lento non deve tenere ferma la richiesta.
    public int TimeoutSeconds { get; set; } = 600;

    // Quanti link elabora un singolo clic. Il resto viene segnalato e ignorato.
    public int MaxLinksPerRun { get; set; } = 20;

    // Selettore di formato. Vuoto = automatico: con ffmpeg si prende la
    // qualità migliore e si uniscono video e audio, senza ffmpeg un file unico
    // già pronto (mp4). Un valore esplicito ha sempre la precedenza.
    public string FormatSelector { get; set; } = "";

    // Percorso di ffmpeg. Vuoto = lo cerca da solo (FFMPEG_PATH, cartella del
    // blog, PATH). Con ffmpeg il wrapper prende la qualità migliore e unisce
    // video e audio; senza, si accontenta di un file già pronto.
    public string FfmpegPath { get; set; } = "";

    // Tetto per file scaricato, per non riempire il volume con un video intero.
    public long MaxFileBytes { get; set; } = 200L * 1024 * 1024;

    // Argomenti extra separati da spazio, accodati a ogni invocazione.
    public string ExtraArguments { get; set; } = "";
}

// Errore "previsto" del wrapper: binario assente, link non supportato, timeout.
// Il chiamante lo mostra all'admin invece di trasformarlo in un 500.
public sealed class YtDlpException(string message) : Exception(message);

public sealed record YtDlpFile(string FileName, long Size);

// Un wrapper sottile intorno a yt-dlp che funziona uguale su Windows e Linux:
// trova (o installa) il binario giusto, invoca il processo senza passare da una
// shell e restituisce i file comparsi nella cartella di destinazione.
public sealed class YtDlp
{
    public const string HttpClientName = "yt-dlp";

    private const string LatestReleaseBase =
        "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";

    private static readonly string[] BaseArguments =
    [
        "--no-playlist",
        "--no-progress",
        "--no-warnings",
        "--no-colors",
        "--restrict-filenames",
    ];

    // Nome del file: stabile per lo stesso video (id), leggibile (titolo) e
    // già pulito per un URL (restrict-filenames).
    private const string FileNameTemplate = "%(title,id).80s-%(id)s.%(ext)s";

    private readonly YtDlpOptions _options;
    private readonly BlogOptions _blogOptions;
    private readonly IHostEnvironment _environment;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<YtDlp> _logger;

    // Una invocazione alla volta: due download in parallelo nella stessa
    // cartella renderebbero ambiguo il "prima e dopo" dei file.
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);

    private string? _executable;
    private string? _ffmpeg;
    private bool _ffmpegResolved;

    public YtDlp(
        IOptions<YtDlpOptions> options,
        IOptions<BlogOptions> blogOptions,
        IHostEnvironment environment,
        IHttpClientFactory httpClientFactory,
        ILogger<YtDlp> logger)
    {
        _options = options.Value;
        _blogOptions = blogOptions.Value;
        _environment = environment;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public static string ExecutableName => OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp";

    // Dove finisce il binario scaricato: nella cartella del blog (volume), così
    // sopravvive ai riavvii del container.
    public string ToolsDirectory =>
        MediaLibrary.BlogDirectory(Options.Create(_blogOptions), _environment, "tools");

    // Asset del binario ufficiale per piattaforma. null = piattaforma non coperta.
    public static string? AssetName(OSPlatform os, Architecture architecture)
    {
        if (os == OSPlatform.Windows)
            return "yt-dlp.exe";
        if (os == OSPlatform.Linux)
            return architecture switch
            {
                Architecture.Arm64 => "yt-dlp_linux_aarch64",
                Architecture.Arm => "yt-dlp_linux_armv7l",
                _ => "yt-dlp_linux",
            };
        if (os == OSPlatform.OSX)
            return "yt-dlp_macos";
        return null;
    }

    // Con ffmpeg si prende la qualità migliore e si uniscono video e audio;
    // senza, yt-dlp non può fare il merge, quindi si sceglie un file unico già
    // pronto (mp4, tipicamente 360p/720p su YouTube).
    public static string DefaultFormatSelector(bool hasFfmpeg) => hasFfmpeg
        ? "bv*[ext=mp4]+ba[ext=m4a]/b[ext=mp4]/b"
        : "b[ext=mp4]/b";

    // Scarica un link nella cartella indicata. Restituisce i file creati in
    // questa esecuzione: lista vuota = file già presente (yt-dlp non
    // sovrascrive per default, quindi rieseguire lo stesso link non duplica).
    public async Task<IReadOnlyList<YtDlpFile>> DownloadAsync(
        string url,
        string directory,
        CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new YtDlpException("Only http and https links can be downloaded.");

        Directory.CreateDirectory(directory);
        string executable = await ResolveExecutableAsync(ct);

        await _runGate.WaitAsync(ct);
        try
        {
            var before = new HashSet<string>(
                Directory.EnumerateFiles(directory).Select(path => Path.GetFileName(path)!),
                StringComparer.OrdinalIgnoreCase);

            await RunAsync(BuildStartInfo(executable, directory, uri.AbsoluteUri), ct);

            return Directory.EnumerateFiles(directory)
                .Select(path => new FileInfo(path))
                .Where(file => !before.Contains(file.Name) && !IsPartial(file.Name))
                .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Select(file => new YtDlpFile(file.Name, file.Length))
                .ToList();
        }
        finally
        {
            _runGate.Release();
        }
    }

    private static bool IsPartial(string name) =>
        name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    private ProcessStartInfo BuildStartInfo(string executable, string directory, string url)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in BaseArguments)
            startInfo.ArgumentList.Add(argument);

        // Con ffmpeg yt-dlp può unire video e audio e puntare alla qualità
        // vera; senza, il selettore automatico resta su un file unico.
        string? ffmpeg = FindFfmpeg();
        if (ffmpeg is not null)
        {
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(ffmpeg);
        }

        startInfo.ArgumentList.Add("--paths");
        startInfo.ArgumentList.Add(directory);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(FileNameTemplate);
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add(SelectFormat(hasFfmpeg: ffmpeg is not null));

        if (_options.MaxFileBytes > 0)
        {
            startInfo.ArgumentList.Add("--max-filesize");
            startInfo.ArgumentList.Add(FormatSize(_options.MaxFileBytes));
        }

        foreach (string extra in SplitArguments(_options.ExtraArguments))
            startInfo.ArgumentList.Add(extra);

        startInfo.ArgumentList.Add(url);
        return startInfo;
    }

    private async Task<string> RunAsync(ProcessStartInfo startInfo, CancellationToken ct)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new YtDlpException("Could not start yt-dlp.");

        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(10, _options.TimeoutSeconds)));

        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            try
            {
                await Task.WhenAll(stdout, stderr);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Il processo è stato ucciso: gli stream possono chiudersi a metà.
            }

            if (ct.IsCancellationRequested)
                throw;

            throw new YtDlpException($"yt-dlp timed out after {_options.TimeoutSeconds} seconds.");
        }

        string error = (await stderr).Trim();
        if (process.ExitCode != 0)
            throw new YtDlpException(Summarize(error) ?? $"yt-dlp exited with code {process.ExitCode}.");

        return await stdout;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            // Processo già uscito (o piattaforma che non lo supporta): niente da fare.
        }
    }

    // Le ultime righe di stderr sono quelle utili: yt-dlp ci mette il motivo,
    // mentre l'output normale è rumoroso.
    private static string? Summarize(string error)
    {
        if (string.IsNullOrWhiteSpace(error))
            return null;

        string[] lines = error
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase))
            .Select(line => line[6..].Trim())
            .ToArray();

        string text = lines.Length > 0
            ? string.Join(" ", lines)
            : error.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

        return text.Length <= 500 ? text : text[..500] + "…";
    }

    private async Task<string> ResolveExecutableAsync(CancellationToken ct)
    {
        if (_executable is not null)
            return _executable;

        await _installGate.WaitAsync(ct);
        try
        {
            if (_executable is not null)
                return _executable;

            string? installed = FindInstalled();
            if (installed is not null)
                return _executable = installed;

            if (!_options.AutoInstall)
                throw new YtDlpException(
                    "yt-dlp is not installed. Set YtDlp:ExecutablePath, put it on PATH, or enable YtDlp:AutoInstall.");

            _executable = await InstallAsync(ct);
            return _executable;
        }
        finally
        {
            _installGate.Release();
        }
    }

    // Ordine di ricerca: percorso configurato, variabile d'ambiente, copia
    // scaricata nella cartella del blog, PATH di sistema.
    private string? FindInstalled()
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(_options.ExecutablePath))
            candidates.Add(Path.IsPathRooted(_options.ExecutablePath)
                ? _options.ExecutablePath
                : Path.Combine(_environment.ContentRootPath, _options.ExecutablePath));

        string? fromEnvironment = Environment.GetEnvironmentVariable("YT_DLP_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            candidates.Add(fromEnvironment);

        candidates.Add(Path.Combine(ToolsDirectory, ExecutableName));

        foreach (string candidate in candidates)
        {
            if (!IsUsable(candidate))
                continue;

            MakeExecutable(candidate);
            return Path.GetFullPath(candidate);
        }

        return FindOnPath(ExecutableName);
    }

    private string SelectFormat(bool hasFfmpeg) =>
        string.IsNullOrWhiteSpace(_options.FormatSelector)
            ? DefaultFormatSelector(hasFfmpeg)
            : _options.FormatSelector.Trim();

    // ffmpeg: percorso configurato, FFMPEG_PATH, accanto a yt-dlp, PATH.
    // In produzione arriva dal layer aggiunto dall'immagine (deploy/site-ffmpeg.Dockerfile).
    private string? FindFfmpeg()
    {
        if (_ffmpegResolved)
            return _ffmpeg;

        _ffmpegResolved = true;

        string name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        if (!string.IsNullOrWhiteSpace(_options.FfmpegPath))
        {
            string configured = Path.IsPathRooted(_options.FfmpegPath)
                ? _options.FfmpegPath
                : Path.Combine(_environment.ContentRootPath, _options.FfmpegPath);
            if (IsUsable(configured))
                return _ffmpeg = Path.GetFullPath(configured);
        }

        string? fromEnvironment = Environment.GetEnvironmentVariable("FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(fromEnvironment) && IsUsable(fromEnvironment))
            return _ffmpeg = Path.GetFullPath(fromEnvironment);

        string besideYtDlp = Path.Combine(ToolsDirectory, name);
        if (IsUsable(besideYtDlp))
            return _ffmpeg = Path.GetFullPath(besideYtDlp);

        return _ffmpeg = FindOnPath(name);
    }

    private string? FindOnPath(string executableName)
    {
        string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (string directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(directory.Trim('"'), executableName);
                if (IsUsable(candidate))
                    return Path.GetFullPath(candidate);
            }
            catch (ArgumentException)
            {
                // Voce di PATH malformata: si passa alla successiva.
            }
        }

        return null;
    }

    private static bool IsUsable(string path)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<string> InstallAsync(CancellationToken ct)
    {
        OSPlatform? platform =
            RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? OSPlatform.Windows
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? OSPlatform.Linux
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? OSPlatform.OSX
            : null;

        string? asset = platform is { } os
            ? AssetName(os, RuntimeInformation.OSArchitecture)
            : null;

        if (asset is null)
            throw new YtDlpException(
                $"No yt-dlp build is published for {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}).");

        Directory.CreateDirectory(ToolsDirectory);

        string target = Path.Combine(ToolsDirectory, ExecutableName);
        string temp = target + ".download";

        _logger.LogInformation("Scarico yt-dlp da {Url}", LatestReleaseBase + asset);

        try
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(LatestReleaseBase + asset, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw new YtDlpException($"Could not download yt-dlp (HTTP {(int)response.StatusCode}).");

            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                await response.Content.CopyToAsync(file, ct);

            File.Move(temp, target, overwrite: true);
            MakeExecutable(target);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        _logger.LogInformation("yt-dlp installato in {Path}", target);
        return target;
    }

    // Su Linux il bit di esecuzione non arriva con un file scaricato.
    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Se non si può cambiare, il processo dirà da solo che non è eseguibile.
        }
    }

    private static IEnumerable<string> SplitArguments(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // yt-dlp vuole "200M", non un numero di byte.
    private static string FormatSize(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return (bytes / (1024L * 1024 * 1024)) + "G";
        if (bytes >= 1024L * 1024)
            return (bytes / (1024L * 1024)) + "M";
        if (bytes >= 1024)
            return (bytes / 1024) + "K";
        return bytes.ToString();
    }
}
