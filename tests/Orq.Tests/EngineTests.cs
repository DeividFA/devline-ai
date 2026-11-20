using Orq.Core;

namespace Orq.Tests;

public class EngineTests
{
    private static bool Overlap(SimulatedProvider.Call a, SimulatedProvider.Call b) => a.Start < b.End && b.Start < a.End;

    [Fact]
    public async Task Paralelo_DoisAgentesExecutamAoMesmoTempo()
    {
        using var env = new TestEnv();
        env.Claude.Delay = env.Codex.Delay = TimeSpan.FromMilliseconds(3000);
        env.Add("A", Complexity.Medium, new[] { "a.txt" });   // codex primeiro
        env.Add("B", Complexity.Complex, new[] { "b.txt" });  // claude primeiro

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        var ca = Assert.Single(env.Codex.Calls);
        var cb = Assert.Single(env.Claude.Calls);
        Assert.True(Overlap(ca, cb), "as duas chamadas deveriam se sobrepor no tempo");
        Assert.Contains("codex", env.Integrated("a.txt"));
        Assert.Contains("claude", env.Integrated("b.txt"));
        Assert.DoesNotContain("a.txt", env.RawGit("ls-tree", "--name-only", "main")); // branch do usuário intacto
    }

    [Fact]
    public async Task Isolamento_MesmoArquivoNuncaEditadoEmParalelo()
    {
        using var env = new TestEnv();
        env.Claude.Delay = env.Codex.Delay = TimeSpan.FromMilliseconds(600);
        env.Add("A", Complexity.Medium, new[] { "src/comum.txt" });
        env.Add("B", Complexity.Complex, new[] { "src/comum.txt" });
        env.Add("C", Complexity.Complex, new[] { "src/", "src/c.txt" }); // diretório inteiro também conflita

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        var calls = env.Codex.Calls.Concat(env.Claude.Calls).ToList();
        Assert.Equal(3, calls.Count);
        for (var i = 0; i < calls.Count; i++)
            for (var j = i + 1; j < calls.Count; j++)
                Assert.False(Overlap(calls[i], calls[j]), "tarefas com o mesmo arquivo reservado rodaram em paralelo");
    }

    [Fact]
    public async Task Isolamento_AlteracaoForaDaReservaEhDesfeita()
    {
        using var env = new TestEnv();
        env.Codex.Script = (req, _, _) =>
        {
            SimulatedProvider.Write(req, "intruso.txt", "não deveria existir");
            SimulatedProvider.Write(req, "README.md", "sobrescrito indevidamente");
            return Task.FromResult<ProviderResult?>(null); // segue com o comportamento padrão (escreve a.txt)
        };
        env.Add("A", Complexity.Medium, new[] { "a.txt" });

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.True(env.ExistsInIntegration("a.txt"));
        Assert.False(env.ExistsInIntegration("intruso.txt"));
        Assert.Equal("projeto de teste", env.Integrated("README.md"));
        Assert.Equal(2, env.Store.Get("A")!.Violations.Count);
    }

    [Fact]
    public async Task FalhaDeProvedor_OutroAssume()
    {
        using var env = new TestEnv();
        env.Codex.Script = (_, _, _) => Task.FromResult<ProviderResult?>(TestEnv.Fail(Outcome.Unavailable, "ECONNREFUSED"));
        env.Add("A", Complexity.Medium, new[] { "a.txt" });

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal("claude", env.Store.Get("A")!.Owner);
        var attempts = env.Store.Attempts();
        Assert.Contains(attempts, a => a.Provider == "codex" && a.Outcome == "Unavailable");
        Assert.Contains(attempts, a => a.Provider == "claude" && a.Outcome == "Success");
    }

    [Fact]
    public async Task LimiteDeUso_SalvaCheckpointETransfereResumoEstruturado()
    {
        using var env = new TestEnv();
        const string segredo = "sk-abcdefghijklmnopqrstuvwxyz0123456789";
        env.Codex.Script = (req, _, _) =>
        {
            SimulatedProvider.Write(req, "a.txt", "metade do trabalho\n");
            return Task.FromResult<ProviderResult?>(TestEnv.Fail(Outcome.RateLimited, "You've hit your usage limit",
                $"Implementei metade. token={segredo}"));
        };
        env.Claude.Script = (req, _, _) =>
        {
            // o trabalho parcial do provedor anterior precisa estar no diretório de trabalho
            var previous = File.ReadAllText(Path.Combine(req.Cwd, "a.txt"));
            SimulatedProvider.Write(req, "a.txt", previous + "concluído por claude\n");
            return Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = "{\"summary\":\"terminei\"}" });
        };
        env.Add("A", Complexity.Medium, new[] { "a.txt" });

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        var final = env.Integrated("a.txt");
        Assert.Contains("metade do trabalho", final);
        Assert.Contains("concluído por claude", final);

        var prompt = Assert.Single(env.Claude.Calls).Req.Prompt;
        Assert.Contains("Handoff", prompt);
        Assert.Contains("codex", prompt);
        Assert.Contains("RateLimited", prompt);
        Assert.Contains("a.txt", prompt);
        Assert.DoesNotContain(segredo, prompt);         // nenhuma credencial é transferida
        Assert.True(prompt.Length < 4000, "o handoff deve ser um resumo, não o histórico completo");

        var st = env.Store.GetProvider("codex")!;
        Assert.Equal("RateLimited", st.LastOutcome);
        Assert.True(st.CooldownUntil > DateTime.UtcNow.AddMinutes(20));
        Assert.Contains("checkpoint A", env.RawGit("log", "--format=%s", "orq/task-A"));
    }

    [Fact]
    public async Task TodosNoLimite_TarefaFicaInterrompidaERetomaQuandoHaCapacidade()
    {
        using var env = new TestEnv();
        Func<AgentRequest, int, CancellationToken, Task<ProviderResult?>> limited =
            (_, _, _) => Task.FromResult<ProviderResult?>(TestEnv.Fail(Outcome.RateLimited, "rate limit"));
        env.Codex.Script = limited;
        env.Claude.Script = limited;
        env.Add("A", Complexity.Medium, new[] { "a.txt" });

        var s1 = await env.NewEngine().RunAsync(CancellationToken.None);
        Assert.Equal(1, s1.Interrupted);
        Assert.Equal(0, s1.Failed);
        Assert.Equal(0, env.Store.Get("A")!.Attempts);   // sem agente disponível, a tentativa não é gasta

        // Enquanto o período de espera não termina, nenhuma nova chamada é feita (limite não é contornado).
        var s2 = await env.NewEngine().RunAsync(CancellationToken.None);
        Assert.Equal(1, s2.Interrupted);
        Assert.Equal(0, s2.Calls);

        // Capacidade de volta: a espera expirou e o provedor responde.
        env.Store.SetProvider("claude", null, null, null);
        env.Claude.Script = null;
        var s3 = await env.NewEngine().RunAsync(CancellationToken.None);
        Assert.True(s3.AllDone);
        Assert.Equal("claude", env.Store.Get("A")!.Owner);
    }

    [Fact]
    public async Task Reinicio_RetomaSemRepetirTarefasConcluidas()
    {
        using var env = new TestEnv();
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        env.Add("B", Complexity.Complex, new[] { "b.txt" }, Role.Developer, false, "A");
        var started = new TaskCompletionSource();
        env.Claude.Script = async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // agente "travado" no momento da queda
            return null;
        };

        using var cts = new CancellationTokenSource();
        var run = env.NewEngine().RunAsync(cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        cts.Cancel();
        await run;

        // Queda abrupta: o registro em disco ainda diz "em execução".
        var b = env.Store.Get("B")!;
        b.State = TaskState.Running;
        env.Store.Save(b);
        env.Restart();
        env.Claude.Script = null;

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Single(env.Codex.Calls);                       // A não foi executada de novo
        Assert.Contains("claude", env.Integrated("b.txt"));
        Assert.Contains(env.Store.Events(100), e => e.Kind == "recover");
    }

    [Fact]
    public async Task LimiteConfigurado_ParaAntesDeEstourarOOrcamento()
    {
        using var env = new TestEnv();
        env.Cfg.MaxConcurrency = 1;
        env.Cfg.Limits.MaxCallsPerRun = 1;
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        env.Add("B", Complexity.Medium, new[] { "b.txt" });

        var s1 = await env.NewEngine().RunAsync(CancellationToken.None);
        Assert.Equal(1, s1.Done);
        Assert.Equal(1, s1.Interrupted);
        Assert.Equal(1, s1.Calls);
        Assert.Contains("limite configurado", env.Store.Get("B")!.NextAction);

        var s2 = await env.NewEngine().RunAsync(CancellationToken.None); // nova execução, novo orçamento
        Assert.True(s2.AllDone);
    }

    [Fact]
    public async Task Testes_FalhaVoltaAoDesenvolvedorComSessaoRetomada()
    {
        using var env = new TestEnv();
        env.Cfg.TestCommand = "findstr /c:\"correto\" a.txt";
        env.Codex.Script = (req, n, _) =>
        {
            SimulatedProvider.Write(req, "a.txt", n == 1 ? "errado\n" : "correto\n");
            return Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = "{\"summary\":\"feito\"}", SessionId = "sessao-1" });
        };
        env.Add("A", Complexity.Medium, new[] { "a.txt" });

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        var calls = env.Codex.Calls.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Null(calls[0].Req.ResumeSessionId);
        Assert.Equal("sessao-1", calls[1].Req.ResumeSessionId);
        Assert.Contains("Testes falharam", calls[1].Req.Prompt);
        Assert.True(calls[1].Req.Prompt.Length < calls[0].Req.Prompt.Length, "a retomada envia só a correção");
        Assert.Contains("correto", env.Integrated("a.txt"));
        Assert.Contains(env.Store.Get("A")!.Tests, t => t.StartsWith("OK"));
    }

    [Fact]
    public async Task ValidacaoRapida_NaFaseDeTeste_ECompletaSoNoMerge()
    {
        using var env = new TestEnv();
        // rápida: só confere que o arquivo existe; completa (no merge): exige o conteúdo final "correto"
        env.Cfg.QuickTestCommand = "findstr /c:\"o\" a.txt";
        env.Cfg.TestCommand = "findstr /c:\"correto\" a.txt";
        env.Codex.Script = (req, n, _) =>
        {
            SimulatedProvider.Write(req, "a.txt", n == 1 ? "errado\n" : "correto\n");
            return Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = "{\"summary\":\"feito\"}", SessionId = "s" });
        };
        env.Add("A", Complexity.Medium, new[] { "a.txt" }, review: false);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(2, env.Codex.Calls.Count);                       // a rápida passou com "errado"; a completa no merge reprovou e devolveu
        Assert.Contains("validação completa no merge", env.Codex.Calls.ToList()[1].Req.Prompt);
        Assert.Contains("correto", env.Integrated("a.txt"));          // só integra depois de passar na completa
        Assert.Contains(env.Store.Get("A")!.Tests, t => t.StartsWith("merge: OK"));
    }

    [Fact]
    public async Task Testes_EsgotadasAsTentativasNadaEhIntegrado()
    {
        using var env = new TestEnv();
        env.Cfg.TestCommand = "findstr /c:\"nunca\" a.txt";
        env.Cfg.MaxAttemptsPerTask = 2;
        env.Add("A", Complexity.Medium, new[] { "a.txt" });

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.Equal(1, s.Failed);
        Assert.False(env.ExistsInIntegration("a.txt"));
    }

    [Fact]
    public async Task Revisao_FeitaPorOutroProvedorEPedeMudancas()
    {
        using var env = new TestEnv();
        env.Claude.Script = (req, n, _) => Task.FromResult<ProviderResult?>(req.Role == Role.Reviewer && n == 1
            ? new ProviderResult { Outcome = Outcome.Success, Text = "{\"verdict\":\"changes\",\"notes\":\"falta tratar nulo\"}" }
            : null);
        env.Add("A", Complexity.Medium, new[] { "a.txt" }, Role.Developer, review: true);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(2, env.Codex.Calls.Count);                              // desenvolveu, corrigiu
        Assert.All(env.Codex.Calls, c => Assert.Equal(Role.Developer, c.Req.Role));
        Assert.Equal(2, env.Claude.Calls.Count);                             // revisou duas vezes
        Assert.All(env.Claude.Calls, c => Assert.True(c.Req.ReadOnly));
        Assert.Contains("falta tratar nulo", env.Codex.Calls.Last().Req.Prompt);
        Assert.Contains("diff", env.Claude.Calls.First().Req.Prompt);
    }

    [Fact]
    public async Task Ollama_TarefaSimplesFicaLocalEAnaliseViraContrato()
    {
        using var env = new TestEnv();
        env.Ollama.Script = (req, _, _) => Task.FromResult<ProviderResult?>(req.ReadOnly
            ? new ProviderResult { Outcome = Outcome.Success, Text = "Requisitos...\n{\"summary\":\"a soma deve aceitar negativos\",\"decisions\":[],\"pending\":[]}" }
            : new ProviderResult { Outcome = Outcome.Success, Text = "```md\n# Uso\nlinha\n```" });
        env.Add("R", Complexity.Simple, Array.Empty<string>(), Role.Requirements);
        env.Add("D", Complexity.Simple, new[] { "docs/USO.md" }, Role.Developer, false, "R");
        env.Add("X", Complexity.Complex, new[] { "x.txt" }, Role.Developer, false, "R");

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(2, env.Ollama.Calls.Count);
        Assert.Empty(env.Codex.Calls);
        Assert.Equal("# Uso\nlinha", env.Integrated("docs/USO.md").Replace("\r", ""));       // cercas removidas
        Assert.Contains("a soma deve aceitar negativos", Assert.Single(env.Claude.Calls).Req.Prompt); // contrato repassado
    }

    [Fact]
    public async Task Preferencia_ProvedorEscolhidoDesenvolveERevisaoESempreDoClaude()
    {
        using var env = new TestEnv();
        var t = env.Add("A", Complexity.Medium, new[] { "a.txt" }, Role.Developer, review: true); // médio iria ao codex
        t.Provider = "codex";
        env.Store.Save(t);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(Role.Developer, Assert.Single(env.Codex.Calls).Req.Role);
        Assert.Equal(Role.Reviewer, Assert.Single(env.Claude.Calls).Req.Role);  // revisão sempre no Claude (Opus)
    }

    [Fact]
    public void Escalada_DepoisDeDuasReprovacoes_CorrecaoVaiSoParaClaudeComModeloDeEscalada()
    {
        var cfg = new OrqConfig();
        cfg.Providers["claude"] = new ProviderConfig { Models = new(StringComparer.OrdinalIgnoreCase) { ["medium"] = "sonnet", ["review"] = "claude-opus-5-5", ["escalation"] = "claude-opus-4-8" } };
        var t = new TaskItem { Id = "E", Complexity = Complexity.Medium, Provider = "codex" };
        var disp = new[] { "claude", "codex" };

        t.ReviewRejections = 1;   // 1ª reprovação: o Codex ainda corrige
        Assert.Equal("codex", Router.Candidates(t, Role.Developer, null, cfg, disp)[0]);
        Assert.Equal("sonnet", cfg.For("claude").ModelFor(Role.Developer, t.Complexity, Router.Escalada(t, cfg)));

        t.ReviewRejections = 2;   // 2ª reprovação: só o Claude, com o modelo de escalada
        Assert.Equal(new[] { "claude" }, Router.Candidates(t, Role.Developer, null, cfg, disp));
        Assert.Equal("claude-opus-4-8", cfg.For("claude").ModelFor(Role.Developer, t.Complexity, Router.Escalada(t, cfg)));
        // a revisão continua com o modelo de revisão
        Assert.Equal("claude-opus-5-5", cfg.For("claude").ModelFor(Role.Reviewer, t.Complexity, Router.Escalada(t, cfg)));
    }

    [Fact]
    public void Revisao_NuncaCaiParaOutroProvedor()
    {
        var cfg = new OrqConfig();
        var t = new TaskItem { Id = "R", Complexity = Complexity.Complex, Provider = "codex" };
        var lista = Router.Candidates(t, Role.Reviewer, avoid: "codex", cfg, new[] { "claude", "codex", "ollama" });
        Assert.Equal(new[] { "claude" }, lista);
    }

    [Fact]
    public async Task Faixas_DiferentesRodamJuntasEIguaisEmSequencia()
    {
        using var env = new TestEnv();
        env.Cfg.MaxConcurrency = 3;
        env.Claude.Delay = env.Codex.Delay = TimeSpan.FromMilliseconds(3000);
        void Lane(string id, string lane, string provider, string file)
        {
            var t = env.Add(id, Complexity.Medium, Array.Empty<string>()); // sem reserva de arquivos: só a faixa decide
            t.Lane = lane; t.Provider = provider;
            env.Store.Save(t);
            env.Store.Save(t);
        }
        Lane("A", "cadastros", "codex", "a.txt");
        Lane("B", "financeiro", "claude", "b.txt");
        Lane("C", "financeiro", "codex", "c.txt");
        env.Claude.Script = env.Codex.Script = (req, _, _) =>
        {
            SimulatedProvider.Write(req, req.TaskId + ".txt", "x");
            return Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = "{\"summary\":\"ok\"}" });
        };

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        var calls = env.Codex.Calls.Concat(env.Claude.Calls).ToDictionary(c => c.Req.TaskId);
        Assert.True(Overlap(calls["A"], calls["B"]), "faixas diferentes deveriam rodar juntas");
        Assert.False(Overlap(calls["B"], calls["C"]), "a mesma faixa não pode rodar em paralelo");
    }

    [Fact]
    public async Task Frentes_DoisProcessosDividemTarefasEEsperamDependencia()
    {
        using var env = new TestEnv();
        env.Codex.Delay = TimeSpan.FromMilliseconds(800);
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        env.Add("B", Complexity.Complex, new[] { "b.txt" }, Role.Developer, false, "A");
        env.Add("C", Complexity.Complex, new[] { "c.txt" });

        var frenteCodex = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard, null, new[] { "A" });
        var frenteClaude = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard, null, new[] { "B", "C" })
            { DependencyPoll = TimeSpan.FromMilliseconds(200) };

        var r = await Task.WhenAll(frenteClaude.RunAsync(CancellationToken.None), frenteCodex.RunAsync(CancellationToken.None));

        Assert.All(env.Store.All(), t => Assert.Equal(TaskState.Done, t.State));
        Assert.Equal(2, r[0].Done);   // cada frente só conta as suas
        Assert.Equal(1, r[1].Done);
        var a = Assert.Single(env.Codex.Calls);
        var b = env.Claude.Calls.Single(c => c.Req.TaskId == "B");
        Assert.True(b.Start >= a.End, "B só pode começar depois de A integrada pela outra frente");
        Assert.Contains("claude", env.Integrated("b.txt"));
        Assert.Contains("codex", env.Integrated("a.txt"));
    }

    [Fact]
    public async Task Reabrir_TarefaQueFalhouVoltaAExecutar()
    {
        using var env = new TestEnv();
        env.Cfg.MaxAttemptsPerTask = 1;
        env.Cfg.TestCommand = "findstr /c:\"correto\" a.txt";
        env.Codex.Script = (req, n, _) =>
        {
            SimulatedProvider.Write(req, "a.txt", n == 1 ? "errado\n" : "correto\n");
            return Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = "{}" });
        };
        env.Add("A", Complexity.Medium, new[] { "a.txt" });

        Assert.Equal(1, (await env.NewEngine().RunAsync(CancellationToken.None)).Failed);
        var reaberta = env.Store.Reopen("A");
        Assert.Equal(0, reaberta.Attempts);
        Assert.Contains("Testes falharam", reaberta.Feedback);   // a correção pedida continua valendo

        var s = await env.NewEngine().RunAsync(CancellationToken.None);
        Assert.True(s.AllDone);
        Assert.Contains("correto", env.Integrated("a.txt"));
        Assert.Throws<InvalidOperationException>(() => env.Store.Reopen("A")); // concluída não reabre
    }

    [Fact]
    public async Task Reabrir_DuranteExecucaoEntraSemEsperarOutraTarefa()
    {
        using var env = new TestEnv();
        env.Cfg.MaxConcurrency = 2;
        var lenta = new TaskCompletionSource();
        env.Claude.Script = async (req, _, ct) =>
        {
            if (req.TaskId == "L") await lenta.Task.WaitAsync(ct);
            return null;
        };
        var l = env.Add("L", Complexity.Complex, new[] { "l.txt" });
        var f = env.Add("F", Complexity.Medium, new[] { "f.txt" });
        f.State = TaskState.Failed;
        env.Store.Save(f);

        var engine = new Engine(env.Repo, env.Cfg, env.Store, env.Providers, env.Guard) { DependencyPoll = TimeSpan.FromMilliseconds(200) };
        var run = engine.RunAsync(CancellationToken.None);
        await Task.Delay(800);
        env.Store.Reopen("F");                                   // reaberta enquanto L ainda roda
        var limite = DateTime.UtcNow.AddSeconds(30);
        while (env.Store.Get("F")!.State != TaskState.Done && DateTime.UtcNow < limite) await Task.Delay(100);

        Assert.Equal(TaskState.Done, env.Store.Get("F")!.State);
        Assert.Equal(TaskState.Running, env.Store.Get("L")!.State);
        lenta.SetResult();
        Assert.True((await run).AllDone);
    }

    [Fact]
    public async Task Modelos_BaratoParaSimplesERevisaoComModeloProprio()
    {
        using var env = new TestEnv();
        env.Cfg.Providers["codex"].Models = new(StringComparer.OrdinalIgnoreCase) { ["simple"] = "barato", ["complex"] = "forte" };
        env.Cfg.Providers["claude"].Models = new(StringComparer.OrdinalIgnoreCase) { ["review"] = "revisor" };
        var s1 = env.Add("S", Complexity.Simple, new[] { "s.txt" }, Role.Developer, review: true); s1.Provider = "codex"; env.Store.Save(s1);
        var c1 = env.Add("C", Complexity.Complex, new[] { "c.txt" }); c1.Provider = "codex"; env.Store.Save(c1);
        var m1 = env.Add("M", Complexity.Medium, new[] { "m.txt" }); m1.Provider = "codex"; env.Store.Save(m1);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        var dev = env.Codex.Calls.ToDictionary(c => c.Req.TaskId, c => c.Req.Model);
        Assert.Equal("barato", dev["S"]);
        Assert.Equal("forte", dev["C"]);
        Assert.Null(dev["M"]);                                   // sem mapeamento: padrão da ferramenta
        Assert.Equal("revisor", Assert.Single(env.Claude.Calls).Req.Model);
        Assert.Contains(env.Store.Attempts(), a => a.Provider == "codex:barato");
        Assert.Contains(env.Store.Attempts(), a => a.Provider == "claude:revisor");
    }

    [Fact]
    public async Task Modelos_LimiteDeUmModeloNaoBloqueiaOutroDoMesmoProvedor()
    {
        using var env = new TestEnv();
        env.Cfg.Providers["claude"].Models = new(StringComparer.OrdinalIgnoreCase) { ["review"] = "sonnet" };
        env.Store.SetProvider("claude", DateTime.UtcNow.AddDays(4), "RateLimited", "You've hit your Fable limit");
        var t = env.Add("A", Complexity.Complex, new[] { "a.txt" }, Role.Developer, review: true); t.Provider = "claude"; env.Store.Save(t);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(Role.Developer, Assert.Single(env.Codex.Calls).Req.Role);   // padrão do Claude em espera: Codex desenvolve
        var revisao = Assert.Single(env.Claude.Calls);                            // ...e o Claude revisa com outro modelo
        Assert.Equal(Role.Reviewer, revisao.Req.Role);
        Assert.Equal("sonnet", revisao.Req.Model);
    }

    [Fact]
    public async Task Frentes_TarefaDeProcessoMortoEhRecuperadaEDeVivoNao()
    {
        using var env = new TestEnv();
        var morto = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!;
        morto.WaitForExit();
        Assert.False(Engine.RunnerAlive(morto.Id));
        Assert.False(Engine.RunnerAlive(Environment.ProcessId));   // o próprio processo não conta como "outra frente"
        Assert.False(Engine.RunnerAlive(null));

        var t = env.Add("A", Complexity.Medium, new[] { "a.txt" });
        t.State = TaskState.Running; t.RunnerPid = morto.Id;
        env.Store.Save(t);
        var s = await env.NewEngine().RunAsync(CancellationToken.None);
        Assert.True(s.AllDone);
    }

    private static string CriarSkill(string root, string nome)
    {
        var dir = Path.Combine(root, nome);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: " + nome + "\n---\nregras de " + nome);
        return dir;
    }

    [Fact]
    public async Task Skills_PromptTrazCaminhosEBriefingETarefaVoltaSeNaoDeclarar()
    {
        using var env = new TestEnv();
        var roots = Path.Combine(env.Root, "skills-codex");
        CriarSkill(roots, "ui-ux-pro-max");
        CriarSkill(roots, "frontend-design");
        env.Cfg.SkillRoots["codex"] = roots;
        env.Cfg.SkillRoots["claude"] = Path.Combine(env.Root, "skills-claude-vazio");    // Claude sem as skills: não pode ser escolhido
        Directory.CreateDirectory(Engine.OrqDir(env.Repo));
        File.WriteAllText(Path.Combine(Engine.OrqDir(env.Repo), "brief.md"), "Preservar a logomarca.");
        var t = env.Add("D", Complexity.Complex, new[] { "d.txt" });                     // complexa iria primeiro ao Claude
        t.Skills = new() { "ui-ux-pro-max", "frontend-design" }; t.Brief = "brief.md";
        env.Store.Save(t);
        env.Codex.Script = (req, n, _) =>
        {
            SimulatedProvider.Write(req, "d.txt", "tela " + n);
            var json = n == 1
                ? "{\"summary\":\"fiz\",\"skills\":[{\"nome\":\"ui-ux-pro-max\",\"aplicado\":\"contraste 4.5:1\"}]}"   // esqueceu uma
                : "{\"summary\":\"fiz\",\"skills\":[{\"nome\":\"ui-ux-pro-max\",\"aplicado\":\"contraste\"},{\"nome\":\"frontend-design\",\"aplicado\":\"tipografia\"}]}";
            return Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = json });
        };

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Empty(env.Claude.Calls);
        var calls = env.Codex.Calls.ToList();
        Assert.Equal(2, calls.Count);
        Assert.Contains(Path.Combine(roots, "ui-ux-pro-max", "SKILL.md"), calls[0].Req.Prompt);
        Assert.Contains("Preservar a logomarca.", calls[0].Req.Prompt);
        Assert.Contains(Path.Combine(roots, "frontend-design"), calls[0].Req.ReadDirs);
        Assert.Contains("frontend-design", calls[1].Req.Prompt);                      // a correção pede a skill que faltou
        Assert.Contains(env.Store.Events(100), e => e.Kind == "skills");
        Assert.Contains("tela 2", env.Integrated("d.txt"));
    }

    [Fact]
    public void Skills_DeclaracaoSemOQueFoiAplicadoNaoVale()
    {
        var t = new TaskItem { Skills = { "a", "b" } };
        Assert.Equal(new[] { "a", "b" }, Engine.MissingSkills(t, "sem json"));
        Assert.Equal(new[] { "b" }, Engine.MissingSkills(t, "{\"skills\":[{\"nome\":\"a\",\"aplicado\":\"x\"},{\"nome\":\"b\",\"aplicado\":\"\"}]}"));
        Assert.Empty(Engine.MissingSkills(t, "{\"skills\":[\"a\",\"B\"]}"));
        Assert.Empty(Engine.MissingSkills(new TaskItem(), "qualquer"));
    }

    [Fact]
    public async Task Plano_RecarregarNaoReexecutaConcluidas()
    {
        using var env = new TestEnv();
        var plan = new PlanFile { Tasks = { new TaskItem { Id = "A", Title = "a", Objective = "o", Files = { "a.txt" }, Review = false } } };
        env.Store.LoadPlan(plan);
        await env.NewEngine().RunAsync(CancellationToken.None);

        var (added, kept) = env.Store.LoadPlan(Json.From<PlanFile>(Json.To(plan)));
        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.Equal((0, 1), (added, kept));
        Assert.True(s.AllDone);
        Assert.Single(env.Codex.Calls);
    }
}
