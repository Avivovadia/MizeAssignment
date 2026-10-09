# Supplier Feed Throttle & Dedup Service

ASP.NET Core 8+ Web API + SQLite + Dapper. Dedupes supplier reservation updates and throttles suppliers (>100 requests / rolling 60s) across multiple instances. Full details: `plan.md`.

## Run it

Requires the .NET 8 SDK or later. From the repo root:

```bash
dotnet build
dotnet test                                    # whole suite, about a minute and a half (it includes real contention tests)
dotnet run --project src/SupplierFeed.Api      # listens on http://localhost:5227, creates supplierfeed.db in the working directory
```

Try it (the database file is created on first start):

```bash
curl -i -X POST http://localhost:5227/api/reservations/ingest -H 'Content-Type: application/json' \
  -d '{"supplierId":"acme","reservationId":"r1","roomId":"room-1","checkIn":"2026-08-01T00:00:00Z","checkOut":"2026-08-03T00:00:00Z","price":450.00,"updatedAtUtc":"2026-07-01T10:15:00Z"}'
curl http://localhost:5227/api/reservations/stats/acme
```

## Key decisions / assumptions
- **Exact limit, no slack:** request 101 in any rolling 60s is never admitted. The cost is DB writes per request, a deliberate trade of performance for accuracy.
- **The database is the single source of truth** for the window state and the clock (one atomic check-and-insert). On SQLite this only holds on one host: SQLite's "now" is the app process's OS clock. Multi-instance behavior is tested with real separate processes sharing one file on one machine; different-machine behavior is out of scope because SQLite cannot span machines.
- **Identity** is `(supplierId, reservationId)`; `updatedAtUtc` is the version. An older version with different details is ignored as `outofdate`.
- **Throttle runs before validation**; every attributable request counts, throttled ones don't occupy the window.
- **One transaction per request**, owned by the orchestrator: the throttle slot, reservation write and stats counter commit together, so stats can't drift after a crash. The apply step runs in a savepoint so a failing request still consumes its quota slot.
- **Responses and stats share one vocabulary:** `ingested` (created/updated), `ignored` (duplicate/outofdate), `invalid` (400), `throttled` (429 + `Retry-After`). `invalid` counts every bad payload even when the request is also throttled (so a supplier sending garbage stays visible during a flood); invalid requests count toward the limit but never touch `reservations`.

## Where I disagreed with / changed Claude Code's suggestions
- **Clock.** Claude proposed each instance using its own clock and tolerating a little skew. I chose the database as the only clock, because the limit has to be exact.
- **Cleanup.** Claude proposed deleting old rows on every request, then a cleanup job in every instance. I rejected both (extra writes, redundant work) and wanted one external cleaner; SQLite has no expiry or scheduler, so the in-service job stays as a compromise.
- **Retry time.** Claude rounded it to whole seconds and could return 0 to a throttled caller, which invites an instant retry. I flagged the bad UX; fixing it exposed a race, so the time is now exact and always above zero (only the HTTP header rounds up, as HTTP requires).
- **Stats.** Claude kept duplicates and out-of-date updates as separate counters. I merged them into one "ignored" bucket, made responses use the same words as the stats, and added an "invalid" counter (a throttled invalid request counts in both, for visibility).

## What I'd do differently with more time
- **Redis (or similar) as the window store** instead of the DB: lower latency, atomic sliding window, native expiry, one shared clock, no load on the reservations DB. Not used because the assignment fixes the stack.
- **Replace the in-service cleanup job with one external cleaner** (cron job or DB-side scheduler), so cleanup runs once rather than once per instance. With Redis this disappears entirely (native TTL).
- **Scale writes with a dedicated writer service fed by a message broker.** SQLite allows one writer, so the service that writes would be a single, non-scaled consumer while the API tier scales; this moves the throttle verdict off the request path, so it needs a decision (request-reply, or 202 and report throttling another way). Out of scope here.
- **Bound concurrent writers per instance.** Measured: SQLite has one writer, so throughput falls from ~355 req/s with 1 caller to ~54 with 32 (p99 over 5s), and a second server process adds nothing. A queue in front of the DB held ~375 req/s at 32 callers (6x) in an experiment; not implemented. Details in `plan.md`.
- **Move the stats counters out of the request transaction on a server DB:** write a cheap event row in the same transaction (still exact after a crash) and aggregate the per-supplier counters asynchronously, so a hot supplier's counter row isn't updated on every request. On SQLite there is a single writer, so splitting would add a second lock acquisition and allow crash drift for no gain.
- A server DB (e.g. SQL Server) with proper locking, per-supplier auth, per-IP limits, windowed stats.
