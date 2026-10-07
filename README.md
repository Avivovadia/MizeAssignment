# Supplier Feed Throttle & Dedup Service

ASP.NET Core (.NET 8+) Web API + SQLite + Dapper. Ingests supplier reservation updates, dedupes resends, throttles noisy suppliers (>100 requests / rolling 60s) across multiple instances.

> Status: planning complete, implementation not started. This README records decisions so far.

## Assumptions
- **Zero tolerance for rate-limit overhead or slack.** The limit is exact: request 101 in any rolling 60s window is never admitted. We accept a DB round trip and a write per request for that. No local caches, approximate counters, or per-instance clocks.
- **The database is the single source of truth for both window state and time.** The window is a `request_log` table; the check-and-insert is one atomic statement and the DB stamps the time.
- Reservation identity is `(supplierId, reservationId)`. `updatedAtUtc` is a version, not a "detail". Same details = `roomId`, `checkIn`, `checkOut`, `price`.
- Older `updatedAtUtc` with different details is `stale` and ignored (out-of-order retries must not revert newer data). Identical details = `unchanged`, no write.
- Supplier identity is the `supplierId` in the body (no auth). Requests without a usable `supplierId` get 400 and aren't counted.
- Throttle check runs **before** validation. Every attributable request counts toward the limit (writes, duplicates, stale, invalid). Throttled requests do not occupy the window, so a supplier is released as soon as old requests age out.
- Responses: 201 `created`; 200 with `status` = `updated` / `unchanged` / `stale`; 400 invalid; 429 + `Retry-After` throttled.
- Stats are lifetime counters: `ingested` (created + updated), `throttled`, `unchanged`, `stale`. Unknown supplier returns zeros (200).
- Validation: required fields, `checkOut > checkIn`, `price >= 0`; timestamps normalized to UTC. No room-overlap/double-booking checks.
- `request_log` cleanup: on each admitted request, delete that supplier's rows older than 60s (same transaction). Bounded at ~100 rows per supplier, no background service.
- **SQLite caveat:** SQLite has no server clock; its "now" is the OS clock of the app process. "DB as single clock" holds only because all instances share one host. Multi-host needs a server DB (e.g. SQL Server `SYSUTCDATETIME()` with `UPDLOCK, HOLDLOCK`). SQLite has no monotonic time, so host clock jumps shift the window. Tests control time by inserting backdated rows.

## Where I disagreed with / changed Claude Code's suggestions
- **Clock source:** Claude's first option was each instance using its own clock and accepting small skew. Rejected: it gives no single source of truth, which conflicts with the exact-limit assumption. Switched to DB-stamped time.

## What I'd do differently with more time
- **Redis (or similar) as the rate-limit window store instead of the DB:** lower latency, atomic sliding window (sorted set / Lua) or `INCR` + TTL, native expiry (no cleanup), one shared clock, and it takes write load off the reservations DB. Not used because the assignment fixes the stack (ASP.NET + SQL + Dapper).
- A server DB (SQL Server) with proper locking, so the single-clock guarantee is real across hosts.
- Per-supplier auth, metrics on stale rate, windowed stats, background cleanup if the table grows.
