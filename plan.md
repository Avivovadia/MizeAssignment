# Plan: Supplier Feed Throttle & Dedup Service

ASP.NET Core 8+ Web API, SQLite, Dapper. Endpoints:
- `POST /api/reservations/ingest`
- `GET /api/reservations/stats/{supplierId}`

Must be correct across multiple server instances. See `candidate-brief.txt` for the brief and `README.md` for the short list of key decisions.

## Principles
- **Exact limit, no slack:** at most 100 requests per supplier in any rolling 60s. Request 101 is never admitted. We pay DB cost per request for that (performance is the trade-off, not accuracy). No local caches, approximate counters, or per-instance clocks.
- **The database is the single source of truth** for window state and time (no Redis: the stack is fixed).
- Reservation identity is `(supplierId, reservationId)`. `updatedAtUtc` is its version.
- "Same details" = `roomId`, `checkIn`, `checkOut`, `price`.
- Tests control time by inserting backdated `request_log` rows (no sleeping, no fake clock).

## Processing order for every ingest request
1. Parse body. No usable `supplierId` (malformed JSON, missing field) -> 400, nothing counted.
2. Throttle: atomic admit/reject (see Concurrency).
3. Validate payload.
4. Reject far-future `updatedAtUtc`.
5. Upsert reservation and classify the outcome.

## Request types

| Request type | When | Response | In 60s window | Stats counter | DB writes |
|---|---|---|---|---|---|
| Created | New `(supplierId, reservationId)` | 201 `{status:"ingested", detail:"created"}` | Yes | `ingested` | Insert |
| Updated | Key exists, details differ, incoming `updatedAtUtc` >= stored | 200 `{status:"ingested", detail:"updated"}` | Yes | `ingested` | Update |
| Duplicate, same version | Identical details, same `updatedAtUtc` | 200 `{status:"ignored", detail:"duplicate"}` | Yes | `ignored` | None |
| Duplicate, newer version | Identical details, newer `updatedAtUtc` | 200 `{status:"ignored", detail:"duplicate"}` | Yes | `ignored` | Bump stored `updatedAtUtc` only |
| Duplicate, older version | Identical details, older `updatedAtUtc` | 200 `{status:"ignored", detail:"duplicate"}` | Yes | `ignored` | None |
| Out of date | Details differ, incoming `updatedAtUtc` older than stored | 200 `{status:"ignored", detail:"outofdate"}`, stored data kept | Yes | `ignored` | None |
| Throttled | 100 admitted requests already in last 60s | 429 + `Retry-After`, `{status:"throttled", retryAfterSeconds}` | **No** | `throttled` | Counter only |
| Invalid | Missing field, `checkOut <= checkIn`, `price < 0`, `updatedAtUtc` > DB now + ~5 min | 400 with message | Yes | none | None |
| Unattributable | Malformed JSON / no usable `supplierId` | 400 | No | none | None |
| Server error | Unexpected failure after admission | 500 | Yes (slot burnt) | none | None |

Notes:
- Equal `updatedAtUtc` with different details is an update, not out of date.
- Response `status` uses the same vocabulary as the stats counters. `detail` is explanatory only and is not counted.
- Throttled requests don't occupy the window, so a supplier is released as soon as old rows age out.
- Invalid requests consume quota because throttling runs before validation.

## Stats
`GET /api/reservations/stats/{supplierId}` -> `{supplierId, ingested, ignored, throttled}`, lifetime counters.
- `ingested` = created + updated (data written).
- `ignored` = duplicates + out of date (accepted, nothing applied).
- `throttled` = 429s.
- They partition all attributable, valid requests. Invalid (400) requests are in no counter.
- Unknown supplier -> 200 with zeros.

## Concurrency design
- `request_log(supplierId, tsMs)` table. Window = rows for the supplier in the last 60s.
- Atomic check-and-insert in one statement: `INSERT ... SELECT ... WHERE (count in window) < 100`. 0 rows affected -> throttled. The DB stamps the time in the same statement; the app never supplies "now".
- Throttled -> increment the supplier's throttled counter in the same transaction (`INSERT ... ON CONFLICT DO UPDATE`).
- Reservation upsert: unique index on `(supplierId, reservationId)`; conditional `UPDATE ... WHERE updatedAtUtc <= @new AND details differ`, so concurrent writers can't regress data.
- Throttle admit and reservation upsert are separate transactions (a crash between burns a quota slot; accepted).
- `Retry-After` comes from the oldest row in the window.
- Use `BEGIN IMMEDIATE` + `busy_timeout` to avoid `SQLITE_BUSY` between instances. Verify the bundled SQLite supports ms precision (`unixepoch('subsec')`, 3.42+).
- Cleanup: a `BackgroundService` per instance, roughly once a minute, runs one global `DELETE FROM request_log WHERE tsMs <= now - 60000` (DB clock). Not per request: a per-request delete adds lock time to SQLite's single writer and is redundant across instances. Correctness never depends on it (the count query only reads in-window rows via the `(supplierId, tsMs)` index, so old rows don't slow it). Idempotent, so overlapping instances are harmless. No TTL option exists on SQLite (no row expiry, no event scheduler; a trigger would just be per-insert cost inside the DB), so the in-service job is a compromise; the preferred design is a single external cleaner (cron job / DB-side scheduler) — see README. Also cleans rows from quiet/spoofed suppliers. Table stays ~100-200 rows per supplier.

## Assumptions
1. Supplier identity is the `supplierId` in the body (no auth).
2. Throttle check runs before validation; invalid attributable payloads count toward the limit.
3. Validation: required fields, `checkOut > checkIn`, `price >= 0`; timestamps normalized to UTC. No room-overlap/double-booking checks.
4. Price stored with exact decimal semantics (integer minor units or normalized text; decide at implementation).
5. Stats are lifetime, not windowed.
6. `updatedAtUtc` more than ~5 minutes ahead of the DB clock is rejected, otherwise one far-future timestamp would make every later real update out of date.

## Known limitations
- **SQLite clock:** SQLite has no server clock. Its "now" is the OS clock of the app process, so "DB as single clock" holds only because all instances share one host. Multi-host needs a server DB (e.g. SQL Server `SYSUTCDATETIME()` with `UPDLOCK, HOLDLOCK`). Instances on different hosts can't safely share a SQLite file over a network filesystem.
- No monotonic time in SQLite: NTP/manual clock jumps on the host shift the window.
- **Single writer lock:** every request writes (log row, counters; throttled ones too), so a flooding supplier can slow others.
- **Unattributable garbage** is unthrottled; without auth, anyone can spoof a `supplierId` and burn its quota. A per-IP limit would be the real fix (out of scope).
- "Multiple instances" in tests are parallel connections in one process: same clock, not proof of multi-host behavior.

## Decisions history (challenge rounds)
- Clock: Claude first proposed per-instance clocks accepting skew; rejected for DB-stamped time.
- Window contents: only admitted requests occupy the window.
- Throttle counting: every attributable request counts, including invalid ones.
- Stale/duplicate merged into one `ignored` stats bucket; `stale` renamed `outofdate`; response vocabulary matches stats.
- Identical details + newer version -> bump stored version, so a later delayed retry is still rejected as out of date.
- "No overhead" means no over-limit slack, not no DB cost.
- Cleanup moved from per-request to a periodic global background delete (per-request delete was redundant write load on the single SQLite writer).
- User preferred an external cleaner over a job in every instance; no SQLite TTL exists, so the in-service job stays and the external cron cleaner is listed as the "with more time" change.

## Testing plan
- Dedup: created / updated / duplicate (same, newer, older version) / out of date / same reservationId under different suppliers.
- Throttle: 100 admitted, 101st -> 429; throttled requests don't extend the window; release after backdated rows age out; invalid requests count toward the limit.
- Concurrency: parallel requests on separate connections never admit more than 100; concurrent same-reservation writes end consistent.
- Stats counters match outcomes. Validation 400 cases including far-future `updatedAtUtc`.

## Next steps
1. Written spec from this plan; user review.
2. Implementation plan (`writing-plans`).
3. TDD implementation.
4. Keep README short; final transcript export unedited.
