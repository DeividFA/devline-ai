using Orq.Core;

namespace Orq.Tests;

public class NetworkTests
{
    [Theory]
    [InlineData("Reconnecting... waiting for network")]
    [InlineData("Connection failed")]
    [InlineData("error sending request for url")]
    [InlineData("connection timed out")]
    [InlineData("connect timeout")]
    [InlineData("ConnectTimeout")]
    [InlineData("ETIMEDOUT")]
    [InlineData("stream disconnected")]
    public void Rede_EhTransitoria(string message) => Assert.Equal(Outcome.NetworkError, Classifier.Classify(message));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parsers_PreservamSessaoEReconhecemRedeMesmoNoTimeout(bool timeout)
    {
        const string codex = """
            {"type":"thread.started","thread_id":"sessao"}
            {"type":"error","message":"Reconnecting... waiting for network"}
            """;
        const string claude = """{"type":"result","is_error":true,"session_id":"sessao","result":"Connection failed"}""";
        foreach (var r in new[] { CodexProvider.Parse(new ProcResult(-1, codex, "", timeout)), ClaudeProvider.Parse(new ProcResult(-1, claude, "", timeout)) })
        {
            Assert.Equal(Outcome.NetworkError, r.Outcome);
            Assert.Equal("sessao", r.SessionId);
        }
    }

    [Fact]
    public async Task Rede_SalvaCheckpointERepeteNaMesmaSessaoComEsperaCrescente()
    {
        using var env = new TestEnv();
        env.Codex.Delay = TimeSpan.Zero;
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        env.Codex.Script = (req, n, _) =>
        {
            if (n > 1) Assert.Equal("sessao-rede", req.ResumeSessionId);
            if (n > 6) return Task.FromResult<ProviderResult?>(null);
            SimulatedProvider.Write(req, "a.txt", "parcial " + n);
            return Task.FromResult<ProviderResult?>(new ProviderResult
            {
                Outcome = Outcome.Unavailable, Error = "Connection failed", SessionId = "sessao-rede",
            });
        };
        var waits = new List<TimeSpan>();
        var engine = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard)
        {
            NetworkDelay = (delay, _) =>
            {
                Assert.Equal(TaskState.Running, env.Store.Get("A")!.State);
                Assert.Contains("checkpoint A", env.RawGit("log", "--format=%s", "orq/task-A"));
                Assert.Equal("", env.RawGit("-C", env.Store.Get("A")!.Worktree!, "status", "--porcelain"));
                waits.Add(delay);
                return Task.CompletedTask;
            },
        };

        var result = await engine.RunAsync(CancellationToken.None);
        Assert.True(result.AllDone);
        Assert.Equal(new[] { 1, 2, 4, 8, 8, 8 }.Select(n => TimeSpan.FromMinutes(n)), waits);
        Assert.Equal(7, env.Codex.Calls.Count);
        Assert.Empty(env.Claude.Calls);
        Assert.Equal(1, env.Store.Get("A")!.Attempts);
        Assert.Equal(6, env.Store.Events(100).Count(e => e.Kind == "rede"));
        Assert.Null(env.Store.GetProvider("codex")!.CooldownUntil);
    }

    [Fact]
    public async Task Rede_EsgotadaInterrompeSemTrocarDeProvedor()
    {
        using var env = new TestEnv();
        env.Codex.Delay = TimeSpan.Zero;
        env.Cfg.RedeMaxTentativas = 2;
        env.Cfg.RedeEsperaInicialMinutos = 3;
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        env.Codex.Script = (_, _, _) => Task.FromResult<ProviderResult?>(TestEnv.Fail(Outcome.NetworkError, "Connection failed"));
        var waits = new List<TimeSpan>();
        var engine = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard)
        {
            NetworkDelay = (delay, _) => { waits.Add(delay); return Task.CompletedTask; },
        };

        var result = await engine.RunAsync(CancellationToken.None);
        Assert.Equal(1, result.Interrupted);
        Assert.Equal(0, result.Failed);
        Assert.Equal(3, env.Codex.Calls.Count);
        Assert.Empty(env.Claude.Calls);
        Assert.Equal(new[] { TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(6) }, waits);
        Assert.Equal(Phase.Dev, env.Store.Get("A")!.Phase);
        Assert.Contains("reconexão esgotada", env.Store.Get("A")!.NextAction);
        Assert.Equal("sim-codex-A", env.Store.Get("A")!.Sessions["codex:Developer"]);
    }

    [Fact]
    public async Task LimiteDeUso_MantemCooldownSemEsperaDeRede()
    {
        using var env = new TestEnv();
        env.Codex.Delay = TimeSpan.Zero;
        env.Cfg.ExecucaoSoNoPreferido = true;
        var t = env.Add("A", Complexity.Medium, new[] { "a.txt" });
        t.Provider = "codex";
        env.Store.Save(t);
        env.Codex.Script = (_, _, _) => Task.FromResult<ProviderResult?>(TestEnv.Fail(Outcome.RateLimited, "usage limit reached; waiting for network"));
        var engine = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard)
        {
            NetworkDelay = (_, _) => throw new InvalidOperationException("Limite de uso não é falha de rede."),
        };

        Assert.Equal(1, (await engine.RunAsync(CancellationToken.None)).Interrupted);
        Assert.Single(env.Codex.Calls);
        Assert.Equal("RateLimited", env.Store.GetProvider("codex")!.LastOutcome);
        Assert.True(env.Store.GetProvider("codex")!.CooldownUntil > DateTime.UtcNow.AddMinutes(20));
        Assert.DoesNotContain(env.Store.Events(100), e => e.Kind == "rede");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Revisao_RedeEsgotadaInterrompeSemIntegrar(bool selfReview)
    {
        using var env = new TestEnv();
        env.Codex.Delay = env.Claude.Delay = TimeSpan.Zero;
        env.Cfg.RedeMaxTentativas = 1;
        if (selfReview) env.Cfg.SelfReviewProvider = "codex";
        env.Add("A", Complexity.Medium, new[] { "a.txt" }, review: true);
        env.Codex.Script = env.Claude.Script = (req, _, _) => Task.FromResult<ProviderResult?>(req.Role == Role.Reviewer
            ? TestEnv.Fail(Outcome.NetworkError, "Connection failed") : null);
        var waits = 0;
        var engine = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard)
        {
            NetworkDelay = (_, _) => { waits++; return Task.CompletedTask; },
        };

        Assert.Equal(1, (await engine.RunAsync(CancellationToken.None)).Interrupted);
        Assert.Equal(1, waits);
        Assert.Equal(Phase.Review, env.Store.Get("A")!.Phase);
        Assert.False(env.ExistsInIntegration("a.txt"));
        var reviews = env.Codex.Calls.Concat(env.Claude.Calls).Where(c => c.Req.Role == Role.Reviewer).ToList();
        Assert.Equal(2, reviews.Count);
        if (selfReview)
        {
            Assert.False(env.Store.Get("A")!.SelfReviewDone);
            Assert.Empty(env.Claude.Calls);
        }
    }

    [Fact]
    public async Task CancelarDuranteEspera_PreservaCheckpointESessao()
    {
        using var env = new TestEnv();
        using var cts = new CancellationTokenSource();
        env.Codex.Delay = TimeSpan.Zero;
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        env.Codex.Script = (req, _, _) =>
        {
            SimulatedProvider.Write(req, "a.txt", "parcial");
            return Task.FromResult<ProviderResult?>(TestEnv.Fail(Outcome.NetworkError, "Connection failed"));
        };
        var engine = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard)
        {
            NetworkDelay = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; },
        };

        Assert.Equal(1, (await engine.RunAsync(cts.Token)).Interrupted);
        Assert.Single(env.Codex.Calls);
        Assert.Equal("sim-codex-A", env.Store.Get("A")!.Sessions["codex:Developer"]);
        Assert.Contains("checkpoint A", env.RawGit("log", "--format=%s", "orq/task-A"));
    }

    [Fact]
    public void Configuracao_PadroesENomesJson()
    {
        var cfg = new OrqConfig();
        Assert.Equal(1, cfg.RedeEsperaInicialMinutos);
        Assert.Equal(6, cfg.RedeMaxTentativas);
        cfg = Json.From<OrqConfig>("""{"redeEsperaInicialMinutos":2,"redeMaxTentativas":4}""");
        Assert.Equal(2, cfg.RedeEsperaInicialMinutos);
        Assert.Equal(4, cfg.RedeMaxTentativas);
        Assert.Contains("\"redeMaxTentativas\": 4", Json.To(cfg));
    }
}
