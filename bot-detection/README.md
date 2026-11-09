# Bot detection — the model labels the site's request log

Every request that reaches the site is already recorded in Sperone by
`RequestLogMiddleware`. This project adds one thing: before a batch is written,
the writer asks a Python model for two flags and stores them on the entry.

- `IsBot` — the request comes from an automated agent (crawler, scanner, script).
- `IsMaliciousURL` — the request targets a hostile path (`.env`, `.git`,
  WordPress shells, AI-agent config files, …).

There is **no gateway, no blocking and no message broker**. Nothing runs on the
HTTP request path: the site's latency is unchanged, and if the model is down the
log is written with the flags set to null.

## Flow

```text
request
  → RequestLogMiddleware            (existing: builds the entry, enqueues it)
  → RequestLogQueue                 (in-memory, bounded)
  → RequestLogWriter                (background, one batch per flush)
       ├─ ClientRateTracker         (60 s window: requests, distinct paths,
       │                             errors, auth failures, gap)
       ├─ BotClassifier → gRPC      (/botdetection.v1.BotDetectionService/ClassifyBatch)
       │      → Python: features → model → is_bot / is_malicious
       └─ IsBot, IsMaliciousURL, BotProbability, BotModel on the entry
  → Sperone → /requests (badge) and CSV export (two extra columns)
```

A batch is enriched exactly once. If the write to Sperone fails, the enriched
entries stay in `_pending` and are retried without asking the model again. If
the model times out (1 s per batch) or is unreachable, the entries are written
with null flags: the log is never lost because of the classifier.

## Semantics

The probability is P(automated/hostile) from the model. The two flags are:

- `is_malicious` = p ≥ 0.90, or a sensitive path with p ≥ 0.60;
- `is_bot` = `is_malicious`, or p ≥ 0.60, or a known crawler user-agent.

So a legitimate crawler (Semrush, MJ12, PetalBot, Googlebot) is
`IsBot=true, IsMaliciousURL=false`; a human is `false, false`; a scanner on
`/.env` is `true, true`.

## Why these labels, and what the log taught us

The sample export (`requests-2026-11-08`, 24,595 rows) has three properties that
shaped the pipeline:

1. **All 842 rows flagged `IsMaliciousURL` returned HTTP 200** — the site's
   soft-404 product decision. The status code cannot be a label; the path can.
2. **One client made 1,348 requests in 60 seconds** on `/requests`, and the
   site's `IsBot` flag (user-agent based) does not see it. The 60-second window
   is load-bearing.
3. **The scan is no longer only WordPress**: `/.claude/config.json`,
   `/.openai/config.json`, `/.opencode/auth.json`, `claude_desktop_config.json`,
   plus `.env`/`.git` with percent-encoded slashes (`/%2F.env`). The path
   classifier percent-decodes and knows the AI-credential surface.

Measured with the heuristic fallback on that sample:

| metric | value |
|---|---|
| `is_malicious` recall on the 842 log-malicious rows | **840 / 842 = 99.8%** |
| `is_bot` recall on the same rows | **841 / 842 = 99.9%** |
| requests flagged `is_bot` overall | **96.9%** — coherent with the stated 99% bot traffic; the log's UA-only `IsBot` is 33.7% and misses rate-based crawlers and PetalBot |
| `is_malicious` on the 23,753 rows the log calls benign | 13,799: 12,961 are the 1,348 rpm scraper, 571 hit known-sensitive paths. The log's label is incomplete, the detector is not |
| inference, feature vector + logistic score | p50 7.9 µs, p99 14.1 µs |

## Components

- `contracts/bot_detection.proto` — `Classify`, `ClassifyBatch`, `GetModelInfo`.
  One file compiled by `Grpc.Tools` and by `grpc_tools.protoc`.
- `ai/botdetection/` — the Python service:
  - `features.py`: the versioned feature schema (train == serve), percent
    decoding, path/UA rules, weak labels;
  - `model.py`: frozen `ModelBundle`, atomic save, schema validation, heuristic
    fallback, the two-flag decision;
  - `predictor.py` + `hot_reload.py`: lock-free reads, atomic swap, watcher;
  - `storage.py`: SQLite dataset (WAL, idempotent by request_id), training runs;
  - `service.py`: batch RPC + observation recording + Prometheus metrics;
  - `retrain.py`: HistGradientBoosting with GroupKFold by client_id, promotion
    gate, CLI;
  - `server.py`: composition root.
- `src/BotDetection.Client/` — the gRPC client, options and the 60-second rate
  tracker. Referenced by MainSite.
- `MainSite/RequestLogging/` — `BotEnricher`, the writer hook, the four new
  entry fields; `Pages/Requests.*` — badge, CSV columns.

## Retraining

Each classified batch is appended to `/data/observations.sqlite3` with a
**weak label** derived from deterministic rules (`derive_weak_label`): the model
is never trained on its own predictions. The scheduler retrains once
`RETRAIN_MIN_NEW_SAMPLES` new rows exist, validates with GroupKFold by
`client_id` (a random split leaks the same client), and promotes only if the
candidate F1 beats `max(current_f1 - tolerance, 0.50)`. The writer writes
`bot_model.joblib.tmp` + `os.replace`; the watcher in the serving process reloads
it. No restart, no downtime.

Run it manually:

```bash
python -m botdetection.retrain --db /data/observations.sqlite3 --out /models/bot_model.joblib
```

## Backfilling the flags already in Sperone

Rows written before this feature have no flags. A one-shot backfill rewrites
them in place:

1. set `RequestLog:BackfillBotFlags=true` (env: `RequestLog__BackfillBotFlags`)
   for the site and restart it;
2. `RequestLogBackfill` reads the log one day at a time, in chronological
   order, so the 60-second window still means something for historical rows;
3. for the rows without flags it calls `ClassifyBatch` and then Sperone's
   `UpdateElementCommand`: the ItemId, the order and the indexes stay the same;
4. the TTL is re-scheduled to the original expiry, so the backfill does not
   extend the retention;
5. rows that already carry both flags are skipped. The job is resumable and
   harmless if left enabled (`BackfillForce=true` re-classifies everything);
6. if the model is unavailable, the row is left untouched and stays a candidate
   for a later run.

`BackfillDays` (default 90) sets the window. Progress per day and a final
summary are logged:

```text
Bot flags backfill: 2026-11-08 done (24595 scanned, 21403 updated)
Bot flags backfill finished: 740000 scanned, 640000 updated, 100000 skipped
```

The work runs inside the site process because Sperone is embedded: one owner of
the database, no second instance against the same volume.

## Aspire

The model is one container in the site's existing app model
(`MatteoFabbri.org.AppHost/AppHost.cs`):

```csharp
var botAi = builder.AddDockerfile("bot-ai", "../bot-detection", "ai/Dockerfile")
    .WithHttpEndpoint(targetPort: 50051, name: "grpc", isProxied: false)
    .WithHttpEndpoint(targetPort: 8081, name: "http")
    .WithHttpHealthCheck("/healthz", endpointName: "http")
    .AsHttp2Service()
    .WithVolume("botdetection-models", "/models")
    .WithVolume("botdetection-data", "/data")
    .WithEnvironment("MODEL_PATH", "/models/bot_model.joblib")
    .WithEnvironment("DATASET_PATH", "/data/observations.sqlite3");

site.WithEnvironment("BotDetection__GrpcUrl", botAi.GetEndpoint("grpc"));
```

No `WaitFor`: the site must start even when the AI is not ready. In Compose the
endpoint resolves to `bot-ai:50051`; in development it is the allocated local
port. `botdetection-models` holds the model file, `botdetection-data` the SQLite
dataset.

## Run

```bash
# unit tests (15)
cd bot-detection
PYTHONPATH=ai python -m unittest discover -s ai/tests -v

# the whole app model (sites, Caddy, the Python model)
dotnet run --project ../MatteoFabbri.org.AppHost
```

Useful endpoints: `GET /requests` (the new Bot column), the CSV button on the
same page, `GET /healthz` and `GET /metrics` on the Python service.

## Note on visibility

`/requests` is public: the badge tells a scanner what the model noticed. It is
fine for a portfolio, but if it becomes a problem move the column (and the two
CSV fields) behind the admin area.

## Verified / not verified

- ✅ `dotnet build MatteoFabbri.org.slnx` — 0 errors; the only warning is the
  pre-existing `ASPIRE010` on the site's AppHost.
- ✅ 15 Python unit tests.
- ✅ Heuristic scored against the 24,595-row real log (table above).
- ⚠️ Not run in this environment: the Python container and the AppHost model.
