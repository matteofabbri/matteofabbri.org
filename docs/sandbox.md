# This is a blog. It is also a sandbox.

From the outside, matteofabbri.org is a personal blog: posts, tags, an RSS feed, a home
page that lists the most recent articles. All of that is true. It is also the least
interesting way to describe the site.

The better description is this: a sandbox that happens to have a public URL. Everything
I want to test — a request log, a bot classifier, a calendar integration, a self-hosted
analytics stack — runs here, on the real domain, against real traffic, next to the
writing. The blog is where the experiments are written down. The site is where they
happen.

## Why not a lab

A local lab is clean. It has fake data, a hostname nobody resolves, and no one hitting
the endpoints at 3 a.m. That is precisely the environment where the interesting failures
do not happen.

What happens here instead: TLS renewal, DNS, a reverse proxy, containers, volumes, a
deploy pipeline, real crawlers, real users, and the occasional scanner that fills a
public endpoint with garbage. Those are the conditions an experiment has to survive. A
feature that works on a laptop and falls over behind a proxy has not been tested; it has
been previewed.

## The rules

Five rules keep the sandbox from becoming a junk drawer.

1. **The experiment runs in production.** No demo branch, no staging copy with mock
   data. If a feature is not deployed, it is a draft, and drafts do not count.
2. **Every experiment has a delete path.** The request log can be switched off with one
   flag — no middleware, no database, no queue. The booking API answers 503 when no
   calendar is configured, and the site starts normally. Analytics is off when the
   website id is empty. Deleting an experiment must never mean rescuing the blog from it.
3. **The data has to be real.** `/requests` does not display sample rows. It displays
   what the middleware wrote, and the same list of rows feeds the map, the chart, the
   filters, the table and the CSV export. If the numbers disagree, the experiment is
   broken and gets fixed that day.
4. **Writing is the acceptance test.** A feature is finished when I can write the post:
   here is the problem, here is the decision, here is what it cost. If I cannot explain
   the trade-off, I do not understand the feature yet.
5. **No dependency I cannot keep.** Self-hosted beats SaaS; a file on a volume beats a
   managed database; one small server beats a cluster I would have to babysit. The site
   has to be maintainable by one person who also has other things to do.

Removal is a valid outcome. Several experiments have already ended; the posts describing
them remain, because a deleted feature is still a result.

## What is running now (November 2026)

**Publishing.** Posts are Markdown files on a Docker volume, not rows in a database. The
home page is the blog index and renders the first five posts in full; the rest are
summaries. Posts live at `/blog/{slug}`, tags at `/blog/tag/{tag}`, and the site
publishes an RSS feed plus two sitemaps, HTML and XML. The editor is a vendored Toast UI
instance that writes Markdown, so the content survives any future redesign of the site.

**Observation.** Every HTTP request — health checks aside — is written to an embedded
Sperone database in the same process, through a middleware, a bounded in-memory queue of
8,192 entries and a background writer that flushes every second or every 200 rows,
whichever comes first. The log also contains the requests that never reached an article:
static files, 404s, rejected logins. This is the instrument the rest of the sandbox
measures itself with.

**Bot classification.** A separate Python service labels each request with `IsBot`,
`IsMaliciousURL`, a probability and the model that produced it, over gRPC. It runs off
the HTTP path: if the model is slow or down, the request is still served and the
classification stays null. It never blocks anything. An admin verdict on a client is not
a row edit: it relabels every request from that client in the period and feeds the
model's training set, so the next classification reflects the human decision.

**The public dashboard.** `/requests` is the log made visible: a world map from an
offline GeoIP database, a visitors chart, a country table grouped by continent, filters
for country, IP, client id, user agent, bots and malicious URLs, and a CSV export with a
formula-injection guard. Every number on the page is computed from the same list of rows
as the table and the export.

**Signal from crawlers.** `/info-send` accepts whatever raw body a bot or an AI agent
decides to post, stores it, and `/info-bot` shows it back. It is an experiment in
watching what automated traffic says when it thinks nobody reads it. Rate-limited,
size-capped, public.

**The calendar.** `/booking` is a week view where a visitor drags a start and an end;
busy blocks come from a CalDAV calendar and a confirmed slot creates a tentative, private
event. It is parked while the calendar backend is replaced: the code is intact and
generic, and the API answers 503 without credentials.

**Documentation.** Two documentation areas, `/visualmcp` and `/predictive-coding`, are
rendered by the same application from two projects I work on. The site is not only the
subject of the writing; sometimes it is the delivery mechanism for someone else's docs.

**Analytics.** Umami is self-hosted under `/analytics` on the same domain, cookieless,
built into a custom image because its base path is fixed at build time. It is enabled
only when a website id is configured, and it does not run on `/admin`.

**A second application.** TheQult is another Razor Pages app in the same solution: a
hospitality community with members, requests and references, on its own embedded database
and volume. Same building, different house.

**Accounts.** `/admin` signs in against local accounts stored in SQLite, hashed with
PBKDF2. There is no external identity provider. Disabling an account or resetting a
password rotates the session stamp, which invalidates every cookie that account already
holds.

## What the blog is for

The posts are the lab notebook. They follow the same shape: problem, decision,
consequence, with real names and numbers read from the code. A post that announces a
feature is advertising; a post that records what broke, what it cost and what was removed
is documentation.

The blog is also the changelog. When an experiment ends, the post stays. When a feature
changes, a new post explains why. The archive is the only part of the sandbox that is not
allowed to disappear.

## What this site is not

It is not a portfolio. There is no grid of logos and no "hire me" section; the projects
are visible by using the site.

It is not a product. There are no signups, no tracking cookies, no growth metrics, no
newsletter popup.

It is not a news feed. Posts appear when an experiment ends, not on a schedule.

It is not permanent. Features can be removed, endpoints can return 503, pages can change.
The archive is the record; the code is allowed to move.

There is even a page listing the other Matteo Fabbris — a professor, a journalist, a
surfer — so that nobody credits me with their achievements. That is the level of
branding ambition involved.

## Under the floor

.NET 10 with Razor Pages for pages and minimal APIs for endpoints, an Aspire AppHost for
the local stack, Docker on a single OVH server, Caddy in front with layer4 and the PROXY
protocol so the application sees the real client address. SQLite for accounts, embedded
Sperone for the request log, Python over gRPC for the model, self-hosted Umami for
analytics, Markdown on volumes for the content. Two xUnit projects, 137 test methods,
because a sandbox with no tests is just a mess with a domain name.

The site is English only. The code is supposed to be, too; that migration is not
finished, and pretending otherwise would be exactly the kind of claim this document is
about not making.

The point is not to build a product. The point is to have a place where a question can
become a running system before the enthusiasm runs out. The blog is the index. The
sandbox is the content. If an experiment is worth keeping, it earns a post; if it is not,
it earns a deletion. Either way, the site gets more interesting to run.
