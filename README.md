# DevLine AI

DevLine AI — a local multi-agent pipeline that turns AI coding agents into a disciplined dev team: one implements, another reviews, a third fixes only what was flagged.

.NET 10 | license MIT | version 1.0.0.0

[Português (Brasil)](README.pt-BR.md) · [Architecture](docs/ARCHITECTURE.md) · [Methodology](docs/METHODOLOGY.md) · [History](docs/HISTORY.md) · [Changelog](CHANGELOG.md)

## Why

AI coding agents make mistakes, lose context, repeat failed fixes, and hit usage limits. A human ends up supervising every handoff, rerunning tests, and reconciling competing edits. DevLine AI moves that coordination into a local .NET process outside the agents' sessions: it owns scheduling, Git, validation, checkpoints, and recovery.

The command remains `orq`. Agents are roles and calls, not permanent workers; the implementing and fixing roles can use the same inexpensive model, while a stronger model reviews.

## How it works

```text
Dev → Quick test → Review (prescriptive) → Full validation → Merge into integration branch
 ↑                         │
 └── Fix only what was flagged ← changes requested
```

Test failures also return the task to Dev. With `quickTestCommand` configured, only that hook runs before review; `testCommand` runs at integration. Without a quick hook, `testCommand` runs before review and is repeated at integration when updating the task changes its HEAD. Missing test commands skip validation.

Each writing task uses a Git worktree and a task branch. File reservations keep changes within scope; `arquivosLivres` permits shared files or globs such as `tests/e2e/**`. Dependencies order tasks, and lanes can organize separate runners. Approved work lands on the local integration branch (default `orq/integration`).

Claude Code, Codex CLI, and Ollama are routed by role and complexity. Eligible small jobs can use Ollama; medium jobs normally try Codex first and complex jobs Claude first. A task's `provider` overrides this preference. Review uses `revisorProvider` (default `claude`) without fallback. Provider model maps select `simple`, `medium`, `complex`, `review`, and `escalation`; Codex accepts `model@effort` and passes the effort to `model_reasoning_effort`. Select model IDs available in your installed tools.

Usage limits trigger checkpoints and cooldowns, using the return time in recognized limit messages when available. SQLite stores task phases, sessions, attempts, and provider state so `orq resume` can continue after a restart. Ollama returns text; for an eligible writing task the engine writes it to the single reserved file. Claude and Codex use their respective services; “local” describes orchestration, not all inference.

## Self-correction and learning loop

Validation failures and requested review changes return to the executor, which corrects the reported problem and is revalidated automatically until approval, exhausted attempts, or an interruption requiring capacity or manual action. Review corrections are scoped to ONLY the listed blockers. The useful feedback packet includes file:line, the reviewer's exact correction, a page snapshot at failure, and server exceptions when the validation hook supplies them. Preserve the complete actionable error at the end: failed validation feedback is bounded to the last 6,000 characters, not an unlimited transcript.

Several safeguards are corrections learned from real failures and encoded as deterministic rules: automatic Angular `.ts`/existing `.spec.ts` reservations, repeated incomplete-reservation detection, cooldowns parsed from limit messages, integration updates before testing, and restart recovery. An old-text-in-tests detector also helped the original workflow; it belongs to application validation and is not bundled in the engine.

Lessons remain under human control. Put approved lessons in `.orq/contexto.md`; the CLI includes that context in full execution and review prompts. Resumed correction prompts rely on the session's existing context and send the new feedback. Stored test results, violations, attempts, and events make repeated errors inspectable: the person can identify the test/file, count occurrences, compare task IDs, and read the recorded messages. The current version emits explicit counts for repeated out-of-reservation edits; automatic cross-task failure grouping is planned below. A PERSON decides what becomes a lesson and writes or approves its wording. The tool does not autonomously learn new rules or modify its own code.

## Safeguards (Travas)

- Plan normalization accepts complexity synonyms such as `small` → `simple` and `high` → `complex`.
- Reserving an Angular component template/style also reserves its `.ts` and its `.spec.ts` if the spec exists.
- Out-of-scope edits are reverted and recorded. Repeated reversions of the same file produce a `reserva-incompleta` event for the coordinator to fix the reservation.
- The integration branch is merged into the task before the quick test when it merges cleanly; unresolved conflicts block integration for manual repair.
- Incremental review shows only the fix when all changed files were previously reviewed and their count is within `revisaoIncrementalMaxArquivos`.
- Prescriptive review requests every blocker at once: file:line, defect, failing scenario, exact before/after correction or unambiguous steps, and verification. Optional improvements do not block approval.
- Resumed review corrections explicitly say “fix ONLY the points listed by the reviewer.”
- `execucaoSoNoPreferido` keeps execution on a task's enabled, registered preferred provider and waits for its capacity. Escalation still takes precedence; set `escalarAposReprovacoes: 0` to disable provider escalation.
- Model escalation and provider escalation have separate configurable thresholds. Optional self-review can run before official review.

## Quick start

Windows, Git, and the .NET 10 SDK are required. Validation hooks use `cmd.exe`; this version targets Windows. Install and authenticate the agent CLIs you intend to use, or run Ollama for eligible local jobs.

From this repository's root:

```powershell
dotnet build
dotnet test
Set-Alias orq (Join-Path $PWD 'src\Orq.Cli\bin\Debug\net10.0\orq.exe')
orq --version
orq doctor
```

`orq --version` prints `DevLine AI 1.0.0.0`. `doctor` reports tools, authentication status, local models, skills, and suggested concurrency. It checks all three providers and can report missing tools even when you only plan to use a subset.

Use a target Git repository with an existing commit and a clean working tree:

```powershell
orq allow --repo C:\src\my-app
orq init --repo C:\src\my-app
```

`init` creates `.orq/config.json` and `.orq/plano.exemplo.json`. Edit the configuration to select providers and add your validation commands. Save this minimal plan as `C:\src\my-app\.orq\plan.json`:

```json
{
  "tasks": [
    {
      "id": "T1",
      "title": "Document the application",
      "objective": "Create docs/USAGE.md with the application's local startup instructions.",
      "acceptance": ["The instructions match the repository's actual commands."],
      "role": "developer",
      "complexity": "medium",
      "files": ["docs/USAGE.md"],
      "provider": "codex",
      "review": true
    }
  ]
}
```

```powershell
orq run --repo C:\src\my-app --plan C:\src\my-app\.orq\plan.json --concurrency 1 --wait
orq status --repo C:\src\my-app
orq resume --repo C:\src\my-app --concurrency 1 --wait
orq retry --repo C:\src\my-app --task T1
orq resume --repo C:\src\my-app --concurrency 1 --wait
orq report --repo C:\src\my-app
```

`retry` reopens a stopped, unfinished task and resets attempts; it does not run it. `resume` continues the stored plan. Ctrl+C saves state. `--simulate` uses fake providers without spending tokens, but still creates real local worktrees and commits: use a disposable target repository for a dry run. `--sim-limit codex` simulates a limit. `--only T1,T2` restricts a runner to those IDs; `--nome "Lane A"` names it. `scripts/watchdog.ps1` can reopen existing named launchers; see [Architecture](docs/ARCHITECTURE.md).

The engine creates local commits and merges. It never pushes. Review the result, then merge the integration branch into your branch yourself when ready.

## Configuration reference

Configuration lives in the target repository's `.orq/config.json`. JSON uses camelCase; existing Portuguese key names remain part of the contract. These are all `OrqConfig` properties, including the runtime-only `Context` property.

| Key | Type | Default | Purpose |
|---|---|---|---|
| `baseBranch` | string or null | `null` | Starting ref for a new integration branch; null uses `HEAD`. |
| `integrationBranch` | string | `orq/integration` | Local branch receiving approved tasks. |
| `testCommand` | string or null | `null` | Full validation command in the task worktree; null/blank skips it. |
| `quickTestCommand` | string or null | `null` | Focused validation before review; when set, full validation runs at integration. |
| `maxConcurrency` | integer | `2` | Concurrent tasks in this engine instance; `--concurrency` overrides it. |
| `taskTimeoutMinutes` | integer | `20` | Timeout for each agent call. |
| `testTimeoutMinutes` | integer | `10` | Timeout for each validation hook. |
| `maxAttemptsPerTask` | integer | `3` | Maximum development/analysis attempts; a call without available capacity does not consume one. |
| `cooldownMinutes` | integer | `30` | Rate-limit fallback wait when no recognized reset time is present. |
| `escalarAposReprovacoes` | integer | `2` | Official review rejections before provider escalation; `0` disables it. |
| `escalarModeloAposReprovacoes` | integer | `0` | Rejections before using the current provider's escalation model, before provider escalation; `0` disables it. |
| `escalationProvider` | string | `claude` | Provider used after provider escalation, when enabled and registered. |
| `selfReviewProvider` | string or null | `null` | Optional self-review provider; blank disables the extra call. |
| `execucaoSoNoPreferido` | boolean | `false` | Restricts execution to a task's enabled, registered `provider`; escalation can override it. |
| `revisorProvider` | string | `claude` | Official reviewer; review waits when it is unavailable. |
| `revisaoIncrementalMaxArquivos` | integer | `6` | Maximum changed files for incremental review; all must have been reviewed previously. |
| `arquivosLivres` | array of strings | `[]` | Files/globs any task can edit without a reservation; shared conflicts require manual resolution. |
| `skillRoots` | object of provider → path | `{}` | Provider-specific directories containing `<skill>/SKILL.md`. Use absolute paths. |
| `context` (runtime only) | string or null | `null` | JSON-ignored `Context`; CLI loads `.orq/contexto.md` for prompts. Do not set it in config.json. |
| `limits` | object | values below | Budget settings for one engine invocation. |
| `providers` | object | `claude`, `codex`, `ollama` | Provider settings; all enabled with one concurrent call each by default. |

Nested budget and provider settings:

| Key | Type | Default | Purpose |
|---|---|---|---|
| `limits.maxCallsPerRun` | integer | `30` | Stops dispatching calls when the recorded count reaches this value. |
| `limits.maxCostUsdPerRun` | number | `5.0` | Stops dispatching based on reported USD cost; unknown costs count as zero. |
| `limits.maxTokensPerRun` | integer (64-bit) | `3000000` | Stops dispatching based on recorded input + output tokens. |
| `providers.<name>.enabled` | boolean | `true` | Enables the provider for normal routing. |
| `providers.<name>.maxConcurrent` | integer | `1` | Simultaneous calls to this provider per engine instance (minimum effective value: 1). |
| `providers.<name>.model` | string or null | `null`; Ollama: `gemma3:4b` | Fallback model; null uses the CLI's default. |
| `providers.<name>.url` | string or null | `null`; Ollama: `http://localhost:11434` | Ollama API base URL; unused by the CLI providers. |
| `providers.<name>.models` | object of strings | `{}` | Per-job models: `simple`, `medium`, `complex`, `review`, `escalation`. Missing keys fall back to complexity, then `model`. |

Budgets are checked before calls and count completed calls; concurrent work can exceed a threshold. A new engine invocation, including a `--wait` retry, starts fresh counters. These are operational stop conditions, not prepaid spending caps. Usage is provider-reported or estimated; remaining subscription balances are unknown.

Example configuration (hook scripts belong to your application; create them before use):

```json
{
  "baseBranch": "main",
  "integrationBranch": "orq/integration",
  "testCommand": "powershell -NoProfile -File scripts/validate.ps1",
  "quickTestCommand": "powershell -NoProfile -File scripts/validate.ps1 -Quick",
  "maxConcurrency": 1,
  "taskTimeoutMinutes": 20,
  "testTimeoutMinutes": 10,
  "maxAttemptsPerTask": 3,
  "cooldownMinutes": 30,
  "escalarAposReprovacoes": 0,
  "escalarModeloAposReprovacoes": 0,
  "escalationProvider": "claude",
  "selfReviewProvider": null,
  "execucaoSoNoPreferido": true,
  "revisorProvider": "claude",
  "revisaoIncrementalMaxArquivos": 6,
  "arquivosLivres": ["docs/PROJECT_STATE.md", "tests/e2e/**"],
  "skillRoots": {
    "claude": "C:\\src\\skills\\claude",
    "codex": "C:\\src\\skills\\codex"
  },
  "limits": {
    "maxCallsPerRun": 30,
    "maxCostUsdPerRun": 5.0,
    "maxTokensPerRun": 3000000
  },
  "providers": {
    "claude": { "enabled": true, "maxConcurrent": 1, "model": null, "models": {} },
    "codex": { "enabled": true, "maxConcurrent": 1, "model": null, "models": {} },
    "ollama": { "enabled": false, "maxConcurrent": 1, "model": "gemma3:4b", "url": "http://localhost:11434", "models": {} }
  }
}
```

Paths such as `C:\src\my-app` and `~/projects/my-app` are generic examples; configuration paths are not shell-expanded. In particular, replace `~` with an absolute directory before using it in `skillRoots`.

## Plan file reference

A plan is a JSON object with a `tasks` array. Reusing an ID preserves completed/running tasks; reloading updates the contract of other existing tasks without resetting their state. Use `retry` when necessary.

| Task field | Type / default | Meaning |
|---|---|---|
| `id` | string / empty | Required nonblank identifier; use unique IDs safe for branch/directory names. |
| `title` | string / empty | Short task label. |
| `objective` | string / empty | Concrete requested outcome. |
| `acceptance` | string array / `[]` | Verifiable acceptance criteria. |
| `role` | string / `developer` | `requirements`, `impact` are read-only analysis; `developer`, `tester` write. `reviewer` and `integrator` also exist in the enum but normally belong to engine phases. |
| `complexity` | string / `medium` | `simple`, `medium`, `complex`; selects routing and model. |
| `files` | string array / `[]` | Repository-relative literal files, directories ending in `/`, or `*`/`**` globs. Empty means the entire repository. |
| `dependsOn` | string array / `[]` | Task IDs that must be done first. |
| `review` | boolean / `true` | Requires official review before integration. |
| `provider` | string or null / `null` | Preferred executor: `claude`, `codex`, `ollama`. |
| `lane` | string or null / `null` | Same named lane serializes tasks within an engine; distinct named lanes bypass file-overlap checks, so assign disjoint work carefully. |
| `skills` | string array / `[]` | Required skill folder names under the executing provider's `skillRoots`; each must be installed and declared in the agent's JSON result. |
| `brief` | string or null / `null` | Additional briefing file relative to `.orq`, included in fresh execution prompts. |

Automatic reservation completion adds companion Angular files during Dev; explicitly reserve them in the plan as well so scheduling sees the full scope from the start. Shared free files are permitted edits, not automatic conflict resolution. Multiple runner processes share state and a Git lock, but do not provide a global task/file reservation scheduler; use disjoint `--only` sets and scopes. See [Architecture](docs/ARCHITECTURE.md).

## Validation hooks

`quickTestCommand` and `testCommand` run in the task worktree through `cmd.exe /d /c`, with `testTimeoutMinutes`. Hooks are application-owned: the engine does not select specs or publish your application for you. Nonzero exit or timeout returns the task to Dev with diagnostics.

Put the actual error at the END of a validation script's output, after accessibility snapshots or other verbose context. The engine retains the last **6,000 characters** of combined stdout followed by stderr on failure (only the last 200 on success). Preserve the command's failing exit code. A large snapshot printed after an error can hide the only useful diagnosis.

In the original workflow, a Playwright accessibility snapshot and a file-based test attendant outside the agent sandbox improved feedback. These are application-side integrations described in [Methodology](docs/METHODOLOGY.md), not bundled services.

## Results from a real project

Through 2026-11-20, a real-world SaaS (management system: .NET 8 + Angular + PostgreSQL) supplied 101 planned tasks, 71+ integrated tasks, and roughly 550 agent calls. Early runs recorded 112 quick-validation failures and 49 reverted out-of-reservation edits. Later tasks integrated in about 34 minutes on average, one at a time, with few correction cycles. These are development observations from one project, not a controlled benchmark or a promised success rate; see [History](docs/HISTORY.md).

## Documentation and license

Read the [Portuguese README](README.pt-BR.md), [History](docs/HISTORY.md), [Methodology](docs/METHODOLOGY.md), [Architecture](docs/ARCHITECTURE.md), and [Changelog](CHANGELOG.md). Released under the [MIT License](LICENSE).

## Keywords

multi-agent, ai-coding, claude-code, codex-cli, llm-orchestration, code-review, devtools, dotnet

## Roadmap

**v1.1 — recurring-failure report (planned).** When the same failure signature appears across different tasks, list the case with evidence: test/file, occurrence count, affected task IDs, and recorded error messages. A command such as `orq licoes` (or an equivalent interface) would suggest lesson text. A person approves, edits, or discards it; only approved text enters the context file. This report and approval workflow are not implemented in v1.0.0.0.
