# Changelog

## 1.1.0.0 — 2026-11-21

- Added `orq corrigir --repo <repo> --task <id> (--texto "<instruction>" | --arquivo <path.md>) [--forcar]`. It records coordinator feedback, forces Dev/Pending, resets attempts and runner/owner, and records a `corrigir` event. Active runners are refused unless forced. Previously, restarting a lane with a task still in Test reran validation and OVERWROTE the coordinator's instruction with the failure log; correction now starts in development with that instruction intact.
- Transient network failures now checkpoint and retry the same task/provider/session automatically, with `rede` events and waits of 1, 2, 4, then at most 8 minutes. `redeEsperaInicialMinutos` defaults to 1 and `redeMaxTentativas` to 6 retries after the initial call. Exhaustion interrupts the task; usage limits keep the existing cooldown flow.
- Added generic development rules by area to both methodology guides. `.orq/contexto.md` is included in every execution and review prompt, including resumed corrections; shared frontend components, removal of unused code, and automatic violation checks are illustrated without project/client names.
- Version metadata and `orq --version` now report `DevLine AI 1.1.0.0`.

## 1.0.0.0 — 2026-11-20

First public release of **DevLine AI**; the command remains `orq`.

- Local .NET 10 orchestration outside agent sessions, with task worktrees, file reservations, dependencies, lanes, checkpoints, and SQLite restart recovery.
- Claude Code, Codex CLI, and Ollama providers; routing by role/complexity, model maps, Codex `model@effort`, provider/model cooldowns, usage reporting, and configurable operational budgets.
- Dev → quick validation → prescriptive review → correction → full validation at integration, with deterministic local merges and configurable validation hooks.
- Travas: complexity normalization, Angular `.ts`/existing `.spec.ts` reservation completion, repeated scope-violation diagnostics, integration updates before testing, free-file globs, incremental review, optional self-review, preferred-only execution, and configurable model/provider escalation.
- Prescriptive review requests all blockers with file:line, exact correction, and verification; resumed corrections explicitly target only the reviewer's points.
- Failed validation feedback retains the last 6,000 redacted characters; validation scripts should print the actual error after snapshots.
- `orq --version` reports `DevLine AI 1.0.0.0`; CLI and core assemblies carry matching product and four-part version metadata, with a CLI integration test.
- Generic examples and prompts replace identifying project/person references. The project-specific root watchdog moved to `scripts/watchdog.ps1`, parameterized with `-OrqDir` and `-Launchers`, preserving restart/log/pause behavior without fixed project paths.
- English and Brazilian Portuguese README, architecture, methodology, and history; MIT license (2026 DevLine AI contributors). Local databases, state, distribution output, and the publication plan are ignored.

The [README](README.md) describes the configuration and platform limits. Playwright snapshots, the test attendant, and the old-text detector in the history are application-side integrations, not bundled services.

## Pre-release history (v2–v21, internal builds)

These were internal milestones during development of a real-world SaaS (management system: .NET 8 + Angular + PostgreSQL), not earlier public releases.

- **v2–v8:** establish local orchestration, worktrees, Git/test ownership, and persistent recovery.
- **v9:** narrow the merge lock to Git operations so long validation does not stop other runners.
- **v11:** introduce escalation after repeated review rejection; later expose separate model/provider thresholds and a way to disable provider escalation.
- **v14:** update the task with integration before quick validation instead of testing an old base.
- **v16–v19:** normalize plan synonyms; complete Angular reservations after six repeated build failures; diagnose incomplete reservations; parse usage-reset times; allow shared documents; add incremental/prescriptive review, configurable reviewer, preferred-only execution, and correction-only instructions.
- **Pipeline and test practice:** overlap at most two tasks, then reduce to one when CPU contention makes tests unstable; attach accessibility snapshots and use an external file-based test attendant; record precise lessons and corrections for repeated failures.
- **v20:** allow free-file globs so UI text and affected tests can change together; application validation detects stale expected text with file:line.
- **v21:** increase validation feedback from 1,500 to 6,000 characters and print errors after snapshots in application validation.

Through 2026-11-20: 101 planned tasks, 71+ integrated, approximately 550 agent calls; early runs saw 112 quick-validation failures and 49 reverted out-of-reservation edits. Later tasks integrated in about 34 minutes on average, one at a time, with few correction cycles. These observations are not a controlled benchmark. See [History](docs/HISTORY.md) / [História](docs/HISTORIA.md) and [Methodology](docs/METHODOLOGY.md) / [Metodologia](docs/METODOLOGIA.md).
