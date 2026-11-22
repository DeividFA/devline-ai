# Architecture

DevLine AI 1.1.0.0 is a Windows-oriented .NET 10 CLI (`orq`) and a core library. It runs outside agent sessions and owns Git, validation, scheduling, and SQLite persistence. [Português](ARQUITETURA.md) · [README](../README.md) · [Methodology](METHODOLOGY.md) · [History](HISTORY.md)

## Components

| File | Responsibility |
|---|---|
| `src/Orq.Cli/Program.cs` | Commands, configuration/context loading, cancellation, simulation, wait/retry loop, version output. |
| `src/Orq.Core/Models.cs` | Task contract and state, configuration/defaults, provider request/result contracts, JSON helpers. |
| `Engine.cs` | Scheduling, worktrees, scope checks, checkpoints, calls/failover, tests, reviews, integration. |
| `Providers.cs` | Claude Code, Codex CLI, Ollama HTTP, simulated providers, failure classification. |
| `Profiles.cs` | Role instructions, provider routing, prescriptive review, incremental review, structured handoff. |
| `Travas.cs` | Complexity normalization, Angular companion reservations, recognized reset-time parsing. |
| `Safety.cs` | Repository authorization, Git allowlist, confined paths, secret redaction, scope/glob matching. |
| `Store.cs` | SQLite tables `tasks`, `attempts`, `providers`, `events`; WAL and 15-second busy timeout. |
| `Proc.cs` | Process execution, redirected output, timeouts/process-tree termination, Git wrapper, file lock. |
| `Doctor.cs` | Environment/authentication diagnostics and status/usage/full reports. |
| `tests/Orq.Tests` | xUnit tests using temporary repositories and simulated providers; CLI version integration test. |
| `scripts/watchdog.ps1` | Optional Windows helper reopening missing named runners from supplied launchers. |

## Task lifecycle

```text
Dev → Test → Review → Integrate → Done
 ↑      │       │         │
 └──────┴───────┴─────────┘  test failure / requested corrections / full-validation failure
```

1. Create `.orq/worktrees/<safe-id>` on `orq/task-<safe-id>` from the integration branch. The integration worktree is `.orq/worktrees/_integration`. A new integration branch starts at `baseBranch` or `HEAD`, so a target repository needs an existing commit.
2. Dev completes Angular reservations, calls the executor with the objective, acceptance criteria, scope, dependency summaries, context, skills, and optional briefing. Required skills must exist for the selected provider and be declared in its final JSON.
3. Scope checks revert tracked changes and remove new files outside reservations, except `arquivosLivres`. Repeated reversions emit `reserva-incompleta`. The engine commits a checkpoint. An empty diff returns to Dev.
4. Test first attempts to merge integration into the task. A conflict is aborted and logged; final integration will handle it. Run `quickTestCommand` if configured, otherwise `testCommand`. Failure returns to Dev with the output.
5. Review can be skipped with `review: false`. Otherwise an optional `selfReviewProvider` call precedes official review. Official review uses `revisorProvider` only (default `claude`); it may be the same provider as the executor if configured that way. The prompt asks for all blockers, each with file:line, scenario, exact correction, and verification. The engine consumes `verdict` and `notes`; it does not structurally validate each requested review item.
6. After rejection, store the reviewed commit, files, and notes. Incremental review is used only for a nonempty fix within previously reviewed files and `revisaoIncrementalMaxArquivos`; otherwise review the whole task diff. Official rejections increment escalation counters and reset self-review eligibility. Resumed executor sessions receive the correction-only instruction.
7. Integrate merges the latest integration branch into the task. A conflict blocks the task for manual repair. With a quick hook configured, run full `testCommand` here; otherwise repeat full validation only if the update changed HEAD. A failed validation returns to Dev.
8. Recheck integration under the Git lock, then `merge --no-ff` the task into integration. If integration moved during validation and touched overlapping files, update and validate again (up to three rounds per integration attempt). A clean update with no common changed files can proceed without another validation; cross-file interactions still need final system validation.

`requirements` and `impact` are read-only analyses in the integration worktree. Results are stored and written to `.orq/artifacts/<safe-id>.md`; dependent tasks receive the analysis or a summary. Integration is deterministic and consumes no agent tokens.

## Scheduling, scope, and locking

A pending/interrupted task starts when dependencies are done, engine concurrency allows it, and it does not conflict with running writing tasks. Scope patterns support literal files, directory prefixes ending in `/`, `*` within a path segment, and `**` across directories. An empty `files` list means whole-repository exclusivity under scope-based scheduling.

When both writing tasks name a lane, the same lane conflicts and different lanes bypass file-overlap checks. If either lane is absent, file scope decides. Declare disjoint scopes for distinct lanes. Companion Angular reservations are completed during Dev, so include them in the original plan for accurate initial scheduling.

Free files bypass scope enforcement; the engine does not automatically add them to task reservations. Merge conflicts in them need manual repair. They are not a global coordination mechanism.

The task scheduler and provider semaphores are per engine instance. Multiple processes share SQLite and `.orq/git.lock`, not an atomic global task-claim or file-reservation scheduler. Give runners disjoint `--only` sets and nonoverlapping work. Recovery checks a recorded PID and process name before reclaiming a running task owned by another live runner.

The cross-process file lock protects short Git operations around worktree creation, integration updates, and merges. Long validation runs outside that lock. A separate integration semaphore serializes integration within an engine instance. The integration-head recheck coordinates concurrent processes.

## Routing and models

| Work | Normal order |
|---|---|
| Simple/medium requirements or impact analysis | ollama → codex → claude |
| Simple developer/tester with one literal file | ollama → codex → claude |
| Other simple/medium execution | codex → claude |
| Complex execution | claude → codex |
| Official review | `revisorProvider` only; unavailable means wait |

A registered, enabled task `provider` moves to the front; `execucaoSoNoPreferido` restricts execution to it. Provider escalation takes precedence when its target is enabled and registered. `escalarModeloAposReprovacoes` selects `models.escalation` on the normal provider before `escalarAposReprovacoes` switches provider; zero disables each threshold separately.

Model resolution tries escalation for non-review work, or `review` for reviewers, then task complexity, then `model`. Codex splits `model@effort` into `-m model` and `-c model_reasoning_effort=effort`. Ollama is text-only; eligible writing output is written into the one literal file by the engine.

## Limits, feedback, and persistence

| Outcome | Action |
|---|---|
| `Success` | Continue the phase. |
| `Failed` | Clear the possibly invalid session and return a work failure for another attempt. |
| `RateLimited` | Checkpoint/handoff; wait until a recognized reset time or `cooldownMinutes`, then allow retry. |
| `AuthError` | Checkpoint/handoff; 24-hour cooldown; login requires manual intervention. |
| `Unavailable` | Checkpoint/handoff; five-minute cooldown. |
| `Timeout` | Checkpoint/handoff; no fixed cooldown. |
| `NetworkError` | Checkpoint/handoff; retry the same task/provider/session after 1, 2, 4, then at most 8 minutes. `redeEsperaInicialMinutos` sets the initial wait and `redeMaxTentativas` defaults to 6 retries; exhaustion interrupts the task. Log `rede` events. |

Cooldowns are stored by provider/model label. Reset parsing recognizes 12-hour `resets …am/pm` and `try again at … AM/PM`, uses the machine's local time, and adds one minute. It does not interpret arbitrary dates or named time zones. Otherwise the configured fallback applies. Failover considers only routing candidates; no candidate leaves the task interrupted. `--wait` repeats runs while interrupted tasks remain; the CLI wait calculation looks up base provider names, so model-specific waits can be polled rather than slept until exactly.

SQLite persists task phase/state, attempts, review history, session IDs, provider cooldowns, and events. Session IDs are per provider/role; Claude and Codex can resume them. Handoffs summarize checkpoints, decisions, tests, pending items, and a short last note rather than replaying complete conversations. Completed tasks remain completed on plan reload. Stale running tasks become interrupted after restart; `retry` resets attempts on a stopped unfinished task.

Budget counters are per engine invocation and checked before calls based on completed calls, reported cost, and recorded tokens. Concurrent calls can overshoot; unknown cost is zero. Successful calls without usage are estimated from text length. `--wait` creates a new engine with fresh counters. Reports label usage and leave subscription balances unknown.

Validation runs in the task worktree through `cmd.exe /d /c`. Failed output retains the last 6,000 redacted characters of stdout followed by stderr; successful output retains 200. Put snapshots before the final error. The Playwright snapshot, old-text detector, and file-based test attendant described in [History](HISTORY.md) were application-owned tools, not built-in services.

## Local files and safety boundaries

`.orq/config.json` is loaded by the CLI; `.orq/contexto.md` supplies runtime-only prompt context. `.orq/orq.db` stores state; `.orq/report.md` stores reports; `.orq/capturas/<safe-id>` supplies optional validation screenshots to skilled tasks. The engine excludes `.orq/` in the target repository. Branches and worktrees are retained for manual inspection/cleanup.

`orq allow` records explicit authorization in `%USERPROFILE%\.orq\repos-autorizados.txt`. The Git wrapper permits only `rev-parse`, `rev-list`, `status`, `add`, `commit`, `worktree add/list`, `merge`, `diff`, `log`, `branch`, `restore`, and `show`, rejecting destructive flags. It does not push, fetch, pull, reset, or remove worktrees. The wrapper's allowlist applies to engine Git calls, not every command an external agent could execute.

Claude runs noninteractively with Read/Grep/Glob and, for writing, Edit/Write; no shell tools. Codex uses its read-only or workspace-write sandbox. Ollama calls its configured HTTP endpoint. Test commands are trusted user configuration. Redaction filters common token/password patterns in logs, errors, reports, and handoffs; it is not a guarantee that arbitrary sensitive data is recognized. Doctor queries authentication status through tools without reading credential files.

## Optional watchdog

`scripts/watchdog.ps1` preserves the helper's purpose: check named `orq.exe` processes, reopen missing existing launchers, log restarts, and leave tasks/processes alone. Configure Windows Task Scheduler to call it periodically, for example every ten minutes:

```powershell
.\scripts\watchdog.ps1 -OrqDir 'C:\src\my-app\.orq' -Launchers @{ 'Lane A' = 'frente-A.cmd'; 'Lane B' = 'frente-B.cmd' }
```

Map keys must match launcher `--nome` values; a legacy ` - ...` suffix is ignored. Values are paths relative to `OrqDir` or absolute paths. It uses Explorer to open the launchers, waits six seconds between restarts, appends `vigia.log`, and exits when `vigia.pausado` exists. It does not create launchers or register a scheduled task. Name launchers consistently to avoid duplicate runners.

## Scope of this release

Plans and validation scripts are supplied externally. Validation hooks require Windows; example Unix-style paths do not imply cross-platform support. Shared-file conflicts and cleanup remain manual. Claude/Codex inference uses their services. Concurrency and budgets are per engine instance; final integration validation remains essential when work is parallel. See the complete [configuration and plan references](../README.md).
