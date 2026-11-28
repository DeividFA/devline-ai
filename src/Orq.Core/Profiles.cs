using System.Text;

namespace Orq.Core;

/// <summary>Perfis de agente: são funções (instruções + permissões), não processos permanentes.</summary>
public sealed record Profile(Role Role, string Name, bool ReadOnly, string Instructions);

public static class Profiles
{
    private static readonly Dictionary<Role, Profile> All = new()
    {
        [Role.Requirements] = new(Role.Requirements, "Analista de requisitos", true,
            "Você é analista de requisitos. Transforme o objetivo em requisitos verificáveis, premissas e critérios de aceite objetivos. Não escreva código."),
        [Role.Impact] = new(Role.Impact, "Analista de impacto e compatibilidade", true,
            "Você é analista de impacto. Aponte arquivos e contratos afetados, riscos de compatibilidade e a ordem segura das mudanças. Não escreva código."),
        [Role.Developer] = new(Role.Developer, "Desenvolvedor", false,
            "Você é desenvolvedor. Implemente exatamente o que a tarefa pede, com a menor mudança que atenda aos critérios de aceite."),
        [Role.Tester] = new(Role.Tester, "Testador", false,
            "Você é testador. Escreva ou ajuste testes automatizados que comprovem os critérios de aceite. Não altere código de produção."),
        [Role.Reviewer] = new(Role.Reviewer, "Code Reviewer", true,
            "Você é revisor de código. Procure defeitos reais de correção, segurança e aderência aos critérios de aceite. Ignore preferências de estilo."),
        [Role.Integrator] = new(Role.Integrator, "Integrador", false,
            "Função determinística do orquestrador: atualiza o branch da tarefa, roda os testes e faz o merge no branch de integração."),
    };

    public static Profile For(Role role) => All[role];
    public static IEnumerable<Profile> List() => All.Values;
}

/// <summary>Escolhe a ordem de provedores por perfil, complexidade e custo.</summary>
public static class Router
{
    public static bool OllamaEligible(TaskItem t, Role role) => role switch
    {
        Role.Requirements or Role.Impact => t.Complexity != Complexity.Complex,
        Role.Developer or Role.Tester => t.Complexity == Complexity.Simple && t.Files.Count == 1 && Scope.IsLiteral(t.Files[0]),
        _ => false,
    };

    /// <summary>Nível intermediário: mesmo provedor, modelo de escalada dele.</summary>
    public static bool EscaladaDeModelo(TaskItem t, OrqConfig cfg) => cfg.EscalarModeloAposReprovacoes > 0 && t.ReviewRejections >= cfg.EscalarModeloAposReprovacoes && !Escalada(t, cfg);

    public static bool Escalada(TaskItem t, OrqConfig cfg) => cfg.EscalarAposReprovacoes > 0 && t.ReviewRejections >= cfg.EscalarAposReprovacoes;

    public static List<string> Candidates(TaskItem t, Role role, string? avoid, OrqConfig cfg, IEnumerable<string> available)
    {
        var order = new List<string>();
        // Revisão usa só o provedor configurado (padrão: Claude). Se indisponível, espera sem trocar de provedor.
        if (role == Role.Reviewer) order.Add(string.IsNullOrWhiteSpace(cfg.RevisorProvider) ? "claude" : cfg.RevisorProvider);
        else
        {
            if (OllamaEligible(t, role)) order.Add("ollama");
            if (t.Complexity == Complexity.Complex) order.AddRange(new[] { "claude", "codex" });
            else order.AddRange(new[] { "codex", "claude" });
        }
        var have = new HashSet<string>(available, StringComparer.OrdinalIgnoreCase);
        // Escalada: depois de N reprovações da revisão, a correção é só do provedor de escalada (ex.: Claude Opus 4.8); se ele estiver indisponível, espera.
        if (role != Role.Reviewer && Escalada(t, cfg) && have.Contains(cfg.EscalationProvider) && cfg.For(cfg.EscalationProvider).Enabled)
            return new List<string> { cfg.EscalationProvider };
        var list = order.Where(n => have.Contains(n) && cfg.For(n).Enabled).ToList();
        if (role != Role.Reviewer && t.Provider is { } pref && have.Contains(pref) && cfg.For(pref).Enabled)
        {
            list.Remove(pref);
            list.Insert(0, pref);
            if (cfg.ExecucaoSoNoPreferido) return new List<string> { pref };
        }
        if (avoid != null && list.Count > 1 && list.Remove(avoid)) list.Add(avoid);
        return list;
    }
}

public static class Prompts
{
    /// <summary>Arquivos livres (config arquivosLivres): qualquer tarefa pode editar; o orquestrador não desfaz.</summary>
    public static IReadOnlyList<string> ArquivosLivres { get; set; } = Array.Empty<string>();

    private static string NotaLivres() => ArquivosLivres.Count == 0 ? "" :
        "Arquivos LIVRES (você pode e deve editar mesmo fora da sua reserva; o orquestrador não desfaz): " + string.Join(", ", ArquivosLivres) +
        ". Nos documentos de estado, altere só a seção do módulo que você tocou. Nos testes livres, corrija qualquer spec ou apoio que a sua mudança quebrar (texto, rótulo, rota, fluxo), mesmo de outra área.";

    private const string Rules = """
        Regras obrigatórias:
        - Trabalhe somente no diretório atual. Não publique, não faça deploy, não use git push.
        - Não leia nem escreva credenciais, tokens ou arquivos de configuração sensíveis.
        - Você PODE e DEVE rodar verificações locais rápidas e específicas do que alterou: compilação (npx ng build, dotnet build do projeto afetado) e testes unitários dos arquivos tocados (npx ng test --watch=false --include=<arquivo.spec.ts>, dotnet test --filter <Classe>). Não suba o servidor nem rode a suíte de ponta a ponta inteira: o orquestrador faz isso.
        - ANTES DE ENTREGAR, faça sua auto-revisão: releia o seu diff contra o objetivo, cada critério de aceite e cada item da correção pedida; confira os cenários de borda pertinentes (lista vazia, erro do servidor, carregando, dado inválido, celular 390px, teclado e foco, usuário sem permissão); corrija o que encontrar e só então responda.
        """;

    /// <summary>Revisão prescritiva: cada bloqueio vem com a correção exata, para o executor acertar na próxima volta.</summary>
    private const string RegrasDeRevisao = """
        REGRAS DA REVISÃO (o objetivo é resolver na próxima volta, com o mínimo de retrabalho):
        - Reprove ("changes") SÓ por BLOQUEIO: funcionalidade quebrada, critério de aceite não atendido, regressão, teste que deixou de provar o que dizia, segurança/isolamento entre organizações, acessibilidade grave. Preferência, estilo e melhoria NÃO reprovam: vão em "observacoes".
        - Liste TODOS os bloqueios de uma vez; não deixe nenhum para a rodada seguinte.
        - Para CADA bloqueio, escreva exatamente:
          (1) arquivo:linha;
          (2) o defeito em uma frase;
          (3) o cenário concreto em que falha;
          (4) CORREÇÃO EXATA: o trecho atual e o trecho novo (código), ou passos numerados sem ambiguidade — o executor vai aplicar literalmente;
          (5) como verificar: o teste/asserção/comando que prova a correção.
        - Se a correção exigir mexer em arquivo fora da lista de arquivos da tarefa, diga isso explicitamente e indique o arquivo.
        Responda somente com um objeto JSON:
        {"verdict":"approve" ou "changes","notes":"os bloqueios no formato (1)-(5), numerados","observacoes":"melhorias opcionais, se houver"}
        """;

    private const string Contract = """
        Ao terminar, responda de forma curta e finalize com um único objeto JSON:
        {"summary":"o que foi feito em 1-2 frases","decisions":["decisões técnicas relevantes"],"pending":["o que ficou pendente"],"autorrevisao":["cada critério/item pedido: como foi atendido e como verificou (comando/teste)"]}
        """;

    private static void Header(StringBuilder sb, TaskItem t, Profile p, IEnumerable<TaskItem> deps, string? context)
    {
        sb.AppendLine(p.Instructions).AppendLine();
        if (!string.IsNullOrWhiteSpace(context)) sb.AppendLine("# Contexto do projeto").AppendLine(context.Trim()).AppendLine();
        sb.AppendLine($"# Tarefa {t.Id}: {t.Title}");
        sb.AppendLine($"Objetivo: {t.Objective}");
        if (t.Acceptance.Count > 0)
        {
            sb.AppendLine("Critérios de aceite:");
            foreach (var a in t.Acceptance) sb.AppendLine($"- {a}");
        }
        var list = deps.Where(d => !string.IsNullOrWhiteSpace(d.Summary ?? d.Result)).ToList();
        if (list.Count > 0)
        {
            sb.AppendLine().AppendLine("Resultados de tarefas anteriores (contratos, não conversas):");
            foreach (var d in list)
                sb.AppendLine(Profiles.For(d.Role).ReadOnly && d.Result != null
                    ? $"- [{d.Id}] {d.Title} (análise completa):\n{Redactor.Head(d.Result, 7000)}"
                    : $"- [{d.Id}] {d.Title}: {Redactor.Head(d.Summary ?? d.Result, 800)}");
        }
    }

    public static string Dev(TaskItem t, IEnumerable<TaskItem> deps, IProvider provider, bool resumed, string? context = null)
    {
        var sb = new StringBuilder();
        if (resumed && t.Feedback != null)
        {
            if (!string.IsNullOrWhiteSpace(context)) sb.AppendLine("# Contexto do projeto").AppendLine(context.Trim()).AppendLine();
            // Sessão retomada: inclui o contexto atual e a correção, sem repetir o contrato completo.
            sb.AppendLine($"Continuação da tarefa {t.Id}. Corrija o problema abaixo, mantendo as mesmas regras e o mesmo formato de resposta.");
            if (t.Feedback.StartsWith("O revisor", StringComparison.Ordinal))
                sb.AppendLine("Corrija SOMENTE os pontos listados pelo revisor, aplicando a correção indicada em cada um. Não reanalise a tarefa nem altere outras partes; se algum ponto não puder ser feito como indicado, diga por quê no relatório.");
            sb.AppendLine().AppendLine(t.Feedback);
            if (ArquivosLivres.Count > 0) sb.AppendLine().AppendLine(NotaLivres());
            return sb.ToString();
        }

        Header(sb, t, Profiles.For(t.Role), deps, context);
        if (!provider.CanEditFiles)
        {
            sb.AppendLine().AppendLine($"Responda SOMENTE com o conteúdo completo e final do arquivo `{t.Files[0]}`, sem explicações e sem cercas de código.");
            if (t.Feedback != null) sb.AppendLine().AppendLine("Correção necessária: " + t.Feedback);
            return sb.ToString();
        }

        sb.AppendLine(t.Files.Count > 0
            ? "Arquivos reservados para você (não modifique nenhum outro): " + string.Join(", ", t.Files)
            : "Você tem reserva exclusiva do repositório nesta tarefa.");
        if (ArquivosLivres.Count > 0) sb.AppendLine(NotaLivres());
        if (t.Handoff != null) sb.AppendLine().AppendLine(t.Handoff);
        if (t.Feedback != null) sb.AppendLine().AppendLine("Correção necessária:").AppendLine(t.Feedback);
        sb.AppendLine().AppendLine(Rules);
        if (t.Skills.Count > 0)
            sb.AppendLine("- Exceção: você pode executar o script de busca da skill ui-ux-pro-max (somente leitura dos dados da skill).");
        sb.AppendLine(Contract);
        return sb.ToString();
    }

    public static string Analysis(TaskItem t, IEnumerable<TaskItem> deps, string? context = null)
    {
        var sb = new StringBuilder();
        Header(sb, t, Profiles.For(t.Role), deps, context);
        if (t.Feedback != null) sb.AppendLine().AppendLine("Correção necessária: " + t.Feedback);
        sb.AppendLine().AppendLine("Não modifique arquivos. Entregue a análise de forma objetiva, em no máximo 120 linhas.").AppendLine(Contract);
        return sb.ToString();
    }

    /// <summary>Auto-revisão do executor antes da revisão oficial: rigorosa e específica.</summary>
    public static string SelfReview(TaskItem t, string diff, string? context = null)
    {
        var sb = new StringBuilder(Review(t, diff, context));
        sb.AppendLine().AppendLine("ATENÇÃO: esta é a SUA AUTO-REVISÃO antes da revisão oficial do Opus. Seja rigoroso e específico como o revisor oficial será:");
        sb.AppendLine("- confira item por item o objetivo, os critérios de aceite e a última correção pedida; aponte o que falta ou está errado com arquivo:linha;");
        sb.AppendLine("- procure regressões, cenários de borda não tratados, textos diferentes dos exigidos, acessibilidade (nome acessível, foco, contraste), celular 390px e testes que não provam o que dizem;");
        sb.AppendLine("- só responda approve se você mesmo aprovaria no lugar do revisor oficial.");
        return sb.ToString();
    }

    /// <summary>Revisão incremental: só a correção, à luz do que o revisor pediu antes.</summary>
    public static string ReviewFix(TaskItem t, string diffCorrecao, string notasAnteriores, string? context = null)
    {
        var sb = new StringBuilder();
        Header(sb, t, Profiles.For(Role.Reviewer), Array.Empty<TaskItem>(), context);
        sb.AppendLine().AppendLine("REVISÃO DA CORREÇÃO. O restante da tarefa já foi revisado; você pediu estas mudanças:");
        sb.AppendLine(notasAnteriores);
        sb.AppendLine().AppendLine("Diff SOMENTE da correção (desde a sua revisão anterior):");
        sb.AppendLine("```diff").AppendLine(diffCorrecao).AppendLine("```");
        if (t.Tests.Count > 0) sb.AppendLine("Testes executados pelo orquestrador: " + t.Tests[^1]);
        sb.AppendLine().AppendLine("Verifique se cada mudança pedida foi atendida corretamente e se a correção não quebrou nada nos arquivos tocados. Não reabra pontos já aprovados fora deste diff. Não modifique arquivos. Responda somente com um objeto JSON:");
        sb.AppendLine(RegrasDeRevisao);
        return sb.ToString();
    }

    public static string Review(TaskItem t, string diff, string? context = null)
    {
        var sb = new StringBuilder();
        Header(sb, t, Profiles.For(Role.Reviewer), Array.Empty<TaskItem>(), context);
        sb.AppendLine().AppendLine("Diff a revisar (branch da tarefa contra a integração):");
        sb.AppendLine("```diff").AppendLine(diff).AppendLine("```");
        if (t.Tests.Count > 0) sb.AppendLine("Testes executados pelo orquestrador: " + t.Tests[^1]);
        sb.AppendLine().AppendLine("Não modifique arquivos. Responda somente com um objeto JSON:");
        sb.AppendLine(RegrasDeRevisao);
        return sb.ToString();
    }

    /// <summary>Resumo estruturado para troca de provedor: estado do trabalho, nunca histórico completo nem credenciais.</summary>
    public static string Handoff(TaskItem t, string from, Outcome why, string? partialText, string diffStat)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Handoff: continuação de trabalho interrompido");
        sb.AppendLine($"- Provedor anterior: {from} (motivo da troca: {why})");
        sb.AppendLine($"- Tentativa: {t.Attempts}");
        sb.AppendLine("- Alterações já salvas no diretório de trabalho (checkpoint em commit):");
        sb.AppendLine(string.IsNullOrWhiteSpace(diffStat) ? "  (nenhuma)" : "  " + Redactor.Head(diffStat, 800).Replace("\n", "\n  "));
        if (t.Decisions.Count > 0) sb.AppendLine("- Decisões técnicas: " + Redactor.Head(string.Join("; ", t.Decisions), 600));
        if (t.Tests.Count > 0) sb.AppendLine("- Último teste: " + Redactor.Head(t.Tests[^1], 400));
        if (t.Pending.Count > 0) sb.AppendLine("- Pendências: " + Redactor.Head(string.Join("; ", t.Pending), 400));
        if (!string.IsNullOrWhiteSpace(partialText)) sb.AppendLine("- Última nota do agente anterior: " + Redactor.Head(partialText, 600));
        sb.AppendLine("- Próxima ação: valide o que já existe nos arquivos reservados e continue a partir daí; não refaça o que estiver correto.");
        return sb.ToString();
    }
}
