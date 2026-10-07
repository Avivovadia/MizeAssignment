# Supplier Feed Throttle & Dedup Service

ASP.NET Core 8+ Web API + SQLite + Dapper. Dedupes supplier reservation updates and throttles suppliers (>100 requests / rolling 60s) across multiple instances. Full details: `plan.md`.

> Status: planning complete, implementation not started.

## Key decisions / assumptions
- **Exact limit, no slack:** request 101 in any rolling 60s is never admitted. The cost is DB writes per request, a deliberate trade of performance for accuracy.
- **The database is the single source of truth** for the window state and the clock (one atomic check-and-insert). On SQLite this only holds on one host: SQLite's "now" is the app process's OS clock.
- **Identity** is `(supplierId, reservationId)`; `updatedAtUtc` is the version. An older version with different details is ignored as `outofdate`.
- **Throttle runs before validation**; every attributable request counts, throttled ones don't occupy the window.
- **One transaction per request**, owned by the orchestrator: the throttle slot, reservation write and stats counter commit together, so stats can't drift after a crash. The apply step runs in a savepoint so a failing request still consumes its quota slot.
- **Responses and stats share one vocabulary:** `ingested` (created/updated), `ignored` (duplicate/outofdate), `invalid` (400), `throttled` (429 + `Retry-After`). The counters add up to a supplier's total traffic; invalid requests count toward the limit but never touch `reservations`.

## Where I disagreed with / changed Claude Code's suggestions
- Claude proposed per-instance clocks with accepted skew. Rejected: it breaks the single-source-of-truth and exact-limit assumptions. Switched to DB-stamped time.
- Claude planned separate transactions for the throttle admit and the reservation write, as a default it hadn't stress-tested. I steered to one transaction per request after weighing the trade-offs: it gives consistent stats and one lock acquisition, at the cost of coupling the throttle to the DB transaction (a Redis window store would have to be split back out). A plain single transaction would have let persistently failing requests skip the limit; the savepoint closes that hole.
- Claude proposed deleting old window rows on every request, then a cleanup job inside each service instance. I disagreed with both: per-request deletes add write load on every call, and N instances each running their own cleaner is redundant. SQLite has no row TTL or scheduler, so the in-service job stays only because the fixed stack leaves no alternative.

## What I'd do differently with more time
- **Redis (or similar) as the window store** instead of the DB: lower latency, atomic sliding window, native expiry, one shared clock, no load on the reservations DB. Not used because the assignment fixes the stack.
- **Replace the in-service cleanup job with one external cleaner** (cron job or DB-side scheduler), so cleanup runs once rather than once per instance. With Redis this disappears entirely (native TTL).
- **Move the stats counters out of the request transaction on a server DB:** write a cheap event row in the same transaction (still exact after a crash) and aggregate the per-supplier counters asynchronously, so a hot supplier's counter row isn't updated on every request. On SQLite there is a single writer, so splitting would add a second lock acquisition and allow crash drift for no gain.
- A server DB (e.g. SQL Server) with proper locking, per-supplier auth, per-IP limits, windowed stats.
