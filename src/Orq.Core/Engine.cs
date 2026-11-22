using System.Text.RegularExpressions;

namespace Orq.Core;

public sealed record RunSummary(int Done, int Interrupted, int Failed, int Blocked, int Pending, int Calls, double CostUsd, long Tokens)
{
    public bool AllDone => Interrupted + Failed + Blocked + Pending == 0;
}

/// <summary>
/// Motor do orquestrador. Roda fora das sessões dos agentes: agenda tarefas, reserva arquivos,
/// troca de provedor em falhas, grava checkpoints e integra o resultado.
/// </summary>
public sealed class Engine
{
    private sealed record CallResult(string Provider, IProvider Impl, ProviderResult Result);

    private readonly string _repo;
    private readonly OrqConfig _cfg;
    private readonly Store _store;
    private readonly IReadOnlyDictionary<string, IProvider> _providers;
    private readonly Action<string> _log;
    private readonly Git _git = new();
    private readonly string _orqDir;
    private readonly string _intWt;
    private readonly SemaphoreSlim _gitLock = new(1, 1);
    private readonly SemaphoreSlim _integrateLock = new(1, 1);
    private readonly Dictionary<string, SemaphoreSlim> _slots;
    private readonly object _sync = new();
    private readonly HashSet<string> _parked = new();
    private readonly HashSet<string>? _only;
    private readonly string _gitLockFile;
    private int _calls;
    private double _cost;
    private long _tokens;

    /// <param name="only">Frente de trabalho: se informado, este processo só executa essas tarefas (as demais são de outra frente).</param>
    public Engine(string repo, OrqConfig cfg, Store store, IReadOnlyDictionary<string, IProvider> providers, Guard guard,
        Action<string>? log = null, IEnumerable<string>? only = null)
    {
        _only = only == null ? null : new HashSet<string>(only, StringComparer.OrdinalIgnoreCase);
        _repo = Path.GetFullPath(repo);
        guard.EnsureAllowed(_repo);
        if (!Directory.Exists(Path.Combine(_repo, ".git")))
            throw new InvalidOperationException($"{_repo} não é a raiz de um repositório git.");
        _cfg = cfg;
        Prompts.ArquivosLivres = cfg.ArquivosLivres;
        _store = store;
        _providers = providers;
        _log = log ?? (_ => { });
        _orqDir = Path.Combine(_repo, ".orq");
        _intWt = Path.Combine(_orqDir, "worktrees", "_integration");
        _gitLockFile = Path.Combine(_orqDir, "git.lock");
        _slots = providers.Keys.ToDictionary(n => n, n => new SemaphoreSlim(Math.Max(1, cfg.For(n).MaxConcurrent)));
    }

    public static string OrqDir(string repo) => Path.Combine(Path.GetFullPath(repo), ".orq");
    public static string DbPath(string repo) => Path.Combine(OrqDir(repo), "orq.db");

    private void Log(string? taskId, string kind, string msg)
    {
        _store.Event(taskId, kind, msg);
        _log($"[{DateTime.Now:HH:mm:ss}] {(taskId == null ? "" : taskId + " ")}{Redactor.Head(msg, 300)}");
    }

    // ------------------------------------------------------------------ laço principal

    public TimeSpan DependencyPoll { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Atraso de reconexão injetável para testar sem esperar minutos reais.</summary>
    public Func<TimeSpan, CancellationToken, Task> NetworkDelay { get; init; } = Task.Delay;

    private bool Mine(TaskItem t) => _only == null || _only.Contains(t.Id);

    /// <summary>Faixas diferentes podem rodar juntas; sem faixa, vale a reserva de arquivos.</summary>
    public static bool WritesConflict(TaskItem a, TaskItem b) =>
        a.Lane != null && b.Lane != null
            ? string.Equals(a.Lane, b.Lane, StringComparison.OrdinalIgnoreCase)
            : Scope.Conflict(a.Files, b.Files);

    public async Task<RunSummary> RunAsync(CancellationToken ct)
    {
        Recover();
        using (await FileLock.AcquireAsync(_gitLockFile, ct)) await EnsureIntegrationAsync();

        var running = new Dictionary<string, (TaskItem Task, Task Work)>();
        string? waitingNote = null;
        while (true)
        {
            if (!ct.IsCancellationRequested)
            {
                var all = _store.All();
                var done = all.Where(t => t.State == TaskState.Done).Select(t => t.Id).ToHashSet();
                foreach (var t in all.Where(Mine))
                {
                    if (running.Count >= Math.Max(1, _cfg.MaxConcurrency)) break;
                    if (t.State is not (TaskState.Pending or TaskState.Interrupted)) continue;
                    if (running.ContainsKey(t.Id) || _parked.Contains(t.Id)) continue;
                    if (!t.DependsOn.All(done.Contains)) continue;
                    var writes = !Profiles.For(t.Role).ReadOnly;
                    if (writes && running.Values.Any(r => !Profiles.For(r.Task.Role).ReadOnly && WritesConflict(t, r.Task)))
                        continue; // arquivo reservado por outro agente em execução
                    t.State = TaskState.Running;
                    t.NextAction = null;
                    t.RunnerPid = Environment.ProcessId;
                    _store.Save(t);
                    Log(t.Id, "start", $"iniciada ({Profiles.For(t.Role).Name}, {t.Complexity}{(t.Provider == null ? "" : ", preferência " + t.Provider)})");
                    running[t.Id] = (t, Task.Run(() => RunTaskSafe(t, ct)));
                }

                if (running.Count == 0)
                {
                    // Tarefas desta frente que dependem de outra frente: espera em vez de encerrar.
                    var byId = all.ToDictionary(t => t.Id);
                    var waiting = all.Where(Mine)
                        .Where(t => t.State is TaskState.Pending or TaskState.Interrupted && !_parked.Contains(t.Id))
                        .Where(t => t.DependsOn.Any(d => !done.Contains(d) && byId.TryGetValue(d, out var dep) && !Mine(dep)))
                        .Where(t => t.DependsOn.All(d => byId.TryGetValue(d, out var dep) && dep.State is not (TaskState.Failed or TaskState.Blocked)))
                        .ToList();
                    if (waiting.Count > 0)
                    {
                        var note = string.Join(", ", waiting.Select(t => $"{t.Id}←{string.Join("+", t.DependsOn.Where(d => !done.Contains(d)))}"));
                        if (note != waitingNote) Log(null, "wait", "aguardando outra frente: " + note);
                        waitingNote = note;
                        try { await Task.Delay(DependencyPoll, ct); } catch (OperationCanceledException) { }
                        continue;
                    }
                }
            }
            if (running.Count == 0) break;
            // Acorda também periodicamente: uma tarefa reaberta (orq retry) entra sem esperar outra terminar.
            var tick = ct.IsCancellationRequested ? Task.CompletedTask : Task.Delay(DependencyPoll, ct).ContinueWith(_ => { });
            await Task.WhenAny(running.Values.Select(r => r.Work).Append(ct.IsCancellationRequested ? Task.Delay(Timeout.Infinite) : tick));
            foreach (var id in running.Where(r => r.Value.Work.IsCompleted).Select(r => r.Key).ToList())
                running.Remove(id);
        }

        var final = _store.All().Where(Mine).ToList();
        int C(TaskState s) => final.Count(t => t.State == s);
        return new RunSummary(C(TaskState.Done), C(TaskState.Interrupted), C(TaskState.Failed), C(TaskState.Blocked),
            C(TaskState.Pending), _calls, _cost, _tokens);
    }

    /// <summary>Recuperação após reinício: o que estava "em execução" volta à fila; concluídas não são tocadas.</summary>
    private void Recover()
    {
        var n = 0;
        var mine = _store.All().Where(Mine).ToList();
        foreach (var t in mine.Where(t => t.State == TaskState.Running))
        {
            if (RunnerAlive(t.RunnerPid))
            {
                Log(t.Id, "recover", $"em execução em outra frente (processo {t.RunnerPid}); não recuperada");
                continue;
            }
            t.State = TaskState.Interrupted;
            t.NextAction = "recuperada após reinício do orquestrador";
            _store.Save(t);
            n++;
        }
        var open = _store.CloseOpenAttempts(_only == null ? null : mine.Select(t => t.Id));
        if (n > 0 || open > 0) Log(null, "recover", $"{n} tarefa(s) recuperada(s) após reinício");
    }

    /// <summary>Outro processo vivo, diferente deste, que se registrou como executor da tarefa.</summary>
    public static bool RunnerAlive(int? pid)
    {
        if (pid == null || pid == Environment.ProcessId) return false;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid.Value);
            return !p.HasExited && string.Equals(p.ProcessName, System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private async Task RunTaskSafe(TaskItem t, CancellationToken ct)
    {
        try
        {
            if (Profiles.For(t.Role).ReadOnly) await RunAnalysis(t, ct);
            else await RunWork(t, ct);
        }
        catch (OperationCanceledException)
        {
            t.State = TaskState.Interrupted;
            t.NextAction = "interrompida; continue com: orq resume";
            if (t.Worktree != null && Directory.Exists(t.Worktree))
                try { await Commit(t.Worktree, $"orq: checkpoint {t.Id} (interrompida)"); } catch { }
        }
        catch (PermissionDeniedException ex)
        {
            t.State = TaskState.Blocked;
            t.NextAction = ex.Message;
        }
        catch (Exception ex)
        {
            t.State = TaskState.Failed;
            t.NextAction = "erro interno: " + Redactor.Head(ex.Message, 500);
        }
        _store.Save(t);
        Log(t.Id, "end", $"{t.State}{(t.NextAction == null ? "" : " — " + t.NextAction)}");
    }

    // ------------------------------------------------------------------ tarefas de análise (somente leitura)

    private async Task RunAnalysis(TaskItem t, CancellationToken ct)
    {
        var deps = Deps(t);
        while (t.Attempts < _cfg.MaxAttemptsPerTask)
        {
            t.Attempts++;
            _store.Save(t);
            var call = await Call(t, t.Role, (p, resumed) => Prompts.Analysis(t, deps, _cfg.Context) + SkillsSection(t, p.Name, false), _intWt, true, null, ct);
            if (call == null) { t.Attempts--; Park(t); return; }
            if (call.Result.Outcome != Outcome.Success || string.IsNullOrWhiteSpace(call.Result.Text))
            {
                t.Feedback = "A resposta anterior falhou: " + (call.Result.Error ?? "vazia");
                continue;
            }
            var faltam = MissingSkills(t, call.Result.Text);
            if (faltam.Count > 0)
            {
                t.Feedback = "A análise não declarou o uso das skills obrigatórias: " + string.Join(", ", faltam) +
                             ". Leia o SKILL.md de cada uma, aplique-as na análise e declare no JSON final.";
                Log(t.Id, "skills", "skills não declaradas: " + string.Join(", ", faltam));
                continue;
            }
            t.Owner = call.Provider;
            t.Result = Redactor.Head(call.Result.Text, 8000);
            Absorb(t, call.Result.Text);
            t.Summary ??= Redactor.Head(t.Result, 800);
            var artifacts = Path.Combine(_orqDir, "artifacts");
            Directory.CreateDirectory(artifacts);
            await File.WriteAllTextAsync(Path.Combine(artifacts, Safe(t.Id) + ".md"), t.Result, ct);
            t.Feedback = null;
            t.State = TaskState.Done;
            return;
        }
        Fail(t, "tentativas esgotadas na análise");
    }

    // ------------------------------------------------------------------ tarefas de escrita (dev → teste → revisão → integração)

    private async Task RunWork(TaskItem t, CancellationToken ct)
    {
        var wt = await EnsureTaskWorktree(t);
        var deps = Deps(t);
        var reviewFailures = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            switch (t.Phase)
            {
                case Phase.Dev:
                {
                    if (t.Attempts >= _cfg.MaxAttemptsPerTask) { Fail(t, "tentativas esgotadas: " + Redactor.Head(t.Feedback, 300)); return; }
                    if (Travas.CompletarReserva(t, _repo)) { Log(t.Id, "reserva", "reserva completada com o .ts/.spec.ts dos componentes da tarefa"); _store.Save(t); }
                    t.Attempts++;
                    _store.Save(t);
                    var call = await Call(t, t.Role, (p, resumed) => Prompts.Dev(t, deps, p, resumed, _cfg.Context) + SkillsSection(t, p.Name, resumed), wt, false, null, ct);
                    if (call == null) { t.Attempts--; Park(t); return; }   // ninguém atendeu (limite/indisponível): não conta tentativa
                    if (call.Result.Outcome != Outcome.Success)
                    {
                        t.Feedback = "A execução anterior falhou: " + Redactor.Head(call.Result.Error, 400);
                        _store.Save(t);
                        continue;
                    }
                    var faltam = call.Impl.CanEditFiles ? MissingSkills(t, call.Result.Text) : new List<string>();
                    if (faltam.Count > 0)
                    {
                        // O código fica salvo; o agente volta para aplicar e declarar as skills que faltaram.
                        await ScopeCheck(t, wt);
                        await Commit(wt, $"orq: {t.Id} tentativa {t.Attempts} ({call.Provider}, skills pendentes)");
                        t.Feedback = "Você não declarou o uso das skills obrigatórias: " + string.Join(", ", faltam) +
                                     ". Leia o SKILL.md de cada uma, revise o que fez à luz delas, corrija o que estiver fora e declare no JSON final.";
                        Log(t.Id, "skills", "skills não declaradas: " + string.Join(", ", faltam));
                        _store.Save(t);
                        continue;
                    }
                    if (!call.Impl.CanEditFiles)
                    {
                        var full = Guard.EnsureInside(wt, t.Files[0]);
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        await File.WriteAllTextAsync(full, StripFences(call.Result.Text), ct);
                        t.Summary = $"{t.Files[0]} gerado por {call.Provider}";
                    }
                    else Absorb(t, call.Result.Text);

                    await ScopeCheck(t, wt);
                    await Commit(wt, $"orq: {t.Id} tentativa {t.Attempts} ({call.Provider})");
                    if (!await HasWork(wt))
                    {
                        t.Feedback = "Nenhuma alteração foi feita nos arquivos reservados. Implemente a tarefa editando os arquivos.";
                        _store.Save(t);
                        continue;
                    }
                    t.Owner = call.Provider;
                    t.Handoff = null;
                    t.Feedback = null;
                    t.Phase = Phase.Test;
                    _store.Save(t);
                    break;
                }
                case Phase.Test:
                {
                    // Antes de validar, traz o que já foi integrado (se juntar limpo): a tarefa não valida numa base velha.
                    using (await FileLock.AcquireAsync(_gitLockFile, ct))
                    {
                        var atual = await _git.Run(wt, "merge", "--no-edit", _cfg.IntegrationBranch);
                        if (!atual.Ok) { await _git.Run(wt, "merge", "--abort"); Log(t.Id, "test", "não deu para atualizar com a integração antes do teste (conflito); o merge resolve depois"); }
                    }
                    // Com validação rápida configurada, a fase de teste roda só a rápida; a completa roda no merge (Integrate).
                    var rapida = !string.IsNullOrWhiteSpace(_cfg.QuickTestCommand);
                    var (ok, output) = rapida ? await RunTests(wt, ct, _cfg.QuickTestCommand) : await RunTests(wt, ct);
                    if (rapida) Log(t.Id, "test", ok ? "validação rápida passou" : "validação rápida falhou");
                    t.Tests.Add(output);
                    if (ok) t.Phase = Phase.Review;
                    else
                    {
                        t.Feedback = "Testes falharam:\n" + output;
                        t.Phase = Phase.Dev;
                        Log(t.Id, "test", "testes falharam; devolvida ao desenvolvimento");
                    }
                    _store.Save(t);
                    break;
                }
                case Phase.Review:
                {
                    if (!t.Review) { t.Phase = Phase.Integrate; _store.Save(t); break; }
                    var diff = (await _git.Run(wt, "diff", _cfg.IntegrationBranch + "...HEAD")).Stdout;
                    // 1) Auto-revisão do executor (uma por ciclo), antes da revisão oficial.
                    var auto = _cfg.SelfReviewProvider;
                    if (!t.SelfReviewDone && !string.IsNullOrWhiteSpace(auto) && _providers.ContainsKey(auto!) && !Router.Escalada(t, _cfg))
                    {
                        t.SelfReviewDone = true;
                        var self = await Call(t, Role.Reviewer, (p, _) => Prompts.SelfReview(t, Redactor.Head(diff, 30000), _cfg.Context), wt, true, null, ct, new[] { auto! });
                        if (t.State == TaskState.Interrupted) { t.SelfReviewDone = false; Park(t); return; }
                        if (self != null && self.Result.Outcome == Outcome.Success)
                        {
                            var sv = Json.LastObject(self.Result.Text);
                            var sverd = sv?.Str("verdict") ?? (Regex.IsMatch(self.Result.Text, "(?i)approve|aprovad") ? "approve" : "changes");
                            if (!sverd.StartsWith("approve", StringComparison.OrdinalIgnoreCase))
                            {
                                t.Feedback = "Sua AUTO-REVISÃO encontrou estes problemas; corrija antes da revisão oficial:" + "\n" + (sv?.Str("notes") ?? Redactor.Head(self.Result.Text, 800));
                                t.Phase = Phase.Dev;
                                Log(t.Id, "autorrevisao", "auto-revisão do executor encontrou problemas; corrigindo antes do Opus");
                                _store.Save(t);
                                break;
                            }
                            Log(t.Id, "autorrevisao", "auto-revisão do executor aprovou; seguindo para a revisão oficial");
                        }
                        _store.Save(t);
                    }
                    // 2) Revisão oficial: incremental se a correção só tocou arquivos já revisados.
                    var head = await _git.Ok(wt, "rev-parse", "HEAD");
                    string? diffCorrecao = null;
                    if (t.LastReviewedCommit != null && t.LastReviewNotes != null)
                    {
                        var arqs = (await _git.Run(wt, "diff", "--name-only", t.LastReviewedCommit + "..HEAD")).Stdout
                            .Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        if (arqs.Count > 0 && arqs.Count <= _cfg.RevisaoIncrementalMaxArquivos && arqs.All(a => t.LastReviewFiles.Contains(a)))
                            diffCorrecao = (await _git.Run(wt, "diff", t.LastReviewedCommit + "..HEAD")).Stdout;
                    }
                    if (diffCorrecao != null) Log(t.Id, "review", "revisão incremental: só a correção");
                    var call = await Call(t, Role.Reviewer, (p, _) => (diffCorrecao != null ? Prompts.ReviewFix(t, Redactor.Head(diffCorrecao, 30000), t.LastReviewNotes!, _cfg.Context) : Prompts.Review(t, Redactor.Head(diff, 30000), _cfg.Context)) +
                        (t.Skills.Count == 0 ? "" : SkillsSection(t, p.Name, false, review: true) +
                            "\nNa revisão, confira também se o resultado segue as skills obrigatórias e o briefing; peça mudanças concretas onde não seguir."),
                        wt, true, t.Owner, ct);
                    if (call == null) { t.NextAction = "revisão pendente: " + t.NextAction; Park(t); return; }
                    if (call.Result.Outcome != Outcome.Success)
                    {
                        if (++reviewFailures >= 2) { t.NextAction = "revisão falhou duas vezes: " + call.Result.Error; Park(t); return; }
                        continue;
                    }
                    var verdict = Json.LastObject(call.Result.Text);
                    var v = verdict?.Str("verdict") ?? (Regex.IsMatch(call.Result.Text, "(?i)approve|aprovad") ? "approve" : "changes");
                    var notes = verdict?.Str("notes") ?? Redactor.Head(call.Result.Text, 800);
                    if (call.Provider == t.Owner) t.Decisions.Add("revisão feita pelo mesmo provedor do desenvolvimento (único disponível)");
                    if (v.StartsWith("approve", StringComparison.OrdinalIgnoreCase))
                    {
                        t.Tests.Add($"revisão aprovada por {call.Provider}");
                        t.Phase = Phase.Integrate;
                        t.LastReviewedCommit = null; t.LastReviewNotes = null; t.LastReviewFiles.Clear();
                    }
                    else
                    {
                        t.Feedback = $"O revisor ({call.Provider}) pediu mudanças:\n{notes}";
                        t.Phase = Phase.Dev;
                        t.ReviewRejections++;
                        t.SelfReviewDone = false;
                        t.LastReviewedCommit = head;
                        t.LastReviewNotes = notes;
                        t.LastReviewFiles = (await _git.Run(wt, "diff", "--name-only", _cfg.IntegrationBranch + "...HEAD")).Stdout
                            .Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        Log(t.Id, "review", "revisor pediu mudanças");
                        if (Router.EscaladaDeModelo(t, _cfg))
                            Log(t.Id, "escalada", $"{t.ReviewRejections}ª reprovação da revisão: a correção continua no mesmo provedor com o modelo de escalada");
                        if (Router.Escalada(t, _cfg))
                            Log(t.Id, "escalada", $"{t.ReviewRejections}ª reprovação da revisão: a correção vai para {_cfg.EscalationProvider} (modelo de escalada)");
                    }
                    _store.Save(t);
                    break;
                }
                case Phase.Integrate:
                {
                    if (await Integrate(t, wt, ct)) return;
                    break;
                }
            }
        }
    }

    /// <summary>Integrador determinístico (sem tokens). Retorna true quando a tarefa chegou a um estado final.</summary>
    private static List<string> Linhas(string texto) =>
        texto.Split(new[] { (char)10, (char)13 }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private async Task<bool> Integrate(TaskItem t, string wt, CancellationToken ct)
    {
        // A trava entre processos (git.lock) protege só as operações rápidas do git. A validação completa,
        // que demora, roda FORA dela: assim as outras frentes continuam criando worktrees e trabalhando.
        // Se a integração andar durante a validação, atualiza de novo e valida outra vez antes do merge.
        await _integrateLock.WaitAsync(ct);
        try
        {
            for (var rodada = 1; rodada <= 3; rodada++)
            {
                string before, intHead;
                using (await FileLock.AcquireAsync(_gitLockFile, ct))
                {
                    before = await _git.Ok(wt, "rev-parse", "HEAD");
                    var up = await _git.Run(wt, "merge", "--no-edit", _cfg.IntegrationBranch);
                    if (!up.Ok)
                    {
                        await _git.Run(wt, "merge", "--abort");
                        t.State = TaskState.Blocked;
                        t.NextAction = $"conflito ao atualizar {t.Branch} com {_cfg.IntegrationBranch}; resolva manualmente em {wt}";
                        return true;
                    }
                    intHead = await _git.Ok(_intWt, "rev-parse", "HEAD");
                }
                var completaNoMerge = !string.IsNullOrWhiteSpace(_cfg.QuickTestCommand);
                if (completaNoMerge || await _git.Ok(wt, "rev-parse", "HEAD") != before)
                {
                    if (completaNoMerge) Log(t.Id, "test", rodada == 1 ? "validação completa no merge" : $"validação completa no merge (rodada {rodada}: a integração andou)");
                    var (ok, output) = await RunTests(wt, ct);
                    t.Tests.Add("merge: " + output);
                    if (!ok)
                    {
                        t.Feedback = "A validação completa no merge (sistema inteiro, já com o trabalho das outras tarefas) falhou:\n" + output;
                        Log(t.Id, "test", "validação completa no merge falhou; devolvida ao desenvolvimento");
                        t.Phase = Phase.Dev;
                        _store.Save(t);
                        return false;
                    }
                }
                using (await FileLock.AcquireAsync(_gitLockFile, ct))
                {
                    var intAgora = await _git.Ok(_intWt, "rev-parse", "HEAD");
                    if (intAgora != intHead)
                    {
                        // Outra tarefa entrou durante a validação. Só revalida se ela mexeu em algum arquivo desta
                        // tarefa; sem sobreposição, os dois trabalhos não interagem e o merge segue (o próximo merge
                        // e a jornada final validam o sistema inteiro de novo).
                        var entrou = Linhas((await _git.Run(_intWt, "diff", "--name-only", intHead + ".." + intAgora)).Stdout);
                        var meus = Linhas((await _git.Run(wt, "diff", "--name-only", intHead + "...HEAD")).Stdout);
                        var comuns = entrou.Intersect(meus).ToList();
                        if (comuns.Count > 0)
                        {
                            Log(t.Id, "integrate", $"a integração recebeu outra tarefa que mexe nos mesmos arquivos ({string.Join(", ", comuns.Take(3))}); atualizando e validando de novo");
                            continue;
                        }
                        var junta = await _git.Run(wt, "merge", "--no-edit", _cfg.IntegrationBranch);
                        if (!junta.Ok)
                        {
                            await _git.Run(wt, "merge", "--abort");
                            Log(t.Id, "integrate", "a integração andou e não junta limpo; validando de novo");
                            continue;
                        }
                        Log(t.Id, "integrate", "a integração recebeu outra tarefa sem arquivos em comum; segue para o merge sem revalidar");
                    }
                    var merge = await _git.Run(_intWt, "merge", "--no-ff", "-m", $"orq: integra {t.Id} - {t.Title}", t.Branch!);
                    if (!merge.Ok)
                    {
                        await _git.Run(_intWt, "merge", "--abort");
                        t.State = TaskState.Blocked;
                        t.NextAction = "falha ao integrar: " + Redactor.Tail(merge.All, 300);
                        return true;
                    }
                    t.State = TaskState.Done;
                    t.NextAction = null;
                    t.Feedback = null;
                    Log(t.Id, "integrate", $"integrada em {_cfg.IntegrationBranch}");
                    return true;
                }
            }
            t.NextAction = "integração muito disputada: três rodadas de validação foram superadas por outras tarefas; tenta de novo";
            _store.Save(t);
            return false;
        }
        finally { _integrateLock.Release(); }
    }

    // ------------------------------------------------------------------ chamada com troca de provedor

    private async Task<CallResult?> Call(TaskItem t, Role role, Func<IProvider, bool, string> prompt, string cwd, bool readOnly,
        string? avoid, CancellationToken ct, IEnumerable<string>? forcados = null)
    {
        string? reason = null;
        foreach (var name in forcados?.Where(_providers.ContainsKey).ToList() ?? Router.Candidates(t, role, avoid, _cfg, _providers.Keys))
        {
            var p = _providers[name];
            // O modelo vem da complexidade (ou da revisão); o limite de uso é controlado por provedor+modelo.
            var model = _cfg.For(name).ModelFor(role, t.Complexity, Router.Escalada(t, _cfg) || Router.EscaladaDeModelo(t, _cfg));
            var label = model == null ? name : $"{name}:{model}";
            var st = _store.GetProvider(label);
            if (st?.CooldownUntil is { } until && until > DateTime.UtcNow)
            {
                reason = $"{label} em espera até {until.ToLocalTime():dd/MM HH:mm} ({st.LastOutcome})";
                continue;
            }
            var semSkill = t.Skills.Where(s => !OrqConfig.SkillInstalled(_cfg.SkillDir(name, s))).ToList();
            if (semSkill.Count > 0)
            {
                reason = $"{name} sem as skills instaladas: {string.Join(", ", semSkill)}";
                Log(t.Id, "skills", reason);
                continue;
            }
            if (!BudgetAllows(out var why)) { t.NextAction = why; return null; }

            await _slots[name].WaitAsync(ct);
            try
            {
                var networkRetries = 0;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!BudgetAllows(out why)) { t.NextAction = why; return null; }
                    var key = $"{name}:{role}";
                    t.Sessions.TryGetValue(key, out var sid);
                    var text = prompt(p, sid != null);
                    var req = new AgentRequest
                    {
                        TaskId = t.Id, Role = role, Prompt = text, Cwd = cwd, ReadOnly = readOnly, Files = t.Files,
                        ResumeSessionId = sid, Timeout = TimeSpan.FromMinutes(_cfg.TaskTimeoutMinutes), Model = model,
                        ReadDirs = t.Skills.Select(s => _cfg.SkillDir(name, s)!).Append(CapturasDir(t)).ToList(),
                    };
                    var aid = _store.StartAttempt(t.Id, role, label);
                    Log(t.Id, "call", $"{Profiles.For(role).Name} → {label}{(sid != null ? " (sessão retomada)" : "")}");
                    ProviderResult r;
                    try { r = await p.RunAsync(req, ct); }
                    catch (OperationCanceledException) { _store.EndAttempt(aid, "Interrupted", null, sid, null); throw; }
                    catch (Exception ex)
                    {
                        var outcome = Classifier.Classify(ex.Message);
                        r = new ProviderResult { Outcome = outcome == Outcome.Failed ? Outcome.Unavailable : outcome, Error = ex.Message };
                    }

                    if (r.Outcome is not (Outcome.Success or Outcome.RateLimited or Outcome.AuthError))
                    {
                        var classified = Classifier.Classify(r.Error);
                        if (classified is Outcome.NetworkError or Outcome.RateLimited or Outcome.AuthError) r.Outcome = classified;
                    }

                    if (r.Usage.Kind == UsageKind.Unknown && r.Outcome == Outcome.Success)
                        r.Usage = new Usage { Input = text.Length / 4, Output = r.Text.Length / 4, Kind = UsageKind.Estimated };
                    lock (_sync)
                    {
                        _calls++;
                        _cost += r.Usage.CostUsd ?? 0;
                        _tokens += r.Usage.Input + r.Usage.Output;
                    }
                    _store.EndAttempt(aid, r.Outcome.ToString(), r.Usage, r.SessionId, r.Error);

                    if (r.Outcome == Outcome.Success)
                    {
                        if (r.SessionId != null) t.Sessions[key] = r.SessionId;
                        _store.SetProvider(label, null, "Success", null);
                        return new CallResult(name, p, r);
                    }
                    if (r.Outcome == Outcome.Failed)
                    {
                        t.Sessions.Remove(key); // sessão possivelmente inválida: a próxima tentativa recomeça com o contrato completo
                        return new CallResult(name, p, r);
                    }

                    // Checkpoint + handoff; rede repete a sessão, os demais seguem o fluxo de failover.
                    var minutes = r.Outcome switch
                    {
                        Outcome.RateLimited => _cfg.CooldownMinutes,
                        Outcome.AuthError => 24 * 60,
                        Outcome.Unavailable => 5,
                        _ => 0,
                    };
                    var ate = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : (DateTime?)null;
                    if (r.Outcome == Outcome.RateLimited && Travas.VoltaDoLimite(r.Error, DateTime.Now) is { } volta)
                        ate = volta.ToUniversalTime();
                    _store.SetProvider(label, ate, r.Outcome.ToString(), r.Error);
                    if (r.SessionId != null) t.Sessions[key] = r.SessionId;
                    var stat = "";
                    if (!readOnly)
                    {
                        await ScopeCheck(t, cwd);
                        await Commit(cwd, $"orq: checkpoint {t.Id} ({name} interrompido: {r.Outcome})");
                        stat = (await _git.Run(cwd, "diff", "--stat", _cfg.IntegrationBranch + "...HEAD")).Stdout;
                    }
                    t.Handoff = Prompts.Handoff(t, name, r.Outcome, r.Text, stat);
                    _store.Save(t);
                    if (r.Outcome == Outcome.NetworkError)
                    {
                        if (networkRetries >= Math.Max(0, _cfg.RedeMaxTentativas))
                        {
                            t.State = TaskState.Interrupted;
                            t.NextAction = $"falha de rede: {_cfg.RedeMaxTentativas} tentativa(s) de reconexão esgotada(s); continue com: orq resume";
                            Log(t.Id, "rede", t.NextAction);
                            return null;
                        }
                        var minutesWait = Math.Min(8, Math.Max(1, _cfg.RedeEsperaInicialMinutos) * Math.Pow(2, Math.Min(networkRetries, 3)));
                        networkRetries++;
                        t.NextAction = $"rede indisponível; aguardando {minutesWait} minuto(s) para reconexão {networkRetries}/{_cfg.RedeMaxTentativas}";
                        _store.Save(t);
                        Log(t.Id, "rede", $"{label}: {r.Error}. Checkpoint salvo; {t.NextAction}, na mesma sessão.");
                        await NetworkDelay(TimeSpan.FromMinutes(minutesWait), ct);
                        t.NextAction = null;
                        continue;
                    }
                    reason = $"{name}: {r.Outcome}";
                    Log(t.Id, "failover", $"{name} indisponível ({r.Outcome}): {r.Error}. Estado salvo; tentando outro provedor.");
                    break;
                }
            }
            finally { _slots[name].Release(); }
        }
        t.NextAction = "sem provedor disponível" + (reason == null ? "" : $" ({reason})") + "; continue depois com: orq resume";
        return null;
    }

    private bool BudgetAllows(out string why)
    {
        lock (_sync)
        {
            var l = _cfg.Limits;
            why = _calls >= l.MaxCallsPerRun ? $"limite configurado de {l.MaxCallsPerRun} chamadas por execução atingido"
                : _cost >= l.MaxCostUsdPerRun ? $"limite configurado de custo (US$ {l.MaxCostUsdPerRun}) atingido"
                : _tokens >= l.MaxTokensPerRun ? $"limite configurado de {l.MaxTokensPerRun} tokens por execução atingido"
                : "";
            return why.Length == 0;
        }
    }

    // ------------------------------------------------------------------ apoio

    private void Park(TaskItem t)
    {
        t.State = TaskState.Interrupted;
        lock (_sync) _parked.Add(t.Id);
    }

    private static void Fail(TaskItem t, string why)
    {
        t.State = TaskState.Failed;
        t.NextAction = why;
    }

    private List<TaskItem> Deps(TaskItem t) =>
        t.DependsOn.Select(id => _store.Get(id)).Where(d => d != null).Select(d => d!).ToList();

    public string CapturasDir(TaskItem t) => Path.Combine(_orqDir, "capturas", Safe(t.Id));

    /// <summary>Seção do prompt com as skills obrigatórias e o briefing, com caminhos do provedor que vai executar.</summary>
    public string SkillsSection(TaskItem t, string provider, bool resumed, bool review = false)
    {
        var sb = new System.Text.StringBuilder();
        if (t.Skills.Count > 0)
        {
            sb.AppendLine().AppendLine("# Skills obrigatórias");
            if (resumed)
                sb.AppendLine("Continue aplicando as skills " + string.Join(", ", t.Skills) + " e declare o uso de cada uma no JSON final.");
            else
            {
                sb.AppendLine("Antes de mudar qualquer coisa, leia o SKILL.md de cada skill abaixo e siga o que ele manda. Elas são obrigatórias:");
                foreach (var s in t.Skills)
                {
                    var dir = _cfg.SkillDir(provider, s);
                    sb.AppendLine($"- {s}: {Path.Combine(dir ?? "?", "SKILL.md")}");
                }
                sb.AppendLine("Notas de uso neste ambiente (sem internet):");
                sb.AppendLine("- web-design-guidelines: as regras estão em `guidelines.md`, na pasta da skill; não tente baixá-las.");
                sb.AppendLine("- ui-ux-pro-max: se você puder executar comandos, use o script de busca da skill com o caminho completo " +
                              "(antes, defina PYTHONIOENCODING=utf-8; no Windows use `python -I`). Comece por `--design-system` com " +
                              "\"SaaS admin dashboard\" e `--stack angular`. Se não puder executar comandos, leia " +
                              "`references/quick-reference.md`, `references/pro-rules.md` e os CSV de `data/` (ux-guidelines, stacks/angular).");
                sb.AppendLine("- frontend-design: use-a para a direção visual, preservando a logomarca e a identidade institucional.");
            }
            sb.AppendLine("No JSON final, inclua \"skills\": [{\"nome\":\"<skill>\",\"aplicado\":\"o que você aplicou dela, citando regra e arquivo\"}] para CADA skill obrigatória. " +
                          "A tarefa volta para você se faltar alguma.");
        }
        if (!resumed && t.Brief != null)
        {
            var path = Path.Combine(_orqDir, t.Brief);
            if (File.Exists(path)) sb.AppendLine().AppendLine("# Briefing do projeto (obrigatório)").AppendLine(File.ReadAllText(path).Trim());
        }
        var capt = CapturasDir(t);
        if (t.Skills.Count > 0 && Directory.Exists(capt))
            sb.AppendLine().AppendLine($"Capturas de tela da última validação (desktop, tablet e celular) e o relatório de acessibilidade estão em {capt}" +
                                       (review ? "; abra algumas para avaliar o resultado visual." : "; use-as para conferir o resultado visual."));
        return sb.ToString();
    }

    /// <summary>Skills obrigatórias que o agente não declarou no JSON final.</summary>
    public static List<string> MissingSkills(TaskItem t, string text)
    {
        if (t.Skills.Count == 0) return new();
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Json.LastObject(text) is { } j && j.TryGetProperty("skills", out var arr) && arr.ValueKind == System.Text.Json.JsonValueKind.Array)
            foreach (var e in arr.EnumerateArray())
            {
                var nome = e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() : e.Str("nome") ?? e.Str("name");
                var aplicado = e.ValueKind == System.Text.Json.JsonValueKind.Object ? e.Str("aplicado") ?? e.Str("applied") : "sim";
                if (!string.IsNullOrWhiteSpace(nome) && !string.IsNullOrWhiteSpace(aplicado)) declared.Add(nome.Trim());
            }
        return t.Skills.Where(s => !declared.Contains(s)).ToList();
    }

    private static void Absorb(TaskItem t, string text)
    {
        if (Json.LastObject(text) is not { } j) return;
        if (j.Str("summary") is { Length: > 0 } s) t.Summary = Redactor.Head(s, 800);
        foreach (var d in j.StrList("decisions")) if (!t.Decisions.Contains(d)) t.Decisions.Add(Redactor.Head(d, 300));
        t.Pending = j.StrList("pending").Select(p => Redactor.Head(p, 300)).ToList();
    }

    private static string StripFences(string text)
    {
        var m = Regex.Match(text, @"```[^\n]*\n(.*?)```", RegexOptions.Singleline);
        var body = m.Success ? m.Groups[1].Value : text;
        return body.TrimEnd() + "\n";
    }

    private static string Safe(string id) => Regex.Replace(id, @"[^A-Za-z0-9_\-]", "-");

    private async Task<(bool Ok, string Output)> RunTests(string wt, CancellationToken ct, string? comando = null)
    {
        var cmd = comando ?? _cfg.TestCommand;
        if (string.IsNullOrWhiteSpace(cmd)) return (true, "sem comando de testes configurado (testCommand)");
        var r = await Proc.RunShellAsync(cmd, wt, TimeSpan.FromMinutes(_cfg.TestTimeoutMinutes), ct);
        var tail = Redactor.Tail(r.All, 6000);
        return r.Ok ? (true, $"OK `{cmd}`: {Redactor.Tail(r.All, 200)}")
            : (false, $"FALHA `{cmd}` (código {r.ExitCode}{(r.TimedOut ? ", tempo esgotado" : "")}):\n{tail}");
    }

    private async Task EnsureIntegrationAsync()
    {
        Directory.CreateDirectory(Path.Combine(_orqDir, "worktrees"));
        var exclude = Path.Combine(_repo, ".git", "info", "exclude");
        Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
        if (!File.Exists(exclude) || !File.ReadAllText(exclude).Contains(".orq/"))
            File.AppendAllText(exclude, "\n.orq/\n");
        if (Directory.Exists(_intWt)) return;
        var exists = (await _git.Run(_repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + _cfg.IntegrationBranch)).Ok;
        if (exists) await _git.Ok(_repo, "worktree", "add", _intWt, _cfg.IntegrationBranch);
        else await _git.Ok(_repo, "worktree", "add", "-b", _cfg.IntegrationBranch, _intWt, _cfg.BaseBranch ?? "HEAD");
    }

    private async Task<string> EnsureTaskWorktree(TaskItem t)
    {
        await _gitLock.WaitAsync();
        try
        {
            using var crossProcess = await FileLock.AcquireAsync(_gitLockFile, CancellationToken.None);
            var wt = Path.Combine(_orqDir, "worktrees", Safe(t.Id));
            var branch = "orq/task-" + Safe(t.Id);
            if (!Directory.Exists(wt))
            {
                var exists = (await _git.Run(_repo, "rev-parse", "--verify", "--quiet", "refs/heads/" + branch)).Ok;
                if (exists) await _git.Ok(_repo, "worktree", "add", wt, branch);
                else await _git.Ok(_repo, "worktree", "add", "-b", branch, wt, _cfg.IntegrationBranch);
            }
            t.Worktree = wt;
            t.Branch = branch;
            _store.Save(t);
            return wt;
        }
        finally { _gitLock.Release(); }
    }

    private async Task<bool> HasWork(string wt) =>
        int.TryParse(await _git.Ok(wt, "rev-list", "--count", _cfg.IntegrationBranch + "..HEAD"), out var n) && n > 0;

    private async Task Commit(string wt, string message)
    {
        await _git.Ok(wt, "add", "-A");
        if (string.IsNullOrWhiteSpace((await _git.Run(wt, "status", "--porcelain")).Stdout)) return;
        await _git.Ok(wt, "commit", "-q", "-m", message);
    }

    /// <summary>Isolamento: tudo que o agente alterou fora dos arquivos reservados é desfeito e registrado.</summary>
    private async Task ScopeCheck(TaskItem t, string wt)
    {
        if (t.Files.Count == 0) return;
        var raw = (await _git.Run(wt, "status", "--porcelain", "-uall", "-z")).Stdout;
        var entries = raw.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            if (e.Length < 4) continue;
            var xy = e[..2];
            var path = e[3..];
            var renamed = xy[0] is 'R' or 'C';
            var original = renamed && i + 1 < entries.Length ? entries[++i] : null;
            if (Scope.InScope(t.Files, path) && (original == null || Scope.InScope(t.Files, original))) continue;
            if (original == null && Scope.InScope(_cfg.ArquivosLivres.Select(l => l.Replace('\\', '/')), path)) continue;

            var full = Guard.EnsureInside(wt, path);
            if (xy == "??" || xy[0] == 'A' || renamed)
            {
                if (xy != "??") await _git.Run(wt, "restore", "--staged", "--", path);
                if (File.Exists(full)) File.Delete(full);
                if (original != null) await _git.Run(wt, "restore", "--staged", "--worktree", "--source=HEAD", "--", original);
            }
            else await _git.Run(wt, "restore", "--staged", "--worktree", "--source=HEAD", "--", path);

            var v = $"alteração fora da reserva desfeita: {path}";
            t.Violations.Add(v);
            if (t.Violations.Count(x => x == v) >= 2)
            {
                Log(t.Id, "reserva-incompleta", $"reserva incompleta: a tarefa precisa editar {path}, que não está em files (desfeito {t.Violations.Count(x => x == v)}x)");
                t.Feedback = (t.Feedback ?? "") + $"\n\nATENÇÃO: sua alteração em {path} foi desfeita mais de uma vez porque o arquivo não está na sua reserva. Não tente de novo nesse arquivo; resolva dentro dos arquivos reservados ou diga no relatório final que {path} precisa entrar na reserva.";
            }
            Log(t.Id, "scope", v);
        }
    }
}
