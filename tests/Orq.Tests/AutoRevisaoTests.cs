using Orq.Core;
using Xunit;

namespace Orq.Tests;

/// <summary>Auto-revisão do executor antes da revisão oficial e revisão incremental da correção.</summary>
public class AutoRevisaoTests
{
    private static Task<ProviderResult?> Ok(string text) =>
        Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = text, SessionId = "s" });

    [Fact]
    public async Task AutoRevisao_EncontraProblema_ExecutorCorrigeAntesDaRevisaoOficial()
    {
        using var env = new TestEnv();
        env.Cfg.SelfReviewProvider = "codex";
        var autoRevisoes = 0;
        env.Codex.Script = (req, n, _) =>
        {
            if (req.Role == Role.Reviewer)
            {
                autoRevisoes++;
                Assert.Contains("AUTO-REVISÃO", req.Prompt);
                return Ok(autoRevisoes == 1 ? "{\"verdict\":\"changes\",\"notes\":\"falta tratar lista vazia\"}" : "{\"verdict\":\"approve\"}");
            }
            SimulatedProvider.Write(req, "a.txt", req.Prompt.Contains("falta tratar lista vazia") ? "corrigido\n" : "primeira\n");
            return Ok("{\"summary\":\"feito\"}");
        };
        env.Claude.Script = (req, n, _) => Ok("{\"verdict\":\"approve\"}");
        env.Add("A", Complexity.Medium, new[] { "a.txt" }, review: true);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(1, autoRevisoes);                                    // uma auto-revisão por ciclo
        Assert.Contains("corrigido", env.Integrated("a.txt"));            // o executor corrigiu antes do Opus
        var oficial = Assert.Single(env.Claude.Calls);                     // o Opus revisou uma vez só, já corrigido
        Assert.Equal(Role.Reviewer, oficial.Req.Role);
        Assert.Equal(0, env.Store.Get("A")!.ReviewRejections);            // auto-revisão não conta como reprovação
    }

    [Fact]
    public async Task RevisaoIncremental_CorrecaoSoNosMesmosArquivos_RevisorVeSoACorrecao()
    {
        using var env = new TestEnv();
        var revisoes = new List<string>();
        env.Codex.Script = (req, n, _) =>
        {
            SimulatedProvider.Write(req, "a.txt", n == 1 ? "linha1\nlinha2\n" : "linha1\nlinha2-corrigida\n");
            return Ok("{\"summary\":\"feito\"}");
        };
        env.Claude.Script = (req, n, _) =>
        {
            revisoes.Add(req.Prompt);
            return Ok(revisoes.Count == 1 ? "{\"verdict\":\"changes\",\"notes\":\"corrija a linha 2\"}" : "{\"verdict\":\"approve\"}");
        };
        env.Add("A", Complexity.Medium, new[] { "a.txt" }, review: true);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(2, revisoes.Count);
        Assert.DoesNotContain("REVISÃO DA CORREÇÃO", revisoes[0]);        // 1ª revisão: completa
        Assert.Contains("REVISÃO DA CORREÇÃO", revisoes[1]);              // 2ª: só a correção
        Assert.Contains("corrija a linha 2", revisoes[1]);                 // com o que foi pedido
        Assert.Contains("linha2-corrigida", revisoes[1]);
    }

    [Fact]
    public async Task RevisaoIncremental_CorrecaoTocaArquivoNovo_VoltaARevisaoCompleta()
    {
        using var env = new TestEnv();
        var revisoes = new List<string>();
        env.Codex.Script = (req, n, _) =>
        {
            SimulatedProvider.Write(req, "a.txt", "a\n");
            if (n > 1) SimulatedProvider.Write(req, "b.txt", "novo\n");
            return Ok("{\"summary\":\"feito\"}");
        };
        env.Claude.Script = (req, n, _) =>
        {
            revisoes.Add(req.Prompt);
            return Ok(revisoes.Count == 1 ? "{\"verdict\":\"changes\",\"notes\":\"ajuste\"}" : "{\"verdict\":\"approve\"}");
        };
        env.Add("A", Complexity.Medium, new[] { "a.txt", "b.txt" }, review: true);

        var s = await env.NewEngine().RunAsync(CancellationToken.None);

        Assert.True(s.AllDone);
        Assert.Equal(2, revisoes.Count);
        Assert.DoesNotContain("REVISÃO DA CORREÇÃO", revisoes[1]);        // tocou arquivo novo: revisão completa
    }

    [Fact]
    public void Escalada_TresNiveis_CodexNormal_CodexComModeloDeEscalada_DepoisClaude()
    {
        var cfg = new OrqConfig { EscalarModeloAposReprovacoes = 2, EscalarAposReprovacoes = 3 };
        cfg.Providers["codex"] = new ProviderConfig { Models = new(StringComparer.OrdinalIgnoreCase) { ["medium"] = "gpt-6.1-sol", ["escalation"] = "gpt-6.1-sol@xhigh" } };
        cfg.Providers["claude"] = new ProviderConfig { Models = new(StringComparer.OrdinalIgnoreCase) { ["escalation"] = "claude-opus-4-8" } };
        var t = new TaskItem { Id = "E", Complexity = Complexity.Medium, Provider = "codex" };
        var disp = new[] { "claude", "codex" };
        string Modelo(string p) => cfg.For(p).ModelFor(Role.Developer, t.Complexity, Router.Escalada(t, cfg) || Router.EscaladaDeModelo(t, cfg))!;

        t.ReviewRejections = 1;
        Assert.Equal("codex", Router.Candidates(t, Role.Developer, null, cfg, disp)[0]);
        Assert.Equal("gpt-6.1-sol", Modelo("codex"));

        t.ReviewRejections = 2;   // 2ª: Codex com raciocínio máximo
        Assert.Equal("codex", Router.Candidates(t, Role.Developer, null, cfg, disp)[0]);
        Assert.Equal("gpt-6.1-sol@xhigh", Modelo("codex"));

        t.ReviewRejections = 3;   // 3ª: só o Claude, com o modelo de escalada
        Assert.Equal(new[] { "claude" }, Router.Candidates(t, Role.Developer, null, cfg, disp));
        Assert.Equal("claude-opus-4-8", Modelo("claude"));
    }

    [Fact]
    public void Codex_ModeloComEsforco_ViraArgumentoDeRaciocinio()
    {
        var req = new AgentRequest { Prompt = "x", Cwd = ".", Role = Role.Developer };
        var args = CodexProvider.BuildArgs(req, "gpt-6.1-sol@xhigh");
        var i = args.IndexOf("-m");
        Assert.Equal("gpt-6.1-sol", args[i + 1]);
        Assert.Contains("model_reasoning_effort=xhigh", args);
    }
}
