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
- Avoid `SQLITE_BUSY` between instances with immediate transactions, WAL, and a finite `Default Timeout` (see "SQLite facts" below).
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

## SQLite facts (chunk 0 results)
Measured with a throwaway console app (outside the repo, discarded) on `Microsoft.Data.Sqlite 8.0.31` + `Dapper 2.1.89`, bundled SQLite **3.53.3** (`SQLitePCLRaw.bundle_e_sqlite3 2.1.12`).
1. **Millisecond time works.** `unixepoch('subsec')` is available; `cast(unixepoch('subsec')*1000 as integer)` gives ms. `julianday` also works but isn't needed. The window design stands.
2. **`BeginTransaction()` is already immediate.** With 100 concurrent read-sleep-write transactions on separate connections under WAL: default `BeginTransaction()` -> 0 errors, no lost updates; raw `BEGIN IMMEDIATE` -> 0 errors; `BeginTransaction(deferred: true)` -> 8/100 failed with `SQLite Error 5: database is locked`. So the risk I raised is real, but only for explicit deferred transactions. Use the default `BeginTransaction()`; no raw `BEGIN IMMEDIATE` needed. (Corrects my earlier assumption that it defaulted to deferred.)
3. **Waiting on locks is controlled by `Default Timeout` (seconds, the command timeout), not by `PRAGMA busy_timeout`** (which reads 0; the provider retries itself). A writer blocked by a held lock failed after ~1.1s with `Default Timeout=1` and waited ~1.6s then succeeded with `30`. `Default Timeout=0` means no limit (waited indefinitely), so set an explicit finite value (e.g. 5-30s).
4. **WAL:** `PRAGMA journal_mode=WAL` persists in the DB file only once the file has been written (setting it on an empty new file reverted to `delete`). Set it in the same init step that creates the schema.
5. **The atomic admit holds under contention:** 150 admit statements from 8 threads, each on its own connection, against one file: exactly 100 admitted, 50 rejected, 0 errors, 100 rows in the log — both as a single autocommit statement and inside an explicit transaction.
6. **Not covered:** separate OS processes (same file-lock mechanism, so expected to behave the same), the throttled-counter increment, and the reservation upsert; those are tested in chunks 4, 5 and 8.

## Implementation chunks
Each chunk is small, test-first (TDD), ends green, and is committed on its own. Starting point: the untracked IDE template (`SupplierFeed.sln`, `src/SupplierFeed.Api`, `tests/SupplierFeed.Tests`; controllers-based Web API, tests csproj is empty).

| # | Chunk | Scope | Done when |
|---|---|---|---|
| 0 | **Spike: SQLite facts** (throwaway) — **DONE, see "SQLite facts"** | Confirm the bundled SQLite supports `unixepoch('subsec')` (3.42+); how to get `BEGIN IMMEDIATE` with `Microsoft.Data.Sqlite` + Dapper; WAL + `busy_timeout`; DB path/connection config shared by several connections. Record findings in plan.md. | Questions answered; any spike code discarded. If ms time is unavailable, revisit the window design before going on. |
| 1 | **Project setup** — **DONE (awaiting acceptance)** | Add Dapper, Microsoft.Data.Sqlite, **NUnit** + `Microsoft.AspNetCore.Mvc.Testing`; project reference tests -> api; strip template leftovers (Swagger, weather sample, HTTPS redirect, auth, IIS launch profile); `public partial class Program` for `WebApplicationFactory`. Folders (`Api`, `Domain`, `Data`) are created by the chunks that add files, not as empty placeholders. | `dotnet build` passes and a smoke test (app starts, unknown route -> 404) passes. |
| 2 | **Schema + connection factory** — **DONE (awaiting acceptance)**. Decisions: timestamps are integer ms (UTC); `price` is a `TEXT` column holding an exact `decimal` (compared as `decimal` in C#, never in SQL; a `NUMERIC` column would turn it into a float, guarded by a test); later, on a server DB, this can become a native decimal column. | `reservations` (unique `(supplierId, reservationId)`), `request_log` (+ index `(supplierId, tsMs)`), `supplier_stats`; idempotent init on startup; connection factory with the settings from chunk 0. | Tests: schema created twice without error; unique constraint enforced. |
| 3 | **Validation + models** (pure, no DB) | Request/response DTOs; payload validation (required fields, `checkOut > checkIn`, `price >= 0`, UTC normalization); outcome enums matching stats vocabulary. | Unit tests for every 400 case. |
| 4 | **Throttle store** | Atomic admit/reject (DB-stamped time), `Retry-After`, `throttled` counter, per-supplier isolation. | Tests with backdated `request_log` rows: 100 admitted, 101st rejected; rejected requests don't extend window; release when rows age out; supplier A doesn't affect B. |
| 5 | **Reservation store** | Upsert + classification: created / updated / duplicate (same, newer-bump, older) / outofdate; far-future `updatedAtUtc` check against DB time; `ingested` / `ignored` counters in the same transaction. | One test per row of the request-types table in this file. |
| 6 | **Ingest endpoint** | Orchestrate parse -> throttle -> validate -> apply; HTTP mapping (201/200/400/429/500). **Trap:** `[ApiController]` auto-400 and model binding run before our code, which would skip the throttle and miss unattributable-request handling, so bind the raw body manually (or disable automatic model-state 400) and read `supplierId` first. | HTTP-level tests through `WebApplicationFactory` for every outcome and status code. |
| 7 | **Stats endpoint** | `GET /api/reservations/stats/{supplierId}` returns `{supplierId, ingested, ignored, throttled}`; zeros for unknown supplier. | Stats match outcomes after a mixed sequence of requests. |
| 8 | **Concurrency tests** | Parallel requests on separate connections (and separate app instances against one DB file): exactly 100 admitted out of 150; concurrent same-reservation writes end consistent; no `SQLITE_BUSY` surfacing as 500. | Tests pass repeatedly (run several times to catch flakiness). |
| 9 | **Cleanup job** | `BackgroundService` doing the global `DELETE ... tsMs <= now - 60000` about every minute; configurable interval. | Test: deletes only out-of-window rows and never changes a throttle decision. |
| 10 | **Wrap-up** | Final README trim (half page), run instructions, review diff against this plan, unedited transcript export. | Checklist in the README's three sections complete; all tests green. |

Order notes: 3 can be done before or after 2; 4 and 5 are independent of each other once 2 exists; 6 needs 3, 4 and 5; 8 and 9 come last because they build on the finished path.

## Next steps
1. Review/adjust the chunks above.
2. Written spec review, then start chunk 0.
3. Keep README short; final transcript export unedited.
