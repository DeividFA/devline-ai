using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Orq.Core;

public sealed record DoctorItem(string Name, bool Ok, string Detail);

/// <summary>Diagnóstico do ambiente. Não lê arquivos de credenciais: só pergunta o status às próprias ferramentas.</summary>
public static class Doctor
{
    private static readonly TimeSpan T = TimeSpan.FromSeconds(30);

    private static async Task<ProcResult?> Try(string tool, params string[] args)
    {
        var exe = Proc.Find(tool);
        return exe == null ? null : await Proc.RunAsync(exe, args, null, null, T, CancellationToken.None);
    }

    private static string First(ProcResult r) =>
        r.All.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";

    public static async Task<List<DoctorItem>> RunAsync(OrqConfig cfg)
    {
        var items = new List<DoctorItem>();
        var ramGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;
        items.Add(new("Sistema", true, RuntimeInformation.OSDescription));
        items.Add(new("CPU", true, $"{Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "?"} — {Environment.ProcessorCount} threads"));
        items.Add(new("RAM", true, $"{ramGb:0.0} GB"));

        var sdks = await Try("dotnet", "--list-sdks");
        items.Add(new(".NET", sdks?.Ok == true,
            sdks == null ? "não encontrado" : $"runtime {Environment.Version}; SDKs: {string.Join(", ", sdks.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split(' ')[0]))}"));

        var git = await Try("git", "--version");
        items.Add(new("Git", git?.Ok == true, git == null ? "não encontrado" : First(git)));

        // Claude Code
        var cv = await Try("claude", "--version");
        if (cv == null) items.Add(new("Claude Code", false, "não encontrado no PATH"));
        else
        {
            var auth = await Try("claude", "auth", "status");
            var logged = false;
            var plan = "?";
            try
            {
                using var doc = JsonDocument.Parse(auth!.Stdout);
                logged = doc.RootElement.TryGetProperty("loggedIn", out var l) && l.ValueKind == JsonValueKind.True;
                plan = doc.RootElement.Str("subscriptionType") ?? doc.RootElement.Str("authMethod") ?? "?";
            }
            catch (JsonException) { }
            items.Add(new("Claude Code", cv.Ok && logged, $"{First(cv)} — {(logged ? "autenticado" : "NÃO autenticado (rode: claude auth login)")}, plano {plan}; saldo restante: desconhecido (sem API oficial)"));
        }

        // Codex
        var xv = await Try("codex", "--version");
        if (xv == null) items.Add(new("Codex CLI", false, "não encontrado no PATH"));
        else
        {
            var auth = await Try("codex", "login", "status");
            var logged = auth?.Ok == true && auth.All.Contains("Logged in", StringComparison.OrdinalIgnoreCase);
            var how = logged && auth!.All.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase) ? " via ChatGPT" : "";
            items.Add(new("Codex CLI", xv.Ok && logged, $"{First(xv)} — {(logged ? "autenticado" + how : "NÃO autenticado (rode: codex login)")}; saldo restante: desconhecido (sem API oficial)"));
        }

        // Ollama
        var oc = cfg.For("ollama");
        var url = (oc.Url ?? "http://localhost:11434").TrimEnd('/');
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var doc = JsonDocument.Parse(await http.GetStringAsync(url + "/api/tags"));
            var models = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.Str("name") ?? "?").ToList();
            var has = models.Any(m => string.Equals(m, oc.Model, StringComparison.OrdinalIgnoreCase));
            items.Add(new("Ollama", has, $"API em {url}; modelos: {(models.Count == 0 ? "nenhum" : string.Join(", ", models))}" +
                                         (has ? $"; em uso: {oc.Model} (local, sem custo)" : $"; modelo configurado '{oc.Model}' ausente")));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            items.Add(new("Ollama", false, $"API indisponível em {url} (abra o Ollama ou rode: ollama serve)"));
        }

        foreach (var (prov, root) in cfg.SkillRoots)
        {
            var nomes = Directory.Exists(root)
                ? Directory.GetDirectories(root).Where(d => File.Exists(Path.Combine(d, "SKILL.md"))).Select(Path.GetFileName).OrderBy(n => n).ToList()
                : new List<string?>();
            items.Add(new($"Skills {prov}", nomes.Count > 0, nomes.Count == 0 ? $"nenhuma skill em {root}" : $"{root}: {string.Join(", ", nomes)}"));
        }

        var rec = Math.Clamp(Math.Min(Environment.ProcessorCount / 4, (int)Math.Round(ramGb / 8)), 1, 4);
        items.Add(new("Concorrência", true, $"recomendada: {rec} agente(s) simultâneo(s); configurada: {cfg.MaxConcurrency}"));
        return items;
    }
}

public static class Report
{
    private static string Icon(TaskState s) => s switch
    {
        TaskState.Done => "CONCLUÍDA",
        TaskState.Running => "EM EXECUÇÃO",
        TaskState.Interrupted => "INTERROMPIDA",
        TaskState.Failed => "FALHOU",
        TaskState.Blocked => "BLOQUEADA",
        _ => "PENDENTE",
    };

    public static string Status(Store store)
    {
        var sb = new StringBuilder();
        var tasks = store.All();
        if (tasks.Count == 0) return "Nenhuma tarefa registrada. Use: orq run --plan <arquivo>\n";
        var done = tasks.Where(t => t.State == TaskState.Done).Select(t => t.Id).ToHashSet();
        foreach (var t in tasks)
        {
            var waiting = t.State == TaskState.Pending && !t.DependsOn.All(done.Contains)
                ? " (aguarda " + string.Join(",", t.DependsOn.Where(d => !done.Contains(d))) + ")" : "";
            sb.AppendLine($"{t.Id,-8} {Icon(t.State),-13} {Profiles.For(t.Role).Name,-22} resp: {t.Owner ?? "-",-7} tent: {t.Attempts}  {t.Title}{waiting}");
            if (t.Files.Count > 0 && t.State == TaskState.Running) sb.AppendLine($"         reservados: {string.Join(", ", t.Files)}");
            if (t.NextAction != null) sb.AppendLine($"         próxima ação: {t.NextAction}");
        }
        sb.AppendLine();
        foreach (var name in store.Attempts().Select(a => a.Provider).Distinct())
        {
            var st = store.GetProvider(name);
            var cd = st?.CooldownUntil > DateTime.UtcNow ? $"em espera até {st!.CooldownUntil!.Value.ToLocalTime():dd/MM HH:mm} ({st.LastOutcome})" : "disponível";
            sb.AppendLine($"provedor {name,-7} {cd}");
        }
        return sb.ToString();
    }

    public static string Usage(Store store)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| Provedor | Chamadas | Sucesso | Tokens entrada | Tokens saída | Custo informado | Origem do uso | Saldo restante |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var g in store.Attempts().GroupBy(a => a.Provider))
        {
            var kinds = g.Where(a => a.Outcome == "Success").Select(a => a.UsageKind).Distinct().ToList();
            var kind = kinds.Count == 0 ? "desconhecido" : kinds.Contains("Estimated") ? "estimado" : kinds.All(k => k == "Known") ? "informado pelo provedor" : "misto";
            var cost = g.Any(a => a.CostUsd != null) ? "US$ " + g.Sum(a => a.CostUsd ?? 0).ToString("0.0000") : "não informado";
            sb.AppendLine($"| {g.Key} | {g.Count()} | {g.Count(a => a.Outcome == "Success")} | {g.Sum(a => a.InputTokens)} | {g.Sum(a => a.OutputTokens)} | {cost} | {kind} | desconhecido |");
        }
        return sb.ToString();
    }

    public static string Full(Store store, OrqConfig cfg, string repo)
    {
        var sb = new StringBuilder();
        var tasks = store.All();
        sb.AppendLine($"# Relatório do orquestrador — {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine().AppendLine($"Repositório: `{repo}`  ");
        sb.AppendLine($"Tarefas: {tasks.Count(t => t.State == TaskState.Done)}/{tasks.Count} concluídas.").AppendLine();
        sb.AppendLine("## Tarefas");
        foreach (var t in tasks)
        {
            sb.AppendLine().AppendLine($"### {t.Id} — {t.Title} [{Icon(t.State)}]");
            sb.AppendLine($"- Perfil: {Profiles.For(t.Role).Name}; complexidade: {t.Complexity}; responsável: {t.Owner ?? "-"}; tentativas: {t.Attempts}");
            sb.AppendLine($"- Objetivo: {t.Objective}");
            if (t.Acceptance.Count > 0) sb.AppendLine("- Critérios de aceite: " + string.Join("; ", t.Acceptance));
            if (t.Files.Count > 0) sb.AppendLine("- Arquivos reservados: " + string.Join(", ", t.Files));
            if (t.DependsOn.Count > 0) sb.AppendLine("- Dependências: " + string.Join(", ", t.DependsOn));
            if (t.Summary != null) sb.AppendLine("- Resultado: " + t.Summary);
            if (t.Decisions.Count > 0) sb.AppendLine("- Decisões técnicas: " + string.Join("; ", t.Decisions));
            if (t.Tests.Count > 0) sb.AppendLine("- Testes/validações: " + string.Join(" | ", t.Tests.Select(x => x.Replace("\r", "").Replace("\n", " "))));
            if (t.Violations.Count > 0) sb.AppendLine("- Isolamento: " + string.Join("; ", t.Violations));
            if (t.Pending.Count > 0) sb.AppendLine("- Pendências: " + string.Join("; ", t.Pending));
            if (t.NextAction != null) sb.AppendLine("- Próxima ação: " + t.NextAction);
            if (t.Branch != null) sb.AppendLine($"- Branch: `{t.Branch}`");
        }
        sb.AppendLine().AppendLine("## Uso por provedor").AppendLine().AppendLine(Usage(store));
        sb.AppendLine("O saldo restante das assinaturas não é exposto por interface oficial automatizável; consulte `/usage` (Claude) e `/status` (Codex).");
        sb.AppendLine().AppendLine("## Integração");
        sb.AppendLine($"O trabalho aprovado está no branch local `{cfg.IntegrationBranch}`. Nada foi enviado a nenhum remoto.");
        sb.AppendLine("Para incorporar ao seu branch, por decisão sua:").AppendLine();
        sb.AppendLine("```").AppendLine($"git -C \"{repo}\" merge {cfg.IntegrationBranch}").AppendLine("```");
        sb.AppendLine().AppendLine($"Worktrees ficam em `{Path.Combine(repo, ".orq", "worktrees")}` e não são apagados automaticamente.");
        return sb.ToString();
    }
}
