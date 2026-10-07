# CLAUDE.md

Project: Supplier Feed Throttle & Dedup Service (see `candidate-brief.txt`). ASP.NET Core 8+, SQLite, Dapper.

## README maintenance (standing instruction)
Keep `README.md` current as part of every change. When a decision, assumption, disagreement, or "do differently" idea is made or changed, update the matching README section in the **same turn**, without being asked.

README sections to maintain:
1. **Assumptions** — include the zero-tolerance/exact rate limiting assumption (no slack, no approximate counters, no per-instance clocks) and the DB-as-single-source-of-truth assumption.
2. **Where I disagreed with / changed Claude Code's suggestions** — record what Claude proposed, what the user chose instead, and the user's reasoning.
3. **What I'd do differently with more time** — include Redis (or similar) as the window store instead of the DB, with the reasons.

Guidelines: keep it about half a page; record the user's reasoning, not just the outcome; update existing entries instead of duplicating.

## Working rules
- No code until the user approves the spec/plan stage (brainstorming gate).
- Never edit or trim the exported transcript.
- Throttle check runs before validation. The DB stamps time; app code never supplies "now" for throttling.
- Tests control time by inserting backdated `request_log` rows (no sleeping, no fake clock).
- TDD for dedup and throttle logic.
