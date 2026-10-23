namespace MainSite.Blog;

// Un articolo come sta su disco: il Markdown grezzo più i metadati del front
// matter. Slug e nome del file coincidono: il file è la fonte di verità, così
// non si può avere un articolo con due indirizzi diversi.
public sealed record BlogPost
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public string Markdown { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public DateTimeOffset Published { get; init; }
    public DateTimeOffset Updated { get; init; }
    public bool Draft { get; init; }
    public string Author { get; init; } = "";

    // Un articolo è pubblico quando non è una bozza ed è già arrivato il suo
    // momento: le date future servono a programmare la pubblicazione.
    public bool IsVisibleAt(DateTimeOffset now) => !Draft && Published <= now;
}

// Quello che l'editor passa allo store per creare o aggiornare un articolo.
public sealed record BlogPostInput
{
    public required string Slug { get; init; }
    public required string Title { get; init; }
    public string Summary { get; init; } = "";
    public required string Markdown { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public required DateTimeOffset Published { get; init; }
    public bool Draft { get; init; }
    public required string Author { get; init; }
}

// Errore di validazione mostrabile all'utente (non è un guasto tecnico).
public sealed class BlogValidationException(string message) : Exception(message);

// Un file caricato nella cartella media del blog.
public sealed record BlogMediaFile(string FileName, string Url, long Size, DateTimeOffset Modified)
{
    public string SizeLabel => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.#} KB",
        _ => $"{Size / (1024.0 * 1024.0):0.#} MB",
    };
}
