using Orq.Core;

namespace Orq.Tests;

public class CorrigirTests
{
    [Theory]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Interrupted)]
    [InlineData(TaskState.Blocked)]
    [InlineData(TaskState.Running)]
    [InlineData(TaskState.Pending)]
    public void Instrucao_DevolveParaDevELimpaResponsavel(TaskState state)
    {
        using var env = new TestEnv();
        var t = env.Add("A", Complexity.Medium, new[] { "a.txt" });
        t.State = state;
        t.Phase = Phase.Test;
        t.Attempts = 3;
        t.Owner = "codex";
        t.RunnerPid = int.MaxValue;
        t.SelfReviewDone = true;
        t.Sessions["codex:Developer"] = "sessao";
        env.Store.Save(t);

        CliCommands.Corrigir(env.Store, "A", "Corrija o diagnóstico indicado.", null);
        env.Restart();

        t = env.Store.Get("A")!;
        Assert.Equal("Instrução do coordenador:\nCorrija o diagnóstico indicado.", t.Feedback);
        Assert.Equal(Phase.Dev, t.Phase);
        Assert.Equal(TaskState.Pending, t.State);
        Assert.Null(t.Owner);
        Assert.Null(t.RunnerPid);
        Assert.Equal(0, t.Attempts);
        Assert.False(t.SelfReviewDone);
        Assert.Equal("sessao", t.Sessions["codex:Developer"]);
        Assert.Equal("corrigir", Assert.Single(env.Store.Events()).Kind);
    }

    [Fact]
    public void FrenteViva_RecusaSemForcar()
    {
        using var env = new TestEnv();
        var t = env.Add("A", Complexity.Medium, new[] { "a.txt" });
        t.State = TaskState.Running;
        t.RunnerPid = Environment.ProcessId;
        env.Store.Save(t);

        var ex = Assert.Throws<InvalidOperationException>(() => CliCommands.Corrigir(env.Store, "A", "Corrija.", null));
        Assert.Contains("pare a frente antes", ex.Message);
        Assert.Equal(TaskState.Running, env.Store.Get("A")!.State);
        Assert.Empty(env.Store.Events());

        Assert.Equal(TaskState.Pending, CliCommands.Corrigir(env.Store, "A", "Corrija.", null, forcar: true).State);
    }

    [Fact]
    public void Arquivo_LeInstrucaoCompletaEmUtf8()
    {
        using var env = new TestEnv();
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        var path = Path.Combine(env.Root, "instrução.md");
        const string instruction = "# Correção\nPreserve a ação e o diagnóstico.\nSegunda linha.";
        File.WriteAllText(path, instruction);
        CliCommands.Corrigir(env.Store, "A", null, path);
        Assert.Equal("Instrução do coordenador:\n" + instruction, env.Store.Get("A")!.Feedback);
    }

    [Fact]
    public void TarefaInexistente_RetornaErro()
    {
        using var env = new TestEnv();
        var ex = Assert.Throws<InvalidOperationException>(() => CliCommands.Corrigir(env.Store, "ausente", "Corrija.", null));
        Assert.Contains("não existe", ex.Message);
        Assert.Empty(env.Store.All());
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("texto", "arquivo.md")]
    [InlineData("   ", null)]
    public void EntradaInvalida_NaoAlteraTarefa(string? texto, string? arquivo)
    {
        using var env = new TestEnv();
        env.Add("A", Complexity.Medium, new[] { "a.txt" });
        Assert.Throws<ArgumentException>(() => CliCommands.Corrigir(env.Store, "A", texto, arquivo));
        Assert.Null(env.Store.Get("A")!.Feedback);
    }

    [Fact]
    public async Task Retomada_ExecutaDevComInstrucaoAntesDeTestar()
    {
        using var env = new TestEnv();
        env.Codex.Delay = TimeSpan.Zero;
        var t = env.Add("A", Complexity.Medium, new[] { "a.txt" });
        t.State = TaskState.Failed;
        t.Phase = Phase.Test;
        t.Attempts = env.Cfg.MaxAttemptsPerTask;
        t.Sessions["codex:Developer"] = "sessao-anterior";
        env.Store.Save(t);
        env.Cfg.Context = "Regra obrigatória do projeto.";
        env.Cfg.TestCommand = "findstr /c:\"corrigido\" a.txt";
        env.Codex.Script = (req, _, _) =>
        {
            Assert.Contains("Instrução do coordenador:\nAplique a correção exata.", req.Prompt);
            Assert.Contains(env.Cfg.Context, req.Prompt);
            SimulatedProvider.Write(req, "a.txt", "corrigido\n");
            return Task.FromResult<ProviderResult?>(new ProviderResult { Outcome = Outcome.Success, Text = "feito" });
        };
        CliCommands.Corrigir(env.Store, "A", "Aplique a correção exata.", null);

        Assert.True((await env.NewEngine().RunAsync(CancellationToken.None)).AllDone);
        Assert.Equal("sessao-anterior", Assert.Single(env.Codex.Calls).Req.ResumeSessionId);
    }
}
