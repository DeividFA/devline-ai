using System.Text.Json;
using System.Text.Json.Serialization;

namespace Orq.Core;

public enum TaskState { Pending, Running, Interrupted, Done, Failed, Blocked }
public enum Phase { Dev, Test, Review, Integrate }
public enum Outcome { Success, RateLimited, AuthError, Timeout, Unavailable, Failed, NetworkError }
public enum UsageKind { Known, Estimated, Unknown }
public enum Role { Requirements, Impact, Developer, Tester, Reviewer, Integrator }
public enum Complexity { Simple, Medium, Complex }

/// <summary>Entrada do registro compartilhado de tarefas (contrato + estado).</summary>
public sealed class TaskItem
{
    // Contrato (vem do plano)
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Objective { get; set; } = "";
    public List<string> Acceptance { get; set; } = new();
    public Role Role { get; set; } = Role.Developer;
    public Complexity Complexity { get; set; } = Complexity.Medium;
    public List<string> Files { get; set; } = new();
    public List<string> DependsOn { get; set; } = new();
    public bool Review { get; set; } = true;
    /// <summary>Provedor preferido para desenvolver/analisar (claude, codex, ollama). Os demais ficam como reserva.</summary>
    public string? Provider { get; set; }
    /// <summary>Faixa de trabalho: tarefas de faixas diferentes podem rodar juntas; da mesma faixa, uma de cada vez.</summary>
    public string? Lane { get; set; }
    /// <summary>Skills obrigatórias (nomes das pastas em skillRoots). O agente precisa declarar o uso de cada uma.</summary>
    public List<string> Skills { get; set; } = new();
    /// <summary>Arquivo de briefing (relativo a .orq) incluído no prompt da tarefa.</summary>
    public string? Brief { get; set; }

    // Estado (mantido pelo orquestrador)
    public TaskState State { get; set; } = TaskState.Pending;
    public Phase Phase { get; set; } = Phase.Dev;
    public string? Owner { get; set; }
    public int Attempts { get; set; }
    /// <summary>Quantas vezes a revisão reprovou esta tarefa. A partir de EscalarAposReprovacoes, a correção vai para o provedor de escalada.</summary>
    public int ReviewRejections { get; set; }
    /// <summary>Auto-revisão do executor já feita neste ciclo (zera quando o revisor pede mudanças).</summary>
    public bool SelfReviewDone { get; set; }
    /// <summary>Commit revisado na última reprovação; base da revisão incremental da correção.</summary>
    public string? LastReviewedCommit { get; set; }
    public List<string> LastReviewFiles { get; set; } = new();
    public string? LastReviewNotes { get; set; }
    public Dictionary<string, string> Sessions { get; set; } = new();
    public List<string> Decisions { get; set; } = new();
    public List<string> Tests { get; set; } = new();
    public List<string> Pending { get; set; } = new();
    public List<string> Violations { get; set; } = new();
    public string? NextAction { get; set; }
    public string? Summary { get; set; }
    public string? Result { get; set; }
    public string? Feedback { get; set; }
    public string? Handoff { get; set; }
    public string? Branch { get; set; }
    public string? Worktree { get; set; }
    /// <summary>Processo (frente) que está executando a tarefa; outra frente não a recupera enquanto ele estiver vivo.</summary>
    public int? RunnerPid { get; set; }
}

public sealed class PlanFile
{
    public List<TaskItem> Tasks { get; set; } = new();
}

public sealed class ProviderConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxConcurrent { get; set; } = 1;
    public string? Model { get; set; }
    public string? Url { get; set; }
    /// <summary>Modelo por tipo de trabalho: simple, medium, complex, review e escalation. Sem chave específica, usa complexidade e depois Model (ou o padrão da ferramenta).</summary>
    public Dictionary<string, string> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? ModelFor(Role role, Complexity complexity, bool escalada = false)
    {
        if (escalada && role != Role.Reviewer && Models.TryGetValue("escalation", out var e) && !string.IsNullOrWhiteSpace(e)) return e;
        if (role == Role.Reviewer && Models.TryGetValue("review", out var r) && !string.IsNullOrWhiteSpace(r)) return r;
        if (Models.TryGetValue(complexity.ToString(), out var m) && !string.IsNullOrWhiteSpace(m)) return m;
        return Model;
    }
}

public sealed class LimitsConfig
{
    public int MaxCallsPerRun { get; set; } = 30;
    public double MaxCostUsdPerRun { get; set; } = 5.0;
    public long MaxTokensPerRun { get; set; } = 3_000_000;
}

public sealed class OrqConfig
{
    public string? BaseBranch { get; set; }
    public string IntegrationBranch { get; set; } = "orq/integration";
    public string? TestCommand { get; set; }
    /// <summary>Validação rápida das correções (ex.: só os testes ligados ao que mudou). Se definida, roda antes da revisão; a completa roda na integração.</summary>
    public string? QuickTestCommand { get; set; }
    public int MaxConcurrency { get; set; } = 2;
    public int TaskTimeoutMinutes { get; set; } = 20;
    public int TestTimeoutMinutes { get; set; } = 10;
    public int MaxAttemptsPerTask { get; set; } = 3;
    public int CooldownMinutes { get; set; } = 30;
    public int RedeEsperaInicialMinutos { get; set; } = 1;
    /// <summary>Repetições após falha transitória de rede; a espera dobra até no máximo oito minutos.</summary>
    public int RedeMaxTentativas { get; set; } = 6;
    /// <summary>Depois de quantas reprovações da revisão a correção sai do provedor normal e vai para EscalationProvider (modelo models.escalation). 0 desliga.</summary>
    public int EscalarAposReprovacoes { get; set; } = 2;
    /// <summary>Nível intermediário: a partir de N reprovações (e antes de EscalarAposReprovacoes), o provedor normal
    /// continua, mas com o seu modelo "escalation" (ex.: Codex com raciocínio máximo). 0 desliga.</summary>
    public int EscalarModeloAposReprovacoes { get; set; }
    public string EscalationProvider { get; set; } = "claude";
    /// <summary>Provedor que faz a auto-revisão antes da revisão oficial (normalmente o mesmo que executa). Vazio desliga.</summary>
    public string? SelfReviewProvider { get; set; }
    /// <summary>Quando true, a execução de uma tarefa com provedor preferido fica SÓ nele (sem cair para outro provedor):
    /// se ele estiver no limite, a tarefa espera. Evita gastar a sessão do revisor com execução. A escalada continua valendo.</summary>
    public bool ExecucaoSoNoPreferido { get; set; }
    /// <summary>Provedor que faz a revisão oficial de cada tarefa (padrão: claude). Sem ele disponível, a revisão espera.</summary>
    public string RevisorProvider { get; set; } = "claude";
    /// <summary>Revisão incremental: se a correção só toca arquivos já revisados e no máximo este número de arquivos, o revisor vê só a correção.</summary>
    public int RevisaoIncrementalMaxArquivos { get; set; } = 6;
    /// <summary>Arquivos que qualquer tarefa pode editar (ex.: documento de estado do projeto): não são desfeitos
    /// pela checagem de reserva e não entram no controle de conflito entre tarefas. Conflito de merge neles é resolvido à mão.</summary>
    public List<string> ArquivosLivres { get; set; } = new();
    /// <summary>Pasta de skills de cada provedor, ex.: claude → C:\src\skills\claude; codex → C:\src\skills\codex.</summary>
    public Dictionary<string, string> SkillRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string? SkillDir(string provider, string skill) =>
        SkillRoots.TryGetValue(provider, out var root) ? Path.Combine(root, skill) : null;

    public static bool SkillInstalled(string? dir) => dir != null && File.Exists(Path.Combine(dir, "SKILL.md"));

    /// <summary>Contexto fixo do projeto, incluído em todo prompt (a CLI carrega de .orq/contexto.md).</summary>
    [JsonIgnore] public string? Context { get; set; }
    public LimitsConfig Limits { get; set; } = new();
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new()
    {
        ["claude"] = new(),
        ["codex"] = new(),
        ["ollama"] = new() { Model = "gemma3:4b", Url = "http://localhost:11434" },
    };

    public ProviderConfig For(string name) =>
        Providers.TryGetValue(name, out var p) ? p : new ProviderConfig();
}

public sealed class Usage
{
    public long Input { get; set; }
    public long Output { get; set; }
    public double? CostUsd { get; set; }
    public UsageKind Kind { get; set; } = UsageKind.Unknown;
}

public sealed class AgentRequest
{
    public string TaskId { get; init; } = "";
    public Role Role { get; init; }
    public string Prompt { get; init; } = "";
    public string Cwd { get; init; } = "";
    public bool ReadOnly { get; init; }
    public IReadOnlyList<string> Files { get; init; } = Array.Empty<string>();
    public string? ResumeSessionId { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(20);
    public string? Model { get; init; }
    /// <summary>Pastas extras que o agente precisa ler (skills, capturas de tela).</summary>
    public IReadOnlyList<string> ReadDirs { get; init; } = Array.Empty<string>();
}

public sealed class ProviderResult
{
    public Outcome Outcome { get; set; }
    public string Text { get; set; } = "";
    public string? SessionId { get; set; }
    public Usage Usage { get; set; } = new();
    public string? Error { get; set; }
}

public interface IProvider
{
    string Name { get; }
    /// <summary>true quando o agente edita arquivos sozinho; false quando só devolve texto.</summary>
    bool CanEditFiles { get; }
    Task<ProviderResult> RunAsync(AgentRequest req, CancellationToken ct);
}

public sealed class PermissionDeniedException(string message) : Exception(message);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string To<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T From<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;

    /// <summary>Extrai o último objeto JSON válido de um texto livre (contrato do agente).</summary>
    public static JsonElement? LastObject(string text)
    {
        var close = text.LastIndexOf('}');
        var tries = 0;
        while (close > 0 && tries < 400)
        {
            for (var i = text.LastIndexOf('{', close); i >= 0 && tries < 400; i = i == 0 ? -1 : text.LastIndexOf('{', i - 1))
            {
                tries++;
                try
                {
                    using var doc = JsonDocument.Parse(text[i..(close + 1)]);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc.RootElement.Clone();
                }
                catch (JsonException) { }
            }
            close = text.LastIndexOf('}', close - 1);
        }
        return null;
    }

    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static long Num(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : 0;

    public static List<string> StrList(this JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array)
            foreach (var x in v.EnumerateArray())
                if (x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString())) list.Add(x.GetString()!);
        return list;
    }
}
