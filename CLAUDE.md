# CLAUDE.md

Project: Supplier Feed Throttle & Dedup Service (see `candidate-brief.txt`). ASP.NET Core 8+, SQLite, Dapper.

## Docs maintenance (standing instruction)
Two documents, kept current in the same turn as any decision change, without being asked:

- **`README.md`** — short (about half a page). Only the **key decisions made on the spec**, plus the two submission sections.

README sections (keep these three, keep each brief):
1. **Key decisions / assumptions** — only decisions that shape the spec's behavior (e.g. exact limit with no slack, DB as single source of truth, identity and versioning, throttle-before-validation, response/stats vocabulary).
2. **Where I disagreed with / changed Claude Code's suggestions** — what Claude proposed, what the user chose, and why.
3. **What I'd do differently with more time** — include Redis (or similar) as the window store instead of the DB.

Update existing entries instead of duplicating; record the user's reasoning, not just the outcome.

## Working rules
- No code until the user approves the spec/plan stage (brainstorming gate).
- Never edit or trim the exported transcript.
- Throttle check runs before validation. The DB stamps time; app code never supplies "now" for throttling.
- Terminology: response `status` is `ingested` | `ignored` | `throttled`, matching the stats counters; `detail` is `created` | `updated` | `duplicate` | `outofdate`.
- Tests control time by inserting backdated `request_log` rows (no sleeping, no fake clock).
- TDD for dedup and throttle logic.
