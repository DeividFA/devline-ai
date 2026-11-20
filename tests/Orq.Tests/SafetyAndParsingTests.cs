using Orq.Core;

namespace Orq.Tests;

public class SafetyTests
{
    [Fact]
    public void RepositorioNaoAutorizado_EhBloqueado()
    {
        using var env = new TestEnv();
        var outro = new Guard(Path.Combine(env.Root, "vazio.txt"));

        var ex = Assert.Throws<PermissionDeniedException>(() => new Engine(env.Repo, env.Cfg, env.Store, env.Providers, outro));
        Assert.Contains("orq allow", ex.Message);
    }

    [Theory]
    [InlineData("push", "origin", "main")]
    [InlineData("push", "--force")]
    [InlineData("reset", "--hard", "HEAD~1")]
    [InlineData("clean", "-fdx")]
    [InlineData("remote", "add", "x", "http://x")]
    [InlineData("fetch")]
    [InlineData("pull")]
    [InlineData("branch", "-D", "main")]
    [InlineData("worktree", "remove", "x")]
    [InlineData("checkout", ".")]
    [InlineData("rebase", "main")]
    [InlineData("config", "--global", "user.name", "x")]
    public async Task GitDestrutivoOuDePublicacao_EhBloqueado(params string[] args)
    {
        using var env = new TestEnv();
        Assert.Throws<PermissionDeniedException>(() => Guard.CheckGit(args));
        await Assert.ThrowsAsync<PermissionDeniedException>(() => new Git().Run(env.Repo, args));
    }

    [Fact]
    public void GitNecessario_EhPermitido()
    {
        Guard.CheckGit(new[] { "worktree", "add", "-b", "orq/task-A", "x", "orq/integration" });
        Guard.CheckGit(new[] { "commit", "-q", "-m", "msg" });
        Guard.CheckGit(new[] { "merge", "--no-ff", "-m", "msg", "orq/task-A" });
        Guard.CheckGit(new[] { "restore", "--staged", "--worktree", "--source=HEAD", "--", "-f" });
    }

    [Fact]
    public void CaminhoForaDoWorktree_EhBloqueado()
    {
        var root = Path.Combine(Path.GetTempPath(), "orq-x");
        Assert.Throws<PermissionDeniedException>(() => Guard.EnsureInside(root, @"..\fora.txt"));
        Assert.Throws<PermissionDeniedException>(() => Guard.EnsureInside(root, @"C:\Windows\win.ini"));
        Assert.StartsWith(root, Guard.EnsureInside(root, @"src\ok.txt"));
    }

    [Theory]
    [InlineData("chave sk-abcdefghijklmnopqrstuvwxyz012345 aqui", "sk-abcdefghijklmnop")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz.0123", "abcdefghijklmnopqrstuvwxyz")]
    [InlineData("password=SuperSecreta123", "SuperSecreta123")]
    [InlineData("\"api_key\": \"valor-muito-secreto\"", "valor-muito-secreto")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdefghij")]
    public void Segredos_SaoRedigidos(string input, string secret)
    {
        var clean = Redactor.Clean(input);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains("[REDIGIDO]", clean);
    }

    [Fact]
    public void Eventos_NaoGuardamSegredos()
    {
        using var env = new TestEnv();
        env.Store.Event("A", "x", "falhou com token=abc123segredo456");
        Assert.DoesNotContain("abc123segredo456", env.Store.Events().Last().Message);
    }

    [Theory]
    [InlineData("a.txt", "a.txt", true)]
    [InlineData("a.txt", "b.txt", false)]
    [InlineData("src/", "src/x/y.cs", true)]
    [InlineData("src/*.cs", "src/a.cs", true)]
    [InlineData("src/*.cs", "docs/a.md", false)]
    [InlineData("src/**", "src/", true)]
    public void Reserva_DetectaSobreposicao(string a, string b, bool expected) =>
        Assert.Equal(expected, Scope.Overlap(a, b));

    [Fact]
    public void Agentes_RecebemPermissoesMinimas()
    {
        var write = new AgentRequest { Cwd = @"C:\wt", ReadOnly = false };
        var read = new AgentRequest { Cwd = @"C:\wt", ReadOnly = true };

        var claudeW = string.Join(' ', ClaudeProvider.BuildArgs(write, null));
        var claudeR = string.Join(' ', ClaudeProvider.BuildArgs(read, null));
        Assert.DoesNotContain("Bash", claudeW);
        Assert.DoesNotContain("PowerShell", claudeW);
        Assert.DoesNotContain("dangerously", claudeW);
        Assert.Contains("--permission-prompts none", claudeW);
        Assert.Contains("--tools Read,Grep,Glob,Edit,Write", claudeW);
        Assert.Contains("--tools Read,Grep,Glob", claudeR);
        Assert.DoesNotContain("Edit", claudeR);

        var codexW = string.Join(' ', CodexProvider.BuildArgs(write, null));
        var codexR = string.Join(' ', CodexProvider.BuildArgs(read, null));
        Assert.Contains("-s workspace-write", codexW);
        Assert.Contains("-s read-only", codexR);
        Assert.DoesNotContain("danger", codexW);
        Assert.DoesNotContain("bypass", codexW);
    }
}

public class ParsingTests
{
    [Fact]
    public void Claude_ExtraiUsoCustoESessao()
    {
        const string json = """
            {"type":"result","subtype":"success","is_error":false,"result":"pronto","session_id":"abc-123",
             "total_cost_usd":0.0123,"usage":{"input_tokens":10,"cache_creation_input_tokens":100,"cache_read_input_tokens":1000,"output_tokens":50}}
            """;
        var r = ClaudeProvider.Parse(new ProcResult(0, json, "", false));
        Assert.Equal(Outcome.Success, r.Outcome);
        Assert.Equal("pronto", r.Text);
        Assert.Equal("abc-123", r.SessionId);
        Assert.Equal(1110, r.Usage.Input);
        Assert.Equal(50, r.Usage.Output);
        Assert.Equal(0.0123, r.Usage.CostUsd);
        Assert.Equal(UsageKind.Known, r.Usage.Kind);
    }

    [Fact]
    public void Claude_LimiteDeUsoEhReconhecido()
    {
        const string json = """{"type":"result","subtype":"success","is_error":true,"result":"You've hit your limit · resets 3pm","session_id":"s1"}""";
        var r = ClaudeProvider.Parse(new ProcResult(1, json, "", false));
        Assert.Equal(Outcome.RateLimited, r.Outcome);
        Assert.Equal("s1", r.SessionId);
    }

    [Fact]
    public void Codex_ExtraiEventosJsonl()
    {
        const string jsonl = """
            {"type":"thread.started","thread_id":"t-9"}
            {"type":"turn.started"}
            {"type":"item.completed","item":{"id":"i1","type":"agent_message","text":"feito"}}
            {"type":"turn.completed","usage":{"input_tokens":200,"cached_input_tokens":150,"output_tokens":30}}
            """;
        var r = CodexProvider.Parse(new ProcResult(0, jsonl, "", false));
        Assert.Equal(Outcome.Success, r.Outcome);
        Assert.Equal("t-9", r.SessionId);
        Assert.Equal("feito", r.Text);
        Assert.Equal(200, r.Usage.Input);
        Assert.Equal(30, r.Usage.Output);
    }

    [Fact]
    public void Codex_FalhaDeLimiteEhReconhecida()
    {
        const string jsonl = """
            {"type":"thread.started","thread_id":"t-9"}
            {"type":"turn.failed","error":{"message":"You've hit your usage limit. Try again at 5:00 PM."}}
            """;
        var r = CodexProvider.Parse(new ProcResult(1, jsonl, "", false));
        Assert.Equal(Outcome.RateLimited, r.Outcome);
        Assert.Equal("t-9", r.SessionId);
    }

    [Theory]
    [InlineData("Claude AI usage limit reached", Outcome.RateLimited)]
    [InlineData("HTTP 429 Too Many Requests", Outcome.RateLimited)]
    [InlineData("Not logged in · Please run /login", Outcome.AuthError)]
    [InlineData("connect ECONNREFUSED 127.0.0.1:443", Outcome.Unavailable)]
    [InlineData("API Error: 529 overloaded", Outcome.Unavailable)]
    [InlineData("syntax error in file", Outcome.Failed)]
    public void Erros_SaoClassificados(string message, Outcome expected) =>
        Assert.Equal(expected, Classifier.Classify(message));

    [Fact]
    public void Timeout_EhReconhecido()
    {
        Assert.Equal(Outcome.Timeout, ClaudeProvider.Parse(new ProcResult(-1, "", "", true)).Outcome);
        Assert.Equal(Outcome.Timeout, CodexProvider.Parse(new ProcResult(-1, "", "", true)).Outcome);
    }

    [Fact]
    public void Contrato_UltimoJsonDoTextoEhExtraido()
    {
        var j = Json.LastObject("texto {não json} e depois\n```json\n{\"summary\":\"ok\",\"decisions\":[\"usar x\"]}\n```\nfim");
        Assert.Equal("ok", j!.Value.Str("summary"));
        Assert.Equal(new[] { "usar x" }, j.Value.StrList("decisions"));
    }
}
