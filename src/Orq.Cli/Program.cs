using System.Text;
using Orq.Core;
using System.Reflection;

Console.OutputEncoding = Encoding.UTF8;

const string Help = """
    DevLine AI — orquestrador local de agentes (Claude Code, Codex CLI, Ollama)

      orq --version                       versão do produto
      orq doctor                          diagnóstico do ambiente
      orq allow   --repo <pasta>          autoriza um repositório (obrigatório antes de run)
      orq init    --repo <pasta>          cria .orq/config.json e um plano de exemplo
      orq run     --repo <pasta> --plan <arquivo.json> [--simulate] [--sim-limit <provedor>] [--wait]
      orq retry   --repo <pasta> --task <id>             reabre uma tarefa que falhou (zera tentativas)
      orq corrigir --repo <pasta> --task <id> (--texto "<instrução>" | --arquivo <caminho.md>) [--forcar]
      orq status  --repo <pasta>          tarefas, responsáveis e provedores

    Frentes paralelas (um prompt de comando por frente, mesmo repositório):
      orq resume --repo <pasta> --only T05,T07 --nome "Frente Codex" --wait
      --only limita a frente às tarefas listadas; dependências de outra frente são aguardadas.
      --concurrency N sobrepõe maxConcurrency da configuração.
      orq resume  --repo <pasta> [--simulate] [--wait]   continua tarefas interrompidas
      orq report  --repo <pasta>          relatório final (também salvo em .orq/report.md)

    --repo é opcional: o padrão é a pasta atual.
    --simulate usa provedores simulados (não consome tokens). --wait aguarda a capacidade voltar.
    """;

if (args.Length > 0 && args[0] == "--version")
{
    var assembly = Assembly.GetExecutingAssembly();
    Console.WriteLine($"{assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product} {assembly.GetName().Version}");
    return 0;
}

if (args.Length == 0 || args[0] is "-h" or "--help" or "help") { Console.WriteLine(Help); return 0; }

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
bool Flag(string name) => args.Contains(name);

var repo = Path.GetFullPath(Opt("--repo") ?? Directory.GetCurrentDirectory());
var only = Opt("--only")?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
var frente = Opt("--nome");
if (frente != null) { try { Console.Title = frente; } catch { } }
var guard = new Guard(Guard.DefaultAllowFile);

OrqConfig LoadConfig()
{
    var path = Path.Combine(Engine.OrqDir(repo), "config.json");
    var cfg = File.Exists(path) ? Json.From<OrqConfig>(File.ReadAllText(path)) : new OrqConfig();
    var ctx = Path.Combine(Engine.OrqDir(repo), "contexto.md");
    if (File.Exists(ctx)) cfg.Context = File.ReadAllText(ctx);
    if (int.TryParse(Opt("--concurrency"), out var conc) && conc > 0) cfg.MaxConcurrency = conc;
    return cfg;
}

Dictionary<string, IProvider> Providers(OrqConfig cfg)
{
    if (!Flag("--simulate"))
        return new Dictionary<string, IProvider>
        {
            ["claude"] = new ClaudeProvider(cfg.For("claude")),
            ["codex"] = new CodexProvider(cfg.For("codex")),
            ["ollama"] = new OllamaProvider(cfg.For("ollama")),
        };
    var sims = new Dictionary<string, IProvider>
    {
        ["claude"] = new SimulatedProvider("claude") { Delay = TimeSpan.FromMilliseconds(800) },
        ["codex"] = new SimulatedProvider("codex") { Delay = TimeSpan.FromMilliseconds(800) },
        ["ollama"] = new SimulatedProvider("ollama") { Delay = TimeSpan.FromMilliseconds(800), CanEditFiles = false },
    };
    if (Opt("--sim-limit") is { } limited && sims.TryGetValue(limited, out var p))
        ((SimulatedProvider)p).Script = (_, _, _) => Task.FromResult<ProviderResult?>(new ProviderResult
        {
            Outcome = Outcome.RateLimited, Error = "usage limit reached (simulado)", Text = "trabalho parcial antes do limite",
        });
    return sims;
}

async Task<int> Execute(bool loadPlan)
{
    guard.EnsureAllowed(repo);
    var cfg = LoadConfig();
    using var store = new Store(Engine.DbPath(repo));
    if (loadPlan)
    {
        var planPath = Opt("--plan") ?? throw new ArgumentException("informe --plan <arquivo.json>");
        var (added, kept) = store.LoadPlan(Json.From<PlanFile>(Travas.NormalizarPlano(File.ReadAllText(planPath))));
        Console.WriteLine($"Plano carregado: {added} tarefa(s) nova(s), {kept} já registrada(s).");
    }
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Console.WriteLine("Interrompendo: salvando estado..."); };

    while (true)
    {
        if (frente != null) Console.WriteLine($"== {frente}: {(only == null ? "todas as tarefas" : string.Join(", ", only))} ==");
        var engine = new Engine(repo, cfg, store, Providers(cfg), guard, Console.WriteLine, only);
        var s = await engine.RunAsync(cts.Token);
        Console.WriteLine();
        Console.Write(Report.Status(store));
        Console.WriteLine($"Resumo: {s.Done} concluída(s), {s.Interrupted} interrompida(s), {s.Failed} falha(s), {s.Blocked} bloqueada(s), {s.Pending} pendente(s). " +
                          $"Chamadas: {s.Calls}; tokens: {s.Tokens}; custo informado: US$ {s.CostUsd:0.0000}.");
        File.WriteAllText(Path.Combine(Engine.OrqDir(repo), "report.md"), Report.Full(store, cfg, repo));
        if (s.Interrupted == 0 || !Flag("--wait") || cts.IsCancellationRequested) return s.AllDone ? 0 : 2;

        // --wait: não contorna limite algum; apenas espera a capacidade voltar e tenta de novo.
        var next = new[] { "claude", "codex", "ollama" }.Select(n => store.GetProvider(n)?.CooldownUntil)
            .Where(d => d > DateTime.UtcNow).DefaultIfEmpty(DateTime.UtcNow.AddMinutes(5)).Min()!.Value;
        var delay = next - DateTime.UtcNow + TimeSpan.FromSeconds(5);
        Console.WriteLine($"Aguardando capacidade até {next.ToLocalTime():HH:mm} (Ctrl+C encerra; o estado já está salvo)...");
        try { await Task.Delay(delay, cts.Token); } catch (OperationCanceledException) { return 2; }
    }
}

try
{
    switch (args[0])
    {
        case "doctor":
        {
            var items = await Doctor.RunAsync(LoadConfig());
            foreach (var i in items) Console.WriteLine($"[{(i.Ok ? "OK" : "!!")}] {i.Name,-13} {i.Detail}");
            return items.All(i => i.Ok) ? 0 : 1;
        }
        case "allow":
            if (!Directory.Exists(Path.Combine(repo, ".git"))) { Console.WriteLine($"{repo} não é a raiz de um repositório git."); return 1; }
            guard.Allow(repo);
            Console.WriteLine($"Autorizado: {repo}");
            return 0;
        case "init":
        {
            var dir = Engine.OrqDir(repo);
            Directory.CreateDirectory(dir);
            var cfgPath = Path.Combine(dir, "config.json");
            if (!File.Exists(cfgPath)) File.WriteAllText(cfgPath, Json.To(new OrqConfig()));
            var planPath = Path.Combine(dir, "plano.exemplo.json");
            if (!File.Exists(planPath))
                File.WriteAllText(planPath, Json.To(new PlanFile
                {
                    Tasks =
                    {
                        new TaskItem { Id = "T1", Title = "Levantar requisitos", Role = Role.Requirements, Complexity = Complexity.Simple,
                            Objective = "Descrever os requisitos de uma função soma(a,b) em JavaScript.", Acceptance = { "Lista de requisitos verificáveis" } },
                        new TaskItem { Id = "T2", Title = "Implementar soma", Role = Role.Developer, Complexity = Complexity.Medium, DependsOn = { "T1" },
                            Objective = "Criar src/soma.js exportando soma(a,b).", Acceptance = { "soma(2,3) retorna 5" }, Files = { "src/soma.js" } },
                        new TaskItem { Id = "T3", Title = "Documentar", Role = Role.Developer, Complexity = Complexity.Simple, Review = false,
                            Objective = "Criar docs/USO.md explicando em 5 linhas como usar a função soma.", Files = { "docs/USO.md" } },
                    },
                }));
            Console.WriteLine($"Criados: {cfgPath}\n         {planPath}");
            return 0;
        }
        case "retry":
        {
            guard.EnsureAllowed(repo);
            using var store = new Store(Engine.DbPath(repo));
            var id = Opt("--task") ?? throw new ArgumentException("informe --task <id>");
            var t = store.Reopen(id);
            Console.WriteLine($"{t.Id} reaberta (fase {t.Phase}); continue com: orq resume");
            return 0;
        }
        case "corrigir":
        {
            guard.EnsureAllowed(repo);
            using var store = new Store(Engine.DbPath(repo));
            var id = Opt("--task") ?? throw new ArgumentException("informe --task <id>");
            var t = CliCommands.Corrigir(store, id, Opt("--texto"), Opt("--arquivo"), Flag("--forcar"));
            Console.WriteLine($"{t.Id}: instrução registrada, fase Dev, estado Pending; continue com: orq resume");
            return 0;
        }
        case "run": return await Execute(true);
        case "resume": return await Execute(false);
        case "status":
        {
            guard.EnsureAllowed(repo);
            using var store = new Store(Engine.DbPath(repo));
            Console.Write(Report.Status(store));
            Console.WriteLine();
            foreach (var e in store.Events(12)) Console.WriteLine($"{DateTime.Parse(e.Ts).ToLocalTime():HH:mm:ss} {e.TaskId,-6} {e.Message}");
            return 0;
        }
        case "report":
        {
            guard.EnsureAllowed(repo);
            using var store = new Store(Engine.DbPath(repo));
            var text = Report.Full(store, LoadConfig(), repo);
            File.WriteAllText(Path.Combine(Engine.OrqDir(repo), "report.md"), text);
            Console.WriteLine(text);
            return 0;
        }
        default:
            Console.WriteLine(Help);
            return 1;
    }
}
catch (PermissionDeniedException ex) { Console.WriteLine("BLOQUEADO: " + ex.Message); return 3; }
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FileNotFoundException or System.Text.Json.JsonException)
{
    Console.WriteLine("ERRO: " + ex.Message);
    return 1;
}

// Allows CLI integration tests to locate this executable assembly.
public partial class Program { }
