using Orq.Core;
using Xunit;

namespace Orq.Tests;

/// <summary>Proteções contra erros que já custaram horas em um projeto real.</summary>
public class TravasTests
{
    [Fact]
    public void Plano_SinonimosDeComplexidadeSaoNormalizados()
    {
        var json = "{\"tasks\":[{\"id\":\"A\",\"complexity\":\"high\"},{\"id\":\"B\",\"complexity\":\"small\"},{\"id\":\"C\",\"complexity\":\"medium\"}]}";
        var plano = Json.From<PlanFile>(Travas.NormalizarPlano(json));
        Assert.Equal(Complexity.Complex, plano.Tasks[0].Complexity);
        Assert.Equal(Complexity.Simple, plano.Tasks[1].Complexity);
        Assert.Equal(Complexity.Medium, plano.Tasks[2].Complexity);
    }

    [Fact]
    public void Reserva_ComponenteComTemplateLevaOTsESpecExistente()
    {
        var raiz = Directory.CreateTempSubdirectory().FullName;
        Directory.CreateDirectory(Path.Combine(raiz, "web", "a"));
        File.WriteAllText(Path.Combine(raiz, "web", "a", "x.component.spec.ts"), "");
        var t = new TaskItem { Id = "T", Files = { "web/a/x.component.html", "web/a/y.component.scss" } };

        Assert.True(Travas.CompletarReserva(t, raiz));
        Assert.Contains("web/a/x.component.ts", t.Files);
        Assert.Contains("web/a/x.component.spec.ts", t.Files);   // existe: entra
        Assert.Contains("web/a/y.component.ts", t.Files);
        Assert.DoesNotContain("web/a/y.component.spec.ts", t.Files); // não existe: não entra
        Assert.False(Travas.CompletarReserva(t, raiz));          // idempotente
    }

    [Theory]
    [InlineData("You've hit your 5-hour limit · resets 8:10am (America/Sao_Paulo)", 8, 11)]
    [InlineData("You've hit your 5-hour limit · resets 5:10pm (America/Sao_Paulo)", 17, 11)]
    [InlineData("You've hit your usage limit. ... or try again at 10:35 AM.", 10, 36)]
    public void Limite_HorarioDeVoltaLidoDaMensagem(string msg, int hora, int minuto)
    {
        var agora = new DateTime(2026, 11, 19, 6, 0, 0);
        var volta = Travas.VoltaDoLimite(msg, agora);
        Assert.NotNull(volta);
        Assert.Equal(new DateTime(2026, 11, 19, hora, minuto, 0), volta);
    }

    [Fact]
    public void Limite_HorarioJaPassadoViraODiaSeguinte_ESemHorarioDevolveNulo()
    {
        var agora = new DateTime(2026, 11, 19, 9, 0, 0);
        Assert.Equal(new DateTime(2026, 11, 20, 8, 11, 0), Travas.VoltaDoLimite("resets 8:10am", agora));
        Assert.Null(Travas.VoltaDoLimite("rate limited, try later", agora));
    }

    [Fact]
    public void Execucao_SoNoPreferido_SemCairParaOutroProvedor_MasEscaladaContinua()
    {
        var cfg = new OrqConfig { ExecucaoSoNoPreferido = true, EscalarAposReprovacoes = 3 };
        var t = new TaskItem { Id = "X", Complexity = Complexity.Medium, Provider = "codex" };
        var disp = new[] { "claude", "codex" };
        Assert.Equal(new[] { "codex" }, Router.Candidates(t, Role.Developer, null, cfg, disp));
        t.ReviewRejections = 3;
        Assert.Equal(new[] { "claude" }, Router.Candidates(t, Role.Developer, null, cfg, disp));
        Assert.Equal(new[] { "claude" }, Router.Candidates(t, Role.Reviewer, null, cfg, disp));
    }

    [Fact]
    public void Revisor_Configuravel_PadraoClaude()
    {
        var t = new TaskItem { Id = "R", Complexity = Complexity.Medium, Provider = "codex" };
        var disp = new[] { "claude", "codex" };
        Assert.Equal(new[] { "claude" }, Router.Candidates(t, Role.Reviewer, null, new OrqConfig(), disp));
        Assert.Equal(new[] { "codex" }, Router.Candidates(t, Role.Reviewer, null, new OrqConfig { RevisorProvider = "codex" }, disp));
    }

    [Fact]
    public void ArquivosLivres_AceitamGlobSemAmpliarCaminhosLiterais()
    {
        var livres = new[] { "docs/PROJECT_STATE.md", "tests/e2e/**" };
        Assert.True(Scope.InScope(livres, "tests/e2e/login.spec.ts"));
        Assert.True(Scope.InScope(livres, "tests/e2e/billing/entries.ts"));
        Assert.True(Scope.InScope(livres, "docs/PROJECT_STATE.md"));
        Assert.False(Scope.InScope(livres, "docs/OUTRO.md"));
        Assert.False(Scope.InScope(livres, "web/src/app/x.ts"));
    }
}
