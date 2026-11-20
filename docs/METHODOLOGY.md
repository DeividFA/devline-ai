# The pipeline method

DevLine AI uses a small-task pipeline: implement, validate, review, fix only the listed issues, validate fully, and integrate. The method developed while building a real-world SaaS (management system: .NET 8 + Angular + PostgreSQL). [Português](METODOLOGIA.md) · [README](../README.md) · [Architecture](ARCHITECTURE.md) · [History](HISTORY.md)

## Separate execution from judgment

Use a fast, inexpensive model to implement and fix. Use a stronger model to plan and review. Planning happens outside the engine: write small, detailed tasks with a defined architecture, concrete acceptance criteria, dependencies, and complete file reservations. The same executor can implement and fix; these are separate responsibilities, not necessarily three distinct models.

Reserve all affected files, including the `.ts` behind every Angular template and existing `.spec.ts` files. Automatic completion is a safeguard, not a substitute for a complete initial plan. Keep changes small enough for a reviewer to inspect as one coherent diff. Skills and a task briefing can carry specialist instructions; `.orq/contexto.md` carries shared project context.

Set the executor as the task's `provider`, choose `revisorProvider` separately, and use `execucaoSoNoPreferido: true` to preserve reviewer capacity when the executor hits a limit. To prevent provider escalation, set `escalarAposReprovacoes: 0`; model escalation is a separate setting. Available models and budgets determine the actual setup, not a fixed brand pairing.

## Keep the review actionable

Ask for all blocking issues in one pass. Each blocker needs:

1. File and line.
2. The defect in one sentence.
3. A concrete failing scenario.
4. The EXACT correction: current/new code, or numbered, unambiguous steps.
5. A test, assertion, or command proving the correction.

Style preferences and optional improvements belong in observations and must not cause rejection. If a fix needs a file outside the reservation, the reviewer must name it so the coordinator can amend the contract. The executor fixes ONLY the listed issues; it should explain an impossible instruction instead of silently redesigning the task. Review the correction diff incrementally when its scope qualifies. This reduced repeated negotiation about fixes in the development workflow; it does not replace validation.

## Match validation to the cycle

Run focused specs for the affected area on every correction with `quickTestCommand`. Run full application validation at integration with `testCommand`. Hooks belong to the target application and must preserve failing exit codes. Before the quick test, the engine attempts to incorporate the latest integration branch so validation reflects recently integrated work.

Give the agent the actual failure. Attach a Playwright accessibility snapshot taken at the failure point (the “page photo”), then print the error at the END. It is a structured view of the page, not necessarily an image. The engine keeps only the last 6,000 characters of failed validation output, so put lengthy diagnostics before the final actionable message.

In the original setup, the agent sandbox could not publish the application. A separate test attendant outside that sandbox accepted requested spec files through `.orq-teste/pedido.txt` and wrote `.orq-teste/resultado.txt`. This was an application-owned integration; DevLine AI does not ship that service, a Playwright snapshot collector, or an old-text detector. Its validation hooks can call tools you provide.

On the SECOND failure of the same test, the coordinator diagnoses it and records the exact correction. Do not keep spending attempts on incomplete diagnostics. A newly understood error becomes a written rule that same day, in the lessons-learned context passed to agents; the correction must reach the agent intact.

## Self-correction and learning loop

The engine automatically returns validation failures and review corrections to execution, then repeats validation/review until the task passes or the attempt limit is exhausted. Capacity waits and blocked conflicts can pause the cycle. The executor fixes only the flagged issues. Give it file:line, the exact prescribed correction, the page snapshot at failure, and relevant server exceptions through the review or validation hook. Full actionable diagnostics matter: the engine retains at most the last 6,000 characters of failed validation output, so print the error after verbose context.

Built-in safeguards encode lessons from actual errors: automatic `.ts`/existing `.spec.ts` reservations, incomplete-reservation detection, message-based cooldowns, integration updates before testing, and restart recovery. Detecting old text still expected in tests was another corrective rule in the original application validation, not a bundled engine feature.

The lessons file is human-curated memory. `.orq/contexto.md` goes into full execution and review prompts; resumed corrections use the existing session context and new feedback. The engine records tests, violations, attempts, and events for inspection. A person can compare tasks, identify the recurring test/file and message, and count repetitions; repeated scope violations already report counts explicitly. The person chooses which observation becomes a lesson and its exact wording. This is feedback-driven correction with human-maintained rules, not autonomous learning or self-modifying AI.

For v1.1, a planned recurring-failure report would group the same signature across tasks and show the test/file, counts, task IDs, error messages, and a suggested lesson through `orq licoes` or an equivalent interface. The person would approve, edit, or discard the suggestion; only approved text would enter the context file. Cross-task grouping and this approval workflow are roadmap items, not current commands.

## Eight lessons to carry into prompts

1. **Visible text is a test contract.** When changing a label, heading, or message, update the relevant assertions in the same task. Free test globs such as `tests/e2e/**` can permit this. A validation detector for old expected text can report file:line in seconds.
2. **Fix interaction patterns in shared helpers.** If a repeated interaction changes, update the supporting helper rather than patching just one test while others keep the old behavior.
3. **Scrollable tabs/steps need deliberate activation.** Activate with the keyboard and wait for `aria-selected` before asserting the panel; do not infer selection from a click alone.
4. **Use role locators with exact names.** Prefer the accessible role and exact accessible name over broad text matches that can hit multiple controls.
5. **Isolate test data.** Two workers sharing one database must not overwrite or consume each other's records. Give tests independent data and cleanup boundaries.
6. **Budget mobile height.** Check the combined height of headings, controls, navigation, and content on a small screen, not just horizontal overflow.
7. **Without a shell, inspect compilation dependencies.** Check imports, component declarations, inputs, and types carefully; the coordinator still owns actual build verification.
8. **Report every visible-text change.** List changed UI wording in the handoff so validation and reviewers can identify assertions affected by the change.

## Concurrency follows the machine

The initial pipeline allowed at most two tasks at once: the executor implements A; while the reviewer reviews A, the executor starts B; then the executor applies only the requested fixes. Separate roles help overlap useful work without asking the reviewer to implement.

Later, CPU contention made tests unstable. One task at a time cost less than the retries caused by parallel load, so the working setting became `--concurrency 1`. Choose concurrency using measured validation stability, not the maximum number of available models. With multiple processes, use disjoint task sets and file scopes; lane names do not create a global reservation system.

The coordinator remains responsible for task scope, exact diagnoses of repeated failures, manual conflict repair, and the decision to incorporate the local integration branch. [History](HISTORY.md) records observations and numbers from the development runs.
