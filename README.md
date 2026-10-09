# Supplier Feed Throttle & Dedup Service

ASP.NET Core 8+ Web API + SQLite + Dapper. Dedupes supplier reservation updates and throttles suppliers (>100 requests / rolling 60s) across multiple instances.

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
- **Exact limit, no slack.** Request 101 in any 60 seconds is never admitted.
- **The database is the single source of truth** for the request count and the clock. Instances keep no counters or clocks of their own, so the limit stays exact across them.
- **A reservation is identified by supplier and reservation ID, and `updatedAtUtc` is its version.** Suppliers may reuse IDs, and a delayed old retry must never overwrite newer data.
- **Every request counts toward the limit, even invalid ones.** They still cost work, and a supplier sending garbage should be limited too. Rejected requests don't count.
- **One transaction per request**, so the count, the data and the stats commit together and a crash can't leave them disagreeing.
- **Responses and stats use the same four words**, so what a supplier is told matches what we count:
  - **ingested**: a new or changed reservation was saved (201 / 200).
  - **ignored**: nothing was saved, because it was a duplicate or an out-of-date update (200).
  - **invalid**: the request was rejected because its data was bad (400).
  - **throttled**: the supplier is over its limit (429, with the exact time to retry).

## Where I disagreed with / changed Claude Code's suggestions
- **Clock.** Claude proposed each instance using its own clock and tolerating a little skew. I chose the database as the only clock, because the limit has to be exact.
- **Cleanup.** Claude proposed deleting old rows on every request, then a cleanup job in every instance. I rejected both (extra writes, redundant work) and wanted one external cleaner; SQLite has no expiry or scheduler, so the in-service job stays as a compromise.
- **Retry time.** A throttled supplier could be told to retry after 0 seconds, which invites an immediate retry. I flagged it as bad UX, and the retry time is now exact and never zero.
- **Stats.** Claude counted duplicates and out-of-date updates separately. I merged them into one "ignored" counter, added an "invalid" counter, and made responses use the same words as the stats.

## What I'd do differently with more time
- **Keep the request window in a cache such as Redis, not in the database.** It keeps the per-request throttle writes off the database, and every instance shares one clock.
- **Remove the cleanup job**, because the cache expires old entries by itself.
- **Per-supplier authentication and per-IP limits**, so one caller can't use up another supplier's quota and requests that don't name a supplier are limited too.
- **Replace SQLite with a server database.** SQLite allows only one writer at a time and can't be shared across machines, which caps how far the service can scale. I kept it because the assignment fixes the stack.
