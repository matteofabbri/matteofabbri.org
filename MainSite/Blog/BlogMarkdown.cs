using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Microsoft.Extensions.Options;

namespace MainSite.Blog;

// Rendering e utilità di testo del blog. Un solo punto in cui si decide come
// il Markdown diventa HTML: così l'anteprima, gli articoli e il feed non possono
// divergere. L'HTML grezzo è disattivato: il Markdown lo scrivono gli autori,
// ma un account compromesso non deve poter iniettare script nel sito.
public static class BlogMarkdown
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    private static readonly Regex ValidSlug = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex WordPattern = new(@"[\p{L}\p{N}]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static string ToHtml(string markdown) => Markdown.ToHtml(markdown ?? "", Pipeline);

    public static string ToPlainText(string markdown) => Markdown.ToPlainText(markdown ?? "", Pipeline);

    public static bool IsValidSlug(string? slug) => slug is not null && ValidSlug.IsMatch(slug);

    // Riepilogo di riserva quando l'autore non ne scrive uno: le prime parole
    // del testo, senza markup, tagliate a un confine di parola.
    public static string Excerpt(string markdown, int maxLength = 200)
    {
        var text = Spaces.Replace(ToPlainText(markdown), " ").Trim();
        if (text.Length <= maxLength)
            return text;

        int cut = text.LastIndexOf(' ', Math.Min(maxLength, text.Length - 1));
        if (cut < maxLength / 2)
            cut = maxLength;

        return text[..cut].TrimEnd(' ', ',', ';', ':', '.', '!', '?') + "…";
    }

    // Minuti di lettura a 200 parole al minuto, mai zero per un articolo che
    // ha almeno una parola.
    public static int ReadingMinutes(string markdown)
    {
        int words = WordPattern.Matches(ToPlainText(markdown)).Count;
        return words == 0 ? 0 : Math.Max(1, (int)Math.Ceiling(words / 200.0));
    }

    // Da titolo a slug: "Perché scrivo di matematica" → "perche-scrivo-di-matematica".
    // Gli accenti si sciolgono con la normalizzazione Unicode, tutto il resto
    // diventa un trattino. Solo ASCII: gli slug finiscono nel nome del file e
    // nell'URL, e vogliamo che siano identici ovunque.
    public static string Slugify(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "post";

        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (char ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (ch is >= 'a' and <= 'z' or >= '0' and <= '9')
                builder.Append(ch);
            else if (ch is >= 'A' and <= 'Z')
                builder.Append(char.ToLowerInvariant(ch));
            else
                builder.Append('-');
        }

        var slug = Regex.Replace(builder.ToString(), "-{2,}", "-").Trim('-');

        // Il limite tiene i nomi dei file corti anche con titoli lunghissimi.
        if (slug.Length > 80)
            slug = slug[..80].TrimEnd('-');

        return slug.Length == 0 ? "post" : slug;
    }
}

// Le date del blog si leggono e si scrivono sempre nel fuso configurato
// (Europe/Rome di default): il container gira in UTC e un datetime-local senza
// fuso sposterebbe la pubblicazione di un'ora o due.
public sealed class BlogClock
{
    private readonly ILogger<BlogClock> _logger;

    public BlogClock(IOptions<BlogOptions> options, ILogger<BlogClock> logger)
    {
        _logger = logger;
        try
        {
            Zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogWarning(ex, "Fuso {Zone} non trovato: il blog userà UTC.", options.Value.TimeZone);
            Zone = TimeZoneInfo.Utc;
        }
    }

    public TimeZoneInfo Zone { get; }

    // Un orario locale (quello del campo datetime-local) diventa un istante con
    // l'offset giusto per quel giorno, ora legale inclusa.
    public DateTimeOffset ToInstant(DateTime local) => new(local, Zone.GetUtcOffset(local));

    public DateTime ToLocal(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone).DateTime;

    public string Format(DateTimeOffset instant, string format = "d MMMM yyyy") =>
        ToLocal(instant).ToString(format, CultureInfo.GetCultureInfo("it-IT"));
}
