using Orq.Core;

namespace Orq.Tests;

/// <summary>Repositório git temporário + provedores simulados. Nenhum teste consome tokens reais.</summary>
public sealed class TestEnv : IDisposable
{
    public string Root { get; }
    public string Repo { get; }
    public OrqConfig Cfg { get; } = new() { MaxConcurrency = 2, CooldownMinutes = 30 };
    public Guard Guard { get; }
    public Store Store { get; private set; }
    public SimulatedProvider Claude { get; } = new("claude");
    public SimulatedProvider Codex { get; } = new("codex");
    public SimulatedProvider Ollama { get; } = new("ollama") { CanEditFiles = false };

    public TestEnv()
    {
        Root = Path.Combine(Path.GetTempPath(), "orq-test-" + Guid.NewGuid().ToString("N")[..10]);
        Repo = Path.Combine(Root, "repo");
        Directory.CreateDirectory(Repo);
        File.WriteAllText(Path.Combine(Repo, "README.md"), "projeto de teste\n");
        RawGit("init", "-q", "-b", "main");
        RawGit("add", "-A");
        RawGit("-c", "user.name=t", "-c", "user.email=t@t", "commit", "-q", "-m", "inicial");
        Guard = new Guard(Path.Combine(Root, "autorizados.txt"));
        Guard.Allow(Repo);
        Store = new Store(Engine.DbPath(Repo));
    }

    public string RawGit(params string[] args)
    {
        var r = Proc.RunAsync("git", args, Repo, null, TimeSpan.FromSeconds(60), CancellationToken.None).GetAwaiter().GetResult();
        return r.Stdout.Trim();
    }

    public Dictionary<string, IProvider> Providers => new() { ["claude"] = Claude, ["codex"] = Codex, ["ollama"] = Ollama };

    public Engine NewEngine() => new(Repo, Cfg, Store, Providers, Guard);

    /// <summary>Simula o encerramento do processo: fecha e reabre o banco em disco.</summary>
    public void Restart()
    {
        Store.Dispose();
        Store = new Store(Engine.DbPath(Repo));
    }

    public TaskItem Add(string id, Complexity c, string[] files, Role role = Role.Developer, bool review = false, params string[] deps)
    {
        var t = new TaskItem
        {
            Id = id, Title = "tarefa " + id, Objective = "objetivo de " + id, Acceptance = { "critério de " + id },
            Complexity = c, Files = files.ToList(), Role = role, Review = review, DependsOn = deps.ToList(),
        };
        Store.Save(t);
        return t;
    }

    public string Integrated(string path) => RawGit("show", $"{Cfg.IntegrationBranch}:{path}");

    public bool ExistsInIntegration(string path) =>
        RawGit("ls-tree", "-r", "--name-only", Cfg.IntegrationBranch).Split('\n').Any(l => l.Trim() == path);

    public static ProviderResult Fail(Outcome o, string error, string text = "") => new() { Outcome = o, Error = error, Text = text };

    public void Dispose()
    {
        Store.Dispose();
        try
        {
            foreach (var f in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(Root, true);
        }
        catch { }
    }
}
