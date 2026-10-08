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
1. **Parse and validate (pure, in memory, no DB):** `IngestParser.Parse` reads the `supplierId` and validates the payload in one pass. Result: Unattributable (no usable `supplierId`: 400, nothing counted, no DB access), Invalid(supplierId), or Valid.
2. **Future-timestamp validation (orchestrator, needs the DB clock):** a validation step of its own that belongs to the orchestrator, not to the reservation. Right after step 1, for a Valid request, `updatedAtUtc` more than the configured skew ahead of DB time turns it into Invalid(supplierId). It completes the validity verdict before the throttle.
3. **Throttle (DB):** atomic admit/reject for Invalid and Valid requests. Rejected -> 429, `throttled`+1; if the payload was also invalid, `invalid`+1 as well (the verdict is already known and is recorded for observability, so a supplier sending garbage stays visible while it is throttled).
4. **If admitted and Invalid:** 400, `invalid`+1, written in the same transaction as the admit (the verdict is already known), so no second transaction.
5. **If admitted and Valid:** upsert the reservation and classify the outcome (`ReservationStore`).

## Request types

| Request type | When | Response | In 60s window | Stats counter | DB writes |
|---|---|---|---|---|---|
| Created | New `(supplierId, reservationId)` | 201 `{status:"ingested", detail:"created"}` | Yes | `ingested` | Insert |
| Updated | Key exists, details differ, incoming `updatedAtUtc` >= stored | 200 `{status:"ingested", detail:"updated"}` | Yes | `ingested` | Update |
| Duplicate, same version | Identical details, same `updatedAtUtc` | 200 `{status:"ignored", detail:"duplicate"}` | Yes | `ignored` | None |
| Duplicate, newer version | Identical details, newer `updatedAtUtc` | 200 `{status:"ignored", detail:"duplicate"}` | Yes | `ignored` | Bump stored `updatedAtUtc` only |
| Duplicate, older version | Identical details, older `updatedAtUtc` | 200 `{status:"ignored", detail:"duplicate"}` | Yes | `ignored` | None |
| Out of date | Details differ, incoming `updatedAtUtc` older than stored | 200 `{status:"ignored", detail:"outofdate"}`, stored data kept | Yes | `ignored` | None |
| Throttled | 100 admitted requests already in last 60s | 429 + `Retry-After` header (whole seconds, rounded up: HTTP allows nothing finer), `{status:"throttled", retryAfterMs, limit, windowSeconds}` (exact ms, and the configured rule, e.g. 100 per 60s) | **No** | `throttled` (and `invalid` too if the payload was invalid) | Counters only |
| Invalid | Missing field, `checkOut <= checkIn`, `price < 0`, `updatedAtUtc` > DB now + ~5 min | 400 `{status:"invalid", errors:[...]}` | Yes | `invalid` | `request_log` row + counter only; never `reservations` |
| Unattributable | Malformed JSON / no usable `supplierId` | 400 | No | none | None |
| Server error | Unexpected failure after admission (apply step throws) | 500 | Yes (slot burnt, kept by the savepoint) | none | `request_log` row only; the apply step is rolled back |

Notes:
- Equal `updatedAtUtc` with different details is an update, not out of date.
- Response `status` uses the same vocabulary as the stats counters. `detail` is explanatory only and is not counted.
- Throttled requests don't occupy the window, so a supplier is released as soon as old rows age out.
- Invalid requests consume quota: the throttle runs on every attributable request, and the invalid verdict is only acted on after admission. Their `request_log` row is the rate-limit record; nothing is written to `reservations`.

## Stats
`GET /api/reservations/stats/{supplierId}` -> `{supplierId, ingested, ignored, invalid, throttled}`, lifetime counters.
- `ingested` = created + updated (data written).
- `ignored` = duplicates + out of date (accepted, nothing applied).
- `invalid` = every attributable request whose payload was rejected (bad payload or far-future `updatedAtUtc`), whether it was admitted (400) or throttled (429).
- `throttled` = 429s.
- Validity and the throttle outcome are separate facts about a request, and both are recorded, so a throttled invalid request is in both `invalid` and `throttled`. The counters therefore overlap and do not sum to total traffic: `ingested + ignored` = requests applied; `throttled` = all rejected by the limit; `invalid` = all bad payloads. Unattributable requests (no `supplierId`) are in no counter.
- Unknown supplier -> 200 with zeros.

## Concurrency design
- `request_log(supplierId, tsMs)` table. Window = rows for the supplier in the last 60s.
- Atomic check-and-insert in one statement: `INSERT ... SELECT ... WHERE (count in window) < 100`. 0 rows affected -> throttled. The DB stamps the time in the same statement; the app never supplies "now".
- Throttled -> increment the supplier's throttled counter in the same transaction (`INSERT ... ON CONFLICT DO UPDATE`).
- Reservation upsert: unique index on `(supplierId, reservationId)`; conditional `UPDATE ... WHERE updatedAtUtc <= @new AND details differ`, so concurrent writers can't regress data.
- **One transaction per request, owned by the orchestrator** (chosen over separate transactions per step): the admit row, the reservation write and the stats counter commit or roll back together, so stats can't drift after a crash, and the writer lock is taken once. The apply step runs inside a `SAVEPOINT`: if it throws, roll back to the savepoint and commit the admit row, so a failing request still consumes its quota slot (otherwise a persistent failure would let a supplier retry unthrottled) and returns 500. Stores take the connection and transaction as arguments and never open their own.
- Trade-offs accepted: throttle SQL is coupled to the DB transaction (a Redis window store could not join it, so it would be split out behind an interface then); SQL Server needs `SAVE TRANSACTION` instead of `SAVEPOINT`; lock-hold time per request is slightly longer (unmeasured).
- The throttle reads the DB clock once per decision and uses that single instant for the count, the insert and the retry time. Measuring the retry time at a later instant could report 0 (or the wrong, later row's expiry) when the oldest row expires between two reads; with one instant the retry time is always positive and consistent with the decision. Tests fix the instant through an internal `TryAdmitAt`. The several statements are safe because both time and data are frozen: one instant, and the write lock held by the transaction. **Precondition:** the transaction must already hold the write lock when the clock is read (the default `BeginTransaction()` does, deferred does not; with a deferred one the instant could be stale by the lock wait and the admit row stamped too early). A test pins the library default.
- Retry time comes from the oldest row in the window and is exact (milliseconds), never rounded by the store. The 429 body also states the configured rule (`limit`, `windowSeconds`) so the supplier knows why, even after the configuration changes. Only the HTTP `Retry-After` header is rounded up to whole seconds, because the protocol cannot carry fractions.
- Avoid `SQLITE_BUSY` between instances with immediate transactions, WAL, and a finite `Default Timeout` (see "SQLite facts" below).
- Cleanup: a `BackgroundService` per instance, roughly once a minute, runs one global `DELETE FROM request_log WHERE tsMs <= now - 60000` (DB clock). Not per request: a per-request delete adds lock time to SQLite's single writer and is redundant across instances. Correctness never depends on it (the count query only reads in-window rows via the `(supplierId, tsMs)` index, so old rows don't slow it). Idempotent, so overlapping instances are harmless. No TTL option exists on SQLite (no row expiry, no event scheduler; a trigger would just be per-insert cost inside the DB), so the in-service job is a compromise; the preferred design is a single external cleaner (cron job / DB-side scheduler) — see README. Also cleans rows from quiet/spoofed suppliers. Table stays ~100-200 rows per supplier.

## Assumptions
1. Supplier identity is the `supplierId` in the body (no auth).
2. The throttle runs on every attributable request, after the pure parse/validate step and before any consequence of validity is applied; invalid attributable payloads count toward the limit and have their own `invalid` counter.
3. Validation: required fields, `checkOut > checkIn`, `price >= 0`; timestamps normalized to UTC. No room-overlap/double-booking checks.
4. Price stored with exact decimal semantics (integer minor units or normalized text; decide at implementation).
5. Stats are lifetime, not windowed.
6. `updatedAtUtc` more than ~5 minutes ahead of the DB clock is rejected, otherwise one far-future timestamp would make every later real update out of date.

## Known limitations
- **SQLite clock:** SQLite has no server clock. Its "now" is the OS clock of the app process, so "DB as single clock" holds only because all instances share one host. Multi-host needs a server DB (e.g. SQL Server `SYSUTCDATETIME()` with `UPDLOCK, HOLDLOCK`). Instances on different hosts can't safely share a SQLite file over a network filesystem.
- No monotonic time in SQLite: NTP/manual clock jumps on the host shift the window.
- **Single writer lock:** every request writes (log row, counters; throttled ones too), so a flooding supplier can slow others. Measured, see "Measured locking behavior": throughput falls as callers pile up, and scaling out instances does not add write throughput.
- **Unattributable garbage** is unthrottled; without auth, anyone can spoof a `supplierId` and burn its quota. A per-IP limit would be the real fix (out of scope).
- **Multi-instance coverage:** tested with several hosts in one process and with real separate OS processes sharing one database file, all on one machine (one clock, one filesystem). **Different-machine behavior is out of scope**: SQLite cannot safely span machines (file locking over a network filesystem is unreliable), so it is not tested; supporting it needs a server database (see the clock note above).
- **Lock waits can time out:** a request waits for the write lock up to `Default Timeout` (15s). Under extreme contention it would fail with a 500 instead of waiting longer. The measurements below reached about 5s at 32 callers; failure at higher concurrency is expected but was not measured.

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

## Measured locking behavior (chunk 9)
An on-demand report (`dotnet test -c Release --filter "TestCategory=Performance" --logger "console;verbosity=detailed"`, test class `LockingPerformanceReport`) measures the single writer lock. Release build, 8 cores, one local disk, 300 requests per row, callers on dedicated threads, full write path (parse, throttle, reservation, stats, commit). Reproducible within about 10% across runs.

| Scenario | Callers | req/s | p50 ms | p99 ms | max ms |
|---|---|---|---|---|---|
| New reservation per request, never throttled | 1 | 355 | 2.6 | 5.1 | 38 |
| | 8 | 170 | 2.6 | 1284 | 1763 |
| | 32 | 54 | 2.8 | 5116 | 5593 |
| One supplier already throttled (abuse path) | 1 | 423 | 2.3 | 3.8 | 6 |
| | 32 | 83 | 2.3 | 3138 | 3610 |
| One supplier flooding from empty | 1 | 394 | 2.4 | 3.8 | 14 |
| | 32 | 59 | 2.5 | 4597 | 4935 |
| Same, 2 real server processes over HTTP | 8 | 140 | 3.0 | 1428 | 2143 |
| | 32 | 107 | 80.8 | 2315 | 2793 |
| Same flood with at most 1 writer per process (queue in front of the DB) | 8 / 16 / 32 | 382 / 377 / 373 | 2.6 | ~770-790 | ~780-800 |

What it shows:
- **A request that gets the lock is fast (p50 ~2.5ms); the cost is waiting for it.** Throughput falls as callers are added (355 req/s with 1 caller, 54 with 32) and the tail explodes (p99 over 5s at 32 callers). The writer lock is the bottleneck, and waiting callers make it worse, not better.
- **The throttled (abuse) path costs about the same as a normal write** (it still commits a counter), so a flooding supplier occupies the shared lock as much as normal traffic and raises latency for every supplier.
- **Scaling out does not help write throughput:** two real processes sharing the file were no faster than one. All instances contend for the same file lock.
- **The cause is the waiting, not the disk:** with 8 callers, 'synchronous = NORMAL' did not raise commit throughput (264-598/s versus 230-306/s for the default FULL), although it made single-caller commits 15-20x faster (about 6000-9000/s versus 410-450/s).
- **A queue in front of the lock fixes the collapse:** letting 1 writer per process into the database at a time (the rest wait in memory) held throughput at the single-caller level (~375 req/s) for 8, 16 and 32 callers, finished the 300 requests in 0.8s instead of 5.1s, and cut the worst wait from 4.9s to 0.8s. Not implemented yet (a design decision); the experiment is in the report (section D).
- **Likely next steps, in order of payoff:** bound concurrent writers per instance (about 6x at 32 callers); then `synchronous = NORMAL` once writers are serialized (trade-off: after an OS or power failure the last few commits may be lost, which can let a supplier slightly exceed its limit once; an application crash alone loses nothing; this combination is unmeasured end to end); for real scale, Redis for the window (README).

## Implementation chunks
Each chunk is small, test-first (TDD), ends green, and is committed on its own. Starting point: the untracked IDE template (`SupplierFeed.sln`, `src/SupplierFeed.Api`, `tests/SupplierFeed.Tests`; controllers-based Web API, tests csproj is empty).

| # | Chunk | Scope | Done when |
|---|---|---|---|
| 0 | **Spike: SQLite facts** (throwaway) — **DONE, see "SQLite facts"** | Confirm the bundled SQLite supports `unixepoch('subsec')` (3.42+); how to get `BEGIN IMMEDIATE` with `Microsoft.Data.Sqlite` + Dapper; WAL + `busy_timeout`; DB path/connection config shared by several connections. Record findings in plan.md. | Questions answered; any spike code discarded. If ms time is unavailable, revisit the window design before going on. |
| 1 | **Project setup** — **DONE** | Add Dapper, Microsoft.Data.Sqlite, **NUnit** + `Microsoft.AspNetCore.Mvc.Testing`; project reference tests -> api; strip template leftovers (Swagger, weather sample, HTTPS redirect, auth, IIS launch profile); `public partial class Program` for `WebApplicationFactory`. Folders (`Api`, `Domain`, `Data`) are created by the chunks that add files, not as empty placeholders. | `dotnet build` passes and a smoke test (app starts, unknown route -> 404) passes. |
| 2 | **Schema + connection factory** — **DONE**. Decisions: timestamps are integer ms (UTC); `price` is a `TEXT` column holding an exact `decimal` (compared as `decimal` in C#, never in SQL; a `NUMERIC` column would turn it into a float, guarded by a test); later, on a server DB, this can become a native decimal column. | `reservations` (unique `(supplierId, reservationId)`), `request_log` (+ index `(supplierId, tsMs)`), `supplier_stats`; idempotent init on startup; connection factory with the settings from chunk 0. | Tests: schema created twice without error; unique constraint enforced. |
| 3 | **Validation + models** (pure, no DB) — **DONE**. Two-stage parse (`IngestParser`): tolerant `supplierId` read first (Unattributable / Invalid / Valid), then strict fields; property names case-insensitive; timestamps ISO 8601 only, offset-less = UTC (`AssumeUniversal`), stored as UTC ms; `supplierId` compared exactly; responses serialize lowercase (`outofdate`) via `IngestJson.Options`. | Request/response DTOs; payload validation (required fields, `checkOut > checkIn`, `price >= 0`, UTC normalization); outcome enums matching stats vocabulary. | Unit tests for every 400 case. |
| 4 | **Throttle store** (pure: knows nothing about validity, stats or reservations) — **DONE** | `TryAdmit(connection, transaction, supplierId)` -> `Admitted` or `Throttled(retryAfterMs, limit, windowSeconds)`; atomic insert with DB-stamped time; exact (unrounded, always positive) retry time from the oldest in-window row, measured at the same DB instant as the decision, plus the configured rule that was exceeded; limit and window from `ThrottleOptions` (config, defaults 100 / 60s). Does not touch stats. | Tests with backdated `request_log` rows: 100 admitted, 101st rejected; rejected requests add no row; release when rows age out; per-supplier isolation; custom limit honored; 150 parallel admits (own connections) give exactly 100. |
| 5 | **Reservation store** — **DONE**; the provider saves `450.00` as the text `"450.0"` (exact value, trailing zeros dropped), which is why prices are compared as numbers | `Apply(connection, transaction, ValidatedIngest)` -> created / updated / duplicate (same, newer-bump, older) / outofdate. Reservation logic only: no stats, no future-timestamp check (that is an orchestrator validation, chunk 6). | One test per row of the request-types table in this file. |
| 6 | **Building blocks: DB clock, future-timestamp policy, stats store** — **DONE** | `DbClock.NowMs` (the clock query now private to `ThrottleStore` moves into it). `UpdatedAtPolicy` (configured max future skew, default 300s, error message includes it): the orchestrator-only future-timestamp validation. `StatsStore.Record(...)` overloads take the *results* (`IngestDetail`, `ThrottleDecision.Throttled`, `IngestParseResult.Invalid`) and own the mapping result -> counter (`ingested` / `ignored` / `invalid` / `throttled`); `StatsStore.Get` reads them (zeros for an unknown supplier). The throttle store never touches stats; the orchestrator only passes results to the stats store. | Each result type bumps exactly its own counter; first increment creates the row; per-supplier isolation; rollback leaves no counts; clock close to the process clock and never goes backwards; policy passes at exactly the skew and fails 1ms over, message carries the configured value; config binding through the app. |
| 7 | **Ingest orchestrator** (service, no HTTP) — **DONE** | `Ingest(string? body)` -> `Unattributable` / `Invalid` / `Throttled` / `Processed` / `Failed`. Owns the single transaction and savepoint: parse (pure) -> if unattributable stop (no DB) -> future-timestamp validation right after the parse validation (DB clock) -> throttle -> throttled: pass the throttle result to the stats store (and the invalid verdict too, if there is one), commit; admitted: all remaining work inside a savepoint (invalid: record it; valid: apply the reservation, record the outcome); on failure roll back to the savepoint, keep the admit row, commit, log, return `Failed`. | Orchestrator tests per request type (result, reservation rows, `request_log` rows, every counter); invalid and future-dated requests count toward the limit; a throttled request writes no reservation; counters add up to attributable requests; failure injection with a DB trigger that makes reservation inserts fail: one extra `request_log` row, no reservation, no counter change, next request works. |
| 8 | **HTTP endpoints** — **DONE**; also smoke-tested against the real Kestrel server with curl (201/200/400/429 + `Retry-After`, stats). An invalid payload that is throttled returns 429 | `POST /api/reservations/ingest` and `GET /api/reservations/stats/{supplierId}` (zeros for unknown supplier); HTTP mapping (201/200/400/429/500; 429 sets the `Retry-After` header to the exact retry time rounded up to whole seconds, since the header can't carry fractions, and the body carries `retryAfterMs`, `limit`, `windowSeconds`). **Trap:** `[ApiController]` auto-400 and model binding run before our code, which would skip the throttle and miss unattributable-request handling, so read the raw body manually (or disable automatic model-state 400). Wire `IngestJson.Options` into MVC. | HTTP-level tests through `WebApplicationFactory` for every outcome and status code; stats match outcomes after a mixed sequence of requests. |
| 9 | **Concurrency tests** — **DONE**: orchestrator under 16 callers, 3 hosts sharing one file, and 2 real server processes started simultaneously (`Category("MultiProcess")`); plus an on-demand locking performance report and a `BeginWriteTransaction` helper so the lock precondition is pinned on the exact call the orchestrator makes | Parallel requests on separate connections (and separate app instances against one DB file): exactly 100 admitted out of 150; concurrent same-reservation writes end consistent; no `SQLITE_BUSY` surfacing as 500. | Tests pass repeatedly (run several times to catch flakiness). |
| 10 | **Cleanup job** | `BackgroundService` doing the global `DELETE ... tsMs <= now - 60000` about every minute; configurable interval. | Test: deletes only out-of-window rows and never changes a throttle decision. |
| 11 | **Wrap-up** | Final README trim (half page), run instructions, review diff against this plan, unedited transcript export. | Checklist in the README's three sections complete; all tests green. |

Order notes: 4 and 5 are independent of each other once 2 exists; 6 needs 3 and 4; 7 needs 4, 5 and 6; 8 needs 7; 9 and 10 come last because they build on the finished path.

## Next steps
1. Review/adjust the chunks above.
2. Written spec review, then start chunk 0.
3. Keep README short; final transcript export unedited.
