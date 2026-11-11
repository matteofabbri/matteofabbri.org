namespace MainSite.Blog;

// The built-in copy of /about. It seeds pages/about.md on a new volume; once the
// file exists, the admin editor owns the content and this copy is only a fallback.
public static class AboutPage
{
    public const string Slug = "about";

    public const string DefaultTitle = "Not a blog, not even a sandbox";

    public const string DefaultDescription =
        "It looks like a personal blog, and calling it a sandbox would be flattering: " +
        "a sandbox protects you from consequences. This one runs on the real domain, " +
        "against real traffic, with no staging copy in between.";

    public const string DefaultMarkdown = """
    The site is where I test things before I know whether they are good ideas: a request
    log, a bot classifier, a calendar integration, analytics, a second community site. All of
    it runs on the real domain, inside the same deployment, next to the writing. The blog is
    where I write down what happened.

    And **sandbox** is not the right word either. A sandbox is a place where nothing you do
    can escape. This one has a public URL, real certificates, real visitors and no staging
    copy in between. It is a production test bench that also publishes articles.

    ## Why not a lab

    A local lab is clean: fake data, a hostname nobody resolves, no crawler hitting the
    endpoints at 3 a.m. That is exactly where the interesting failures do not happen.

    What happens here instead is DNS, certificate renewal, a reverse proxy, containers,
    volumes, a deploy pipeline and the occasional scanner that fills a public endpoint with
    garbage. A feature that works on a laptop and falls over behind a proxy has been
    previewed, not tested.

    ## The rules

    Five rules keep it from becoming a junk drawer.

    1. **The experiment runs in production.** No demo branch, no staging copy with mock
       data. If a feature is not deployed, it is a draft.
    2. **Every experiment has a delete path.** The request log turns off with one flag: no
       middleware, no database, no queue. The booking API answers 503 when no calendar is
       configured, and the site starts normally. Analytics is off when the website id is
       empty. Removing an experiment must never mean rescuing the blog from it.
    3. **The data has to be real.** `/requests` does not show sample rows. It shows what the
       middleware wrote, and the same rows feed the map, the chart, the filters, the table and
       the CSV. If the numbers disagree, the experiment is broken and gets fixed that day.
    4. **Writing is the acceptance test.** A feature is finished when I can write the post:
       the problem, the decision, what it cost. If I cannot explain the trade-off, I do not
       understand the feature yet.
    5. **No dependency I cannot keep.** Self-hosted beats SaaS. A file on a volume beats a
       managed database. One small server beats a cluster I would have to babysit.

    Removal is a valid outcome. When an experiment ends, the post stays.

    ## One AppHost, the whole stack

    ![Diagram of the Aspire AppHost: Caddy in front of the site, theqult, maddalena and umami; bot-ai connected to the site over gRPC; named volumes for content, accounts, logs and models.](/img/aspire-apphost.png)

    *The Aspire AppHost: one graph for local development and for the compose file on the server.*

    Everything the site needs is declared in one Aspire AppHost: the projects, the containers,
    the endpoints and the volumes. In development `aspire run` starts the graph on this
    machine; `aspire publish` turns the same graph into the docker compose file used on the
    server. There is no second description of the architecture to keep in sync.

    The resources, briefly:

    | Resource | What it is |
    |---|---|
    | `caddy` | The only published ports (80/443). TLS, layer4 and the PROXY protocol. |
    | `site` | This application: blog, `/requests`, `/admin`, `/info-bot`, the two docs areas. |
    | `theqult` | A second Razor Pages app: members, stay requests, references. |
    | `maddalena` | A placeholder service; domain and TLS are already wired. |
    | `umami`, `umami-db` | Cookieless analytics under `/analytics`, with PostgreSQL. |
    | `bot-ai` | A Python service that labels request batches over gRPC. |

    Named volumes keep state outside the images: `blog-data`, `auth-data`,
    `request-log-data`, `theqult-data`, `umami-db-data`, `botdetection-models` and
    `botdetection-data`; Caddy keeps its certificates in `caddy-data` and `caddy-config`.
    Rebuilding a container does not take the posts, the accounts or the log with it. That is
    the property that makes it safe to experiment in production.

    ## One request, two records

    ![Diagram of a request: Caddy forwards to the site, the middleware enqueues an entry, a background writer stores it in Sperone, and /requests reads it. Separately, the browser sends a page view to Umami and PostgreSQL. The two records share no key.](/img/request-path.png)

    *One visit produces a server-side row in Sperone and an unrelated page view in Umami.*

    The path of a request is short and boring on purpose. Caddy terminates TLS and forwards
    to the site with the PROXY protocol, so the application sees the real client address
    instead of the proxy. The request-log middleware enqueues an entry and returns; the
    response does not wait for a database. A background writer flushes the queue every second
    or every 200 rows into Sperone, the embedded store that runs in the same process. The
    queue holds 8,192 entries and drops new ones when it is full, because losing a log line is
    better than slowing the site.

    The bot classifier is off that path too. The writer sends batches to `bot-ai` over gRPC
    and stores `IsBot`, `IsMaliciousURL`, the probability and the model name. If the model is
    down, the flags stay null and the log is written anyway. Nothing is blocked: the model
    labels, it does not decide who gets in.

    A visitor also produces a second, unrelated record. The Umami tracker calls
    `/analytics/api/send`, and Umami writes a page view to PostgreSQL. Sperone answers "what
    hit the server"; Umami answers "what the browser reported". There is no shared key between
    the two, and there will not be one: joining them would require an identifier that survives
    the request, which is exactly what a cookieless setup avoids.

    ## What is running now

    - **The blog.** Markdown files on a volume, no database. The home page renders the first
      five posts in full; the rest are summaries. Posts at `/blog/{slug}`, tags, RSS, and two
      sitemaps (HTML and XML). The editor is a vendored Toast UI instance that writes
      Markdown, so the content outlives the UI.
    - **`/requests`.** The request log made public: world map from an offline GeoIP database,
      visitors chart, country table grouped by continent, filters by country, IP, client id,
      user agent, bot and malicious URL, CSV export.
    - **`/info-send` and `/info-bot`.** A public endpoint stores whatever raw body a bot
      posts; a public page shows it back. An experiment in watching automated traffic talk
      when it thinks nobody reads it.
    - **`/admin`.** Local accounts in SQLite, PBKDF2, no external identity provider.
    - **`/visualmcp` and `/predictive-coding`.** Two documentation areas rendered by the same
      app from two projects I work on.
    - **`theqult.org`.** The hospitality community, a second application in the same solution.
    - **`/booking`.** A week view over CalDAV, currently parked: without a calendar backend
      the API answers 503 and the rest of the site does not care.

    ## What it is not

    It is not a portfolio: there is no grid of logos, and the projects are visible by using the
    site.

    It is not a product: no signups, no tracking cookies, no growth metrics, no newsletter
    popup.

    It is not a news feed: posts appear when an experiment ends, not on a schedule.

    It is not permanent: features can be removed, endpoints can return 503, pages can change.
    The archive is the record; the code is allowed to move.

    There is even a [page listing the other Matteo Fabbris](/who-i-am-not), so nobody credits
    me with their achievements. That is the level of branding ambition involved.

    ## Why write it down

    Because a running system is not an explanation. The post is where a decision becomes
    legible: what I tried, what broke, what I removed. It is also the only changelog that
    survives a rewrite of the code.

    If an experiment is worth keeping, it earns a post. If it is not, it earns a deletion.
    Either way, the site gets more interesting to run.
    """;
}
