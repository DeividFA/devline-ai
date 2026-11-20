# From internal builds to DevLine AI 1.0.0.0

DevLine AI began as a local .NET orchestrator outside the agents' sessions. It ran Git and tests, isolated each writing task in a Git worktree, and stored state in SQLite for restart recovery. Internal builds v2–v21 evolved during development of a real-world SaaS (management system: .NET 8 + Angular + PostgreSQL), across roughly one hundred backend, Angular administration-panel, and Playwright tasks.

This timeline describes problems, changes, and observed effects. Internal build numbers are development milestones, not public release versions. [Português](HISTORIA.md) · [README](../README.md) · [Methodology](METHODOLOGY.md) · [Architecture](ARCHITECTURE.md) · [Changelog](../CHANGELOG.md)

## Timeline

| Milestone | Problem → change → effect |
|---|---|
| v2–v8: local coordination | Agents needed durable coordination outside their conversations → the orchestrator took ownership of scheduling, Git, tests, worktrees, and SQLite checkpoints → interrupted work could continue from stored task state rather than a human reconstructing every session. |
| v9: shorten the merge lock | The merge lock covered the entire validation run and stalled all runners → it was narrowed to Git operations → other runners could create worktrees and continue work while validation ran. |
| v11: escalation | Repeated review rejections kept returning to the same level of execution → escalation added a stronger model on the same provider, then another provider → difficult fixes had a configurable route upward. Later configuration allowed disabling provider escalation with `escalarAposReprovacoes: 0`, paired with preferred-only execution when needed. |
| v14: validate a current base | Tasks validated on an old base → merge the integration branch into the task before quick validation → tests could include recently integrated work when the update merged cleanly. |
| v16–v19: normalize the contract | Plans used synonyms such as `high` and `small` → normalize them to accepted complexity values → harmless naming differences no longer broke plan loading. |
| v16–v19: complete reservations | A template edit needed a `.ts` import outside the reservation; the orchestrator reverted it, and the build failed six times → automatically reserve the component `.ts` and existing `.spec.ts`; emit `reserva-incompleta` after repeated reversion of a file → the missing reservation became an explicit diagnosis rather than another blind fix. |
| v16–v19: wait for real capacity | Fixed waits did not match usage-reset times → parse the return time from recognized provider messages, retaining a fallback cooldown → retries could follow the reported reset instead of only a fixed interval. |
| v16–v19: unblock shared documents | Reviewers demanded a state document that was outside the task reservation, creating a deadlock → add `arquivosLivres` → tasks could update approved shared files without the scope checker undoing the work. |
| v16–v19: bound review and execution | Fixes triggered repeated broad review, and fallback execution consumed reviewer capacity → add incremental correction review, configurable `revisorProvider`, `execucaoSoNoPreferido`, and “fix ONLY the points listed by the reviewer” → executor and reviewer responsibilities became clearer. |
| Review practice | Vague or piecemeal review caused repeated negotiation → require every blocker at once with file:line, defect, scenario, EXACT before/after correction, and verification → fewer rework cycles were observed. |
| Pipeline practice | Unbounded overlap made supervision harder → initially cap the pipeline at two tasks: implement A, review A while implementing B, then apply only requested fixes → execution and review could overlap with bounded work in progress. |
| Machine limits | CPU contention made tests unstable under parallel load → later run one task at a time → avoiding flaky retries cost less than the parallel speed gain. |
| Test feedback outside the sandbox | The agent could not publish the application from its sandbox and lacked page state on failure → attach a Playwright accessibility snapshot and run an external test attendant using `.orq-teste/pedido.txt` → `.orq-teste/resultado.txt` → agents could request focused specs and receive useful diagnostics. These were application-side tools. |
| v20: free-file globs and test contracts | UI wording changed while tests still expected the old text → free files accepted globs such as `tests/e2e/**`; application validation detected old expected text with file:line → the same task could repair affected tests, and stale expectations were identified in seconds. |
| v21: preserve the error | A 1,500-character validation tail and a snapshot printed after the error cut off the actual failure → expand the tail to 6,000 characters and put the error after the snapshot → the executor received the diagnosis instead of correcting blindly. |
| 1.0.0.0 — 2026-11-20 | Internal tooling needed a public identity and usable reference material → DevLine AI kept the `orq` command, adopted MIT licensing, generic examples, version metadata, and English/Portuguese documentation → the repository was prepared for its first public release. |

The snapshot collector, test attendant, and old-text detector were integrations in the application being developed. They illustrate how to use validation hooks; they are not bundled features of the orchestrator. See [Methodology](METHODOLOGY.md) for the working rules and eight lessons that came out of them.

## Observations through 2026-11-20

| Measure | Recorded observation |
|---|---|
| Planned tasks | 101 |
| Integrated tasks | 71+ |
| Agent calls | Approximately 550 |
| Early quick-validation rejections | 112 |
| Early out-of-reservation changes reverted | 49 |
| Later average time to integrate a task | Approximately 34 minutes, one task at a time, with few correction cycles |

These numbers describe one evolving project and changing internal builds. They are not a controlled before/after experiment, a general benchmark, or a guarantee. The early failure counts and the later average refer to different stages of development; no numerical improvement rate is inferred from them.
