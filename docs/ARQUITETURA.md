# Arquitetura

O DevLine AI 1.0.0.0 é um CLI (`orq`) e uma biblioteca .NET 10 voltados ao Windows. Roda fora das sessões dos agentes e controla Git, validação, agendamento e persistência em SQLite. [English](ARCHITECTURE.md) · [README](../README.pt-BR.md) · [Metodologia](METODOLOGIA.md) · [História](HISTORIA.md)

## Componentes

| Arquivo | Responsabilidade |
|---|---|
| `src/Orq.Cli/Program.cs` | Comandos, carga de configuração/contexto, cancelamento, simulação, laço de espera/retomada e versão. |
| `src/Orq.Core/Models.cs` | Contrato e estado das tarefas, configuração/padrões, contratos de chamada/retorno dos provedores e utilitários JSON. |
| `Engine.cs` | Agendamento, worktrees, escopo, checkpoints, chamadas/troca de provedor, testes, revisão e integração. |
| `Providers.cs` | Claude Code, Codex CLI, Ollama HTTP, provedores simulados e classificação de falhas. |
| `Profiles.cs` | Instruções por papel, roteamento, revisão prescritiva/incremental e handoff estruturado. |
| `Travas.cs` | Normalização de complexidade, reservas de arquivos Angular associados e leitura de horários de retorno reconhecidos. |
| `Safety.cs` | Autorização de repositórios, lista de Git permitido, caminhos confinados, redação de segredos e escopo/glob. |
| `Store.cs` | Tabelas SQLite `tasks`, `attempts`, `providers`, `events`; WAL e espera por ocupação de 15 segundos. |
| `Proc.cs` | Processos, saída redirecionada, tempo máximo/encerramento da árvore, wrapper Git e trava por arquivo. |
| `Doctor.cs` | Diagnóstico do ambiente/autenticação e relatórios de estado/uso/completo. |
| `tests/Orq.Tests` | Testes xUnit com repositórios temporários e provedores simulados; teste de integração da versão do CLI. |
| `scripts/watchdog.ps1` | Auxiliar opcional do Windows que reabre frentes nomeadas ausentes usando lançadores fornecidos. |

## Ciclo de uma tarefa

```text
Dev → Test → Review → Integrate → Done
 ↑      │       │         │
 └──────┴───────┴─────────┘  falha de teste / correção pedida / falha na validação completa
```

1. Cria `.orq/worktrees/<safe-id>` na branch `orq/task-<safe-id>`, a partir da integração. O worktree de integração é `.orq/worktrees/_integration`. Uma nova branch de integração começa em `baseBranch` ou `HEAD`, portanto o destino precisa ter um commit existente.
2. Dev completa reservas Angular e chama o executor com objetivo, critérios de aceite, escopo, resumos das dependências, contexto, skills e briefing opcional. Skills obrigatórias precisam existir no provedor escolhido e ser declaradas no JSON final.
3. A checagem de escopo desfaz alterações rastreadas e remove arquivos novos fora da reserva, exceto `arquivosLivres`. Reversões repetidas geram `reserva-incompleta`. O motor faz um commit de checkpoint. Diff vazio devolve a Dev.
4. Test tenta primeiro incorporar a integração à tarefa. Um conflito é abortado e registrado; a integração final o tratará. Executa `quickTestCommand`, se configurado, ou `testCommand`. Falha devolve a Dev com a saída.
5. A revisão pode ser pulada com `review: false`. Caso contrário, uma chamada opcional a `selfReviewProvider` antecede a revisão oficial. A oficial usa apenas `revisorProvider` (padrão `claude`); pode ser o mesmo provedor do executor se assim configurado. O prompt pede todos os bloqueios, cada um com arquivo:linha, cenário, correção exata e verificação. O motor consome `verdict` e `notes`; não valida estruturalmente cada item pedido na revisão.
6. Após reprovação, guarda commit revisado, arquivos e notas. A revisão incremental só vale para uma correção não vazia, dentro dos arquivos já revisados e de `revisaoIncrementalMaxArquivos`; senão, revisa todo o diff da tarefa. Reprovações oficiais incrementam os contadores de escalada e tornam a auto-revisão elegível novamente. Sessões retomadas do executor recebem a instrução de corrigir só o apontado.
7. Integrate incorpora a integração mais recente à tarefa. Conflito bloqueia a tarefa para reparo manual. Com comando rápido configurado, executa `testCommand` completo aqui; sem ele, repete a completa só se a atualização mudou HEAD. Falha na validação devolve a Dev.
8. Confere a integração novamente sob a trava de Git e incorpora a tarefa com `merge --no-ff`. Se a integração avançou durante a validação e tocou arquivos em comum, atualiza e valida de novo (até três rodadas por tentativa de integração). Uma atualização limpa sem arquivos alterados em comum pode seguir sem outra validação; interações entre arquivos ainda exigem validação final do sistema.

`requirements` e `impact` são análises somente leitura no worktree de integração. Os resultados são guardados e gravados em `.orq/artifacts/<safe-id>.md`; dependentes recebem a análise ou um resumo. A integração é determinística e não consome tokens de agentes.

## Agendamento, escopo e travas

Uma tarefa pendente/interrompida começa quando as dependências terminaram, há concorrência disponível no motor e ela não conflita com tarefas de escrita em execução. Padrões aceitam arquivos literais, prefixos de diretório terminados em `/`, `*` dentro de um segmento e `**` entre diretórios. `files` vazio significa exclusividade do repositório no agendamento por escopo.

Quando ambas as tarefas de escrita têm faixa, a mesma faixa conflita e faixas distintas ignoram a sobreposição de arquivos. Se alguma faixa não estiver definida, decide o escopo. Declare escopos disjuntos para faixas distintas. As reservas Angular associadas são completadas durante Dev; inclua-as no plano original para o agendamento inicial ser preciso.

Arquivos livres ignoram a restrição de escopo; o motor não os adiciona automaticamente às reservas. Conflitos de merge neles exigem reparo manual. Eles não são um mecanismo global de coordenação.

O agendador e os semáforos dos provedores pertencem a cada instância do motor. Vários processos compartilham SQLite e `.orq/git.lock`, não um agendador global atômico de aquisição de tarefas ou reservas. Dê às frentes conjuntos `--only` disjuntos e trabalhos sem sobreposição. A recuperação confere PID registrado e nome do processo antes de retomar uma tarefa em execução pertencente a outra frente viva.

A trava por arquivo entre processos protege operações curtas de Git na criação de worktrees, atualização da integração e merges. Validações longas rodam fora dela. Um semáforo de integração separado serializa a integração dentro de uma instância. A nova conferência de HEAD da integração coordena processos concorrentes.

## Roteamento e modelos

| Trabalho | Ordem normal |
|---|---|
| Análise de requisitos/impacto simples ou média | ollama → codex → claude |
| Developer/tester simples com um arquivo literal | ollama → codex → claude |
| Outra execução simples/média | codex → claude |
| Execução complexa | claude → codex |
| Revisão oficial | Só `revisorProvider`; indisponível significa esperar |

O `provider` registrado e habilitado da tarefa vai para a frente; `execucaoSoNoPreferido` restringe a execução a ele. A escalada de provedor prevalece quando seu destino está habilitado e registrado. `escalarModeloAposReprovacoes` seleciona `models.escalation` no provedor normal antes de `escalarAposReprovacoes` trocar de provedor; zero desliga cada limiar separadamente.

A resolução do modelo tenta escalada para execução, ou `review` para revisores, depois complexidade e finalmente `model`. O Codex separa `model@effort` em `-m model` e `-c model_reasoning_effort=effort`. Ollama só retorna texto; numa escrita elegível o motor grava o resultado no único arquivo literal.

## Limites, retorno e persistência

| Resultado | Ação |
|---|---|
| `Success` | Continua a fase. |
| `Failed` | Descarta a sessão possivelmente inválida e devolve falha do trabalho para outra tentativa. |
| `RateLimited` | Checkpoint/handoff; espera até horário de retorno reconhecido ou `cooldownMinutes`, depois permite nova tentativa. |
| `AuthError` | Checkpoint/handoff; espera de 24 horas; login exige intervenção manual. |
| `Unavailable` | Checkpoint/handoff; espera de cinco minutos. |
| `Timeout` | Checkpoint/handoff; sem espera fixa. |

As esperas são guardadas por rótulo de provedor/modelo. A leitura de horário reconhece `resets …am/pm` e `try again at … AM/PM` em formato de 12 horas, usa o horário local da máquina e acrescenta um minuto. Não interpreta datas arbitrárias ou fusos nomeados. Caso contrário, vale a espera configurada. A troca de provedor considera apenas os candidatos do roteamento; sem candidato, a tarefa fica interrompida. `--wait` repete execuções enquanto houver tarefas interrompidas; o cálculo de espera do CLI consulta os nomes base dos provedores, portanto esperas por modelo podem ser consultadas periodicamente, em vez de aguardar exatamente o horário final.

O SQLite persiste fase/estado, tentativas, histórico de revisão, IDs de sessão, esperas dos provedores e eventos. IDs de sessão são por provedor/papel; Claude e Codex podem retomá-los. Handoffs resumem checkpoints, decisões, testes, pendências e uma nota final curta, sem repetir conversas inteiras. Tarefas concluídas permanecem concluídas ao recarregar o plano. Tarefas em execução abandonadas viram interrompidas após reinício; `retry` zera tentativas de uma tarefa parada e não concluída.

Contadores de orçamento pertencem a uma invocação e são verificados antes das chamadas, com base em chamadas concluídas, custo informado e tokens registrados. Chamadas concorrentes podem ultrapassar os limiares; custo desconhecido é zero. Chamadas bem-sucedidas sem uso informado têm estimativa pelo tamanho do texto. `--wait` cria outro motor com contadores novos. Relatórios identificam a origem do uso e deixam os saldos de assinatura como desconhecidos.

A validação roda no worktree por `cmd.exe /d /c`. Falhas guardam os últimos 6.000 caracteres redigidos de stdout seguida de stderr; sucessos guardam 200. Coloque snapshots antes do erro final. O snapshot Playwright, o detector de texto antigo e o atendente por arquivos descritos na [História](HISTORIA.md) eram ferramentas da aplicação, não serviços internos.

## Arquivos locais e limites de segurança

O CLI carrega `.orq/config.json`; `.orq/contexto.md` fornece contexto de execução aos prompts. `.orq/orq.db` guarda estado; `.orq/report.md`, relatórios; `.orq/capturas/<safe-id>`, capturas opcionais da validação para tarefas com skills. O motor exclui `.orq/` no repositório de destino. Branches e worktrees são mantidos para inspeção/limpeza manual.

`orq allow` registra autorização explícita em `%USERPROFILE%\.orq\repos-autorizados.txt`. O wrapper Git só permite `rev-parse`, `rev-list`, `status`, `add`, `commit`, `worktree add/list`, `merge`, `diff`, `log`, `branch`, `restore` e `show`, recusando flags destrutivas. Não faz push, fetch, pull, reset ou remoção de worktrees. Essa lista vale para chamadas Git do motor, não para todo comando que um agente externo poderia executar.

Claude roda sem interação com Read/Grep/Glob e, para escrita, Edit/Write; sem ferramentas de shell. Codex usa seu sandbox read-only ou workspace-write. Ollama chama o endpoint HTTP configurado. Comandos de teste são configuração confiável do usuário. A redação filtra padrões comuns de token/senha em logs, erros, relatórios e handoffs; não garante reconhecer qualquer dado sensível. Doctor consulta autenticação pelas ferramentas sem ler arquivos de credenciais.

## Vigia opcional

`scripts/watchdog.ps1` preserva a finalidade do auxiliar: verificar processos `orq.exe` nomeados, reabrir lançadores existentes ausentes, registrar reinícios e deixar tarefas/processos intactos. Configure o Agendador de Tarefas do Windows para chamá-lo periodicamente, por exemplo a cada dez minutos:

```powershell
.\scripts\watchdog.ps1 -OrqDir 'C:\src\my-app\.orq' -Launchers @{ 'Lane A' = 'frente-A.cmd'; 'Lane B' = 'frente-B.cmd' }
```

As chaves precisam corresponder a `--nome` dos lançadores; um sufixo legado ` - ...` é ignorado. Os valores são caminhos relativos a `OrqDir` ou absolutos. Usa Explorer para abrir os lançadores, espera seis segundos entre reinícios, acrescenta registros em `vigia.log` e sai quando existe `vigia.pausado`. Não cria lançadores nem cadastra tarefa agendada. Use nomes consistentes para evitar frentes duplicadas.

## Escopo desta versão

Planos e scripts de validação são fornecidos externamente. A validação exige Windows; exemplos de caminhos Unix não implicam suporte multiplataforma. Conflitos compartilhados e limpeza continuam manuais. A inferência Claude/Codex usa seus serviços. Concorrência e orçamentos pertencem a cada instância; validar a integração final continua essencial com trabalho paralelo. Veja as [referências completas de configuração e plano](../README.pt-BR.md).
