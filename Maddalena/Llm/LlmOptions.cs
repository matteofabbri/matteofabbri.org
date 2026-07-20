using static System.Net.WebRequestMethods;

namespace Maddalena.Llm;

// I parametri dell'LLM di predictive coding, sezione di configurazione "Llm".
// Maddalena non ha appsettings.json: i default stanno QUI e in produzione si
// sovrascrivono con le variabili d'ambiente Llm__* (es. Llm__Epochs=4,
// Llm__WikipediaTitles__0=...). Le dimensioni del modello contano solo alla
// PRIMA creazione: da li' in poi comanda lo snapshot salvato su Postgres.
public sealed class LlmOptions
{
    /// <summary>
    /// Cartella dove il modello si salva e riprende. Vuota = ContentRoot/llm-state
    /// (dev). In produzione l'AppHost la punta al volume docker: /state/llm.
    /// </summary>
    public string? StateDir { get; set; }

    // ── Dimensioni del modello (solo prima creazione; preset CreatePc denso RMSNorm+RoPE, byte tokenizer) ──

    public int Dim { get; set; } = 64;
    public int Blocks { get; set; } = 4;
    public int Heads { get; set; } = 4;
    public int FfDim { get; set; } = 128;

    /// <summary>
    /// La finestra di contesto, in token (= byte col tokenizer di default).
    /// Vale alla CREAZIONE del modello: uno stato salvato conserva la sua
    /// finestra finche' non si riparte da zero (cancellando la cartella di
    /// stato). Il costo dell'attenzione cresce col QUADRATO della finestra e la
    /// generazione rifa' il forward a ogni token: 1024 e' il compromesso sano
    /// su CPU; megabyte di contesto qui non esistono.
    /// </summary>
    public int MaxSeq { get; set; } = 1024;

    public int Seed { get; set; } = 42;

    /// <summary>Ancora Adam nel training PC: ogni quanti step PC un burst di backprop che "rimette in riga" (0 = spenta).</summary>
    public int PcAnchorEvery { get; set; } = 0;

    /// <summary>Passi di Adam per scatto d'ancora.</summary>
    public int PcAnchorBurst { get; set; } = 3;

    /// <summary>iPC: learning-rate del rilassamento dei latenti (prudente = stabile).</summary>
    public float PcEtaState { get; set; } = 0.1f;

    /// <summary>iPC: learning-rate degli update dei pesi.</summary>
    public float PcLrWeights { get; set; } = 0.3f;

    /// <summary>iPC: tetto alla norma degli update — guardia dura contro il NaN.</summary>
    public float PcClipNorm { get; set; } = 1.0f;

    /// <summary>iPC: passi di rilassamento del latente per weight-update (1 = iPC).</summary>
    public int PcRelaxSteps { get; set; } = 1;

    // ── Training di avvio ──

    public int Epochs { get; set; } = 2;

    /// <summary>Tetto alle finestre di training per tenere prevedibile l'avvio (null = tutto il corpus).</summary>
    public int? MaxWindows { get; set; } = 1500;

    // ── Istruzione online (/api/llm/teach) ──

    /// <summary>Passi di discesa per ogni testo insegnato.</summary>
    public int TeachSteps { get; set; } = 2;

    public float TeachLearningRate { get; set; } = 0.001f;

    // ── Crawler (Abot): il web come corpus infinito ──

    public bool CrawlerEnabled { get; set; } = true;

    /// <summary>
    /// I punti di partenza del crawl: qualche sito leggero e ricco di testo,
    /// piu' un paio di quotidiani per ogni paese europeo (micro-stati esclusi).
    /// Robots.txt e paywall decidono poi quanto ne esce davvero, sito per sito.
    /// </summary>
    public string[] CrawlerSeeds { get; set; } =
    [
        // Testuali e leggeri
        "https://text.npr.org/",
        "https://lite.cnn.com/",
        "https://news.ycombinator.com/newest",
        "https://en.wikipedia.org/wiki/Predictive_coding",
        "https://www.wattpad.com/",

        //Boards
        "https://tgstat.ru/en",
        "https://8kun.top/index.html",
        "https://www.diochan.com/b/",
        "https://4chan.org/",
        "https://endchan.net/",
        "https://kohlchan.net/",
        "https://alogs.space/",
        "https://lainchan.org/",
        "https://7chan.org/",
        "https://iichan.hk/",
        "https://uboachan.net/",
        "https://smuglo.li/",
        "https://ponychan.net/",
        "https://mlpol.net/",
        "https://leftypol.org/",
        "https://tvch.moe/",
        "https://kissu.moe/",
        "https://dangeru.us/",

        //Pornazzi
        "https://it.pornhub.com/",
        "https://efukt.com/",
        "https://xraws.com/",
        "https://pornkai.com/",
        "https://www.existenz.se/",
        "https://xhamster.com/",
        "https://91torture.com/18+",
        "https://darknessporn.com/",
        "https://motherless.com/",
        "https://www.crazyshit.com/",
        "https://fetishmania.org/home.html",
        "https://nothingtoxic.com/",
        "https://www.daftporn.com/",
        "https://www.heavy-r.com/",
        "https://darknessporn.com/",
        "https://www.pervertium.com/",
        "https://www.torturetube.com/",
        "https://shooshtime.com/",
        "https://www.wtfpeople.com/",
        "https://sickjunk.com/",
        "https://crazyshit.com/",
        "https://www.pornhd.com/",

        // Italia
        "https://www.repubblica.it/",
        "https://www.corriere.it/",
        // Francia
        "https://www.lemonde.fr/",
        "https://www.lefigaro.fr/",
        // Germania
        "https://www.faz.net/",
        "https://www.sueddeutsche.de/",
        // Spagna
        "https://elpais.com/",
        "https://www.elmundo.es/",
        // Portogallo
        "https://www.publico.pt/",
        "https://expresso.pt/",
        // Regno Unito
        "https://www.theguardian.com/",
        "https://www.independent.co.uk/",
        // Irlanda
        "https://www.irishtimes.com/",
        "https://www.independent.ie/",
        // Paesi Bassi
        "https://www.nrc.nl/",
        "https://www.volkskrant.nl/",
        // Belgio
        "https://www.lesoir.be/",
        "https://www.standaard.be/",
        // Lussemburgo
        "https://www.wort.lu/",
        "https://www.tageblatt.lu/",
        // Austria
        "https://www.derstandard.at/",
        "https://www.diepresse.com/",
        // Svizzera
        "https://www.nzz.ch/",
        "https://www.letemps.ch/",
        // Danimarca
        "https://politiken.dk/",
        "https://www.berlingske.dk/",
        // Svezia
        "https://www.dn.se/",
        "https://www.svd.se/",
        // Norvegia
        "https://www.aftenposten.no/",
        "https://www.vg.no/",
        // Finlandia
        "https://www.hs.fi/",
        "https://www.iltalehti.fi/",
        // Islanda
        "https://www.mbl.is/",
        "https://www.visir.is/",
        // Polonia
        "https://wyborcza.pl/",
        "https://www.rp.pl/",
        // Cechia
        "https://www.idnes.cz/",
        "https://www.lidovky.cz/",
        // Slovacchia
        "https://www.sme.sk/",
        "https://www.pravda.sk/",
        // Ungheria
        "https://index.hu/",
        "https://hvg.hu/",
        // Slovenia
        "https://www.delo.si/",
        "https://www.dnevnik.si/",
        // Croazia
        "https://www.jutarnji.hr/",
        "https://www.vecernji.hr/",
        // Romania
        "https://adevarul.ro/",
        "https://www.libertatea.ro/",
        // Bulgaria
        "https://www.dnevnik.bg/",
        "https://www.24chasa.bg/",
        // Grecia
        "https://www.kathimerini.gr/",
        "https://www.tanea.gr/",
        // Cipro
        "https://politis.com.cy/",
        "https://www.philenews.com/",
        // Malta
        "https://timesofmalta.com/",
        "https://www.independent.com.mt/",
        // Estonia
        "https://www.postimees.ee/",
        "https://www.delfi.ee/",
        // Lettonia
        "https://www.delfi.lv/",
        "https://www.diena.lv/",
        // Lituania
        "https://www.delfi.lt/",
        "https://www.lrytas.lt/",
        // Serbia
        "https://www.politika.rs/",
        "https://www.danas.rs/",
        // Bosnia ed Erzegovina
        "https://www.klix.ba/",
        "https://avaz.ba/",
        // Albania
        "https://shqiptarja.com/",
        "https://www.panorama.com.al/",
        // Macedonia del Nord
        "https://www.slobodenpecat.mk/",
        "https://sdk.mk/",
        // Montenegro
        "https://www.vijesti.me/",
        "https://www.pobjeda.me/",
        // Kosovo
        "https://www.koha.net/",
        "https://telegrafi.com/",
        // Ucraina
        "https://www.pravda.com.ua/",
        "https://kyivindependent.com/",
        // Moldova
        "https://www.zdg.md/",
        "https://unimedia.info/",
    ];

    /// <summary>Thread concorrenti del crawler (pochi: siamo ospiti dei siti altrui).</summary>
    public int CrawlThreads { get; set; } = 2;

    /// <summary>Pausa minima tra due richieste allo stesso dominio.</summary>
    public int CrawlDelayMs { get; set; } = 2000;

    /// <summary>Quanti salti di link dai seed (il tempo e' infinito, la profondita' no).</summary>
    public int CrawlMaxDepth { get; set; } = 500_000_000;

    public int CrawlMaxLinksPerPage { get; set; } = 3000;

    public int CrawlMaxPageBytes { get; set; } = 1_000_000;

    public string CrawlUserAgent { get; set; } = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36 Edg/150.0.0.0";

    /// <summary>Passi di discesa per ogni pagina assorbita.</summary>
    public int CrawlLearnSteps { get; set; } = 1;

    /// <summary>Sotto questa lunghezza il testo estratto non insegna niente (menu, pagine vuote).</summary>
    public int CrawlLearnMinChars { get; set; } = 200;

    /// <summary>Tetto al testo per pagina dato in pasto a Learn (tiene corta l'attesa sotto il semaforo).</summary>
    public int CrawlLearnMaxChars { get; set; } = 32 * 1024 * 1024;

    // ── Salvataggio periodico ──

    /// <summary>Ogni quanti minuti il timer scrive su disco lo stato, se e' cambiato.</summary>
    public int SaveEveryMinutes { get; set; } = 5;
}
