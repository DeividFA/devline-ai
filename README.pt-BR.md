# DevLine AI

DevLine AI — uma esteira multiagente local que transforma agentes de programação com IA em uma equipe de desenvolvimento disciplinada: um implementa, outro revisa, um terceiro corrige apenas o que foi apontado.

.NET 10 | licença MIT | versão 1.1.0.0

[English](README.md) · [Arquitetura](docs/ARQUITETURA.md) · [Metodologia](docs/METODOLOGIA.md) · [História](docs/HISTORIA.md) · [Changelog](CHANGELOG.md)

## Por quê

Agentes de programação com IA erram, perdem contexto, repetem correções malsucedidas e atingem limites de uso. Um humano acaba supervisionando cada passagem de trabalho, repetindo testes e conciliando edições concorrentes. O DevLine AI transfere essa coordenação para um processo .NET local, fora das sessões dos agentes: ele controla agendamento, Git, validação, checkpoints e recuperação.

O comando continua sendo `orq`. Agentes são papéis e chamadas, não trabalhadores permanentes; os papéis de implementação e correção podem usar o mesmo modelo econômico, enquanto um modelo mais forte revisa.

## Como funciona

```text
Dev → Teste rápido → Revisão prescritiva → Validação completa → Merge na branch de integração
 ↑                          │
 └── Corrigir só o apontado ← mudanças pedidas
```

Falhas nos testes também devolvem a tarefa a Dev. Com `quickTestCommand` configurado, só esse comando roda antes da revisão; `testCommand` roda na integração. Sem comando rápido, `testCommand` roda antes da revisão e se repete na integração quando atualizar a tarefa muda seu HEAD. A ausência de comandos de teste pula a validação.

Cada tarefa de escrita usa um Git worktree e uma branch própria. Reservas de arquivos mantêm as mudanças dentro do escopo; `arquivosLivres` permite arquivos compartilhados ou globs como `tests/e2e/**`. Dependências ordenam tarefas, e faixas podem organizar frentes distintas. O trabalho aprovado chega à branch de integração local (padrão `orq/integration`).

Claude Code, Codex CLI e Ollama são roteados por papel e complexidade. Trabalhos pequenos elegíveis podem usar Ollama; trabalhos médios normalmente tentam Codex primeiro e complexos, Claude. O `provider` da tarefa sobrepõe essa preferência. A revisão usa `revisorProvider` (padrão `claude`), sem alternativa automática. Mapas de modelos selecionam `simple`, `medium`, `complex`, `review` e `escalation`; o Codex aceita `model@effort` e passa o esforço a `model_reasoning_effort`. Escolha identificadores de modelos disponíveis nas ferramentas instaladas.

Limites de uso geram checkpoints e esperas, usando o horário de retorno das mensagens de limite reconhecidas quando disponível. O SQLite guarda fases, sessões, tentativas e estado dos provedores para que `orq resume` continue após um reinício. O Ollama retorna texto; numa tarefa de escrita elegível, o motor o grava no único arquivo reservado. Claude e Codex usam seus respectivos serviços; “local” descreve a orquestração, não toda a inferência.

## Autocorreção e ciclo de aprendizado

Falhas de validação e mudanças pedidas na revisão voltam ao executor, que corrige o problema informado e passa pela revalidação automática até a aprovação, o fim das tentativas ou uma interrupção que exija capacidade ou ação manual. Correções de revisão se limitam SOMENTE aos bloqueios listados. O retorno útil inclui arquivo:linha, correção exata do revisor, snapshot da página no erro e exceções do servidor quando fornecidos pelo comando de validação. Preserve o erro completo que permite agir no fim: o retorno de falha é limitado aos últimos 6.000 caracteres, não é uma transcrição ilimitada.

Várias proteções são correções aprendidas com falhas reais e codificadas como regras determinísticas: auto-reserva Angular de `.ts`/`.spec.ts` existente, detecção repetida de reserva incompleta, cooldown lido da mensagem de limite, atualização com a integração antes do teste e retomada após reinício. Um detector de texto antigo em testes também ajudou o fluxo original; ele pertence à validação da aplicação e não é incluído no motor.

As lições continuam sob controle humano. Coloque as lições aprovadas em `.orq/contexto.md`; o CLI inclui esse contexto em todo prompt de execução e revisão. Prompts de correção retomados também recebem o contexto atual e o novo retorno. Resultados de testes, violações, tentativas e eventos armazenados permitem inspecionar erros repetidos: a pessoa pode identificar teste/arquivo, contar ocorrências, comparar IDs de tarefas e ler as mensagens registradas. A versão atual informa contagens explícitas de edições repetidas fora da reserva; o agrupamento automático entre tarefas está planejado abaixo. A PESSOA decide o que vira lição e escreve ou aprova o texto. A ferramenta não aprende novas regras autonomamente nem modifica o próprio código.

## Proteções (Travas)

- A normalização do plano aceita sinônimos de complexidade como `small` → `simple` e `high` → `complex`.
- Reservar template/estilo de um componente Angular também reserva seu `.ts` e seu `.spec.ts`, se o teste existir.
- Edições fora da reserva são desfeitas e registradas. Reversões repetidas do mesmo arquivo geram um evento `reserva-incompleta` para o coordenador corrigir a reserva.
- A branch de integração entra na tarefa antes do teste rápido quando o merge é limpo; conflitos não resolvidos bloqueiam a integração para reparo manual.
- A revisão incremental mostra só a correção quando todos os arquivos alterados já foram revisados e a quantidade está dentro de `revisaoIncrementalMaxArquivos`.
- A revisão prescritiva pede todos os bloqueios de uma vez: arquivo:linha, defeito, cenário de falha, correção exata antes/depois ou passos sem ambiguidade e verificação. Melhorias opcionais não bloqueiam a aprovação.
- Correções de revisão em sessões retomadas dizem explicitamente “corrija SOMENTE os pontos listados pelo revisor”.
- `execucaoSoNoPreferido` mantém a execução no provedor preferido da tarefa, habilitado e registrado, aguardando sua capacidade. A escalada ainda tem precedência; use `escalarAposReprovacoes: 0` para desligar a escalada de provedor.
- Escalada de modelo e de provedor têm limiares configuráveis separados. Uma auto-revisão opcional pode anteceder a revisão oficial.

## Início rápido

São necessários Windows, Git e SDK .NET 10. Os comandos de validação usam `cmd.exe`; esta versão é voltada ao Windows. Instale e autentique os CLIs dos agentes que pretende usar, ou execute Ollama para trabalhos locais elegíveis.

Na raiz deste repositório:

```powershell
dotnet build
dotnet test
Set-Alias orq (Join-Path $PWD 'src\Orq.Cli\bin\Debug\net10.0\orq.exe')
orq --version
orq doctor
```

`orq --version` imprime `DevLine AI 1.1.0.0`. `doctor` informa ferramentas, estado da autenticação, modelos locais, skills e concorrência sugerida. Ele verifica os três provedores e pode apontar ferramentas ausentes mesmo que você pretenda usar apenas parte deles.

Use um repositório Git de destino com um commit existente e árvore de trabalho limpa:

```powershell
orq allow --repo C:\src\my-app
orq init --repo C:\src\my-app
```

`init` cria `.orq/config.json` e `.orq/plano.exemplo.json`. Edite a configuração para selecionar provedores e adicionar seus comandos de validação. Salve este plano mínimo como `C:\src\my-app\.orq\plan.json`:

```json
{
  "tasks": [
    {
      "id": "T1",
      "title": "Documentar a aplicação",
      "objective": "Criar docs/USAGE.md com as instruções de inicialização local da aplicação.",
      "acceptance": ["As instruções correspondem aos comandos reais do repositório."],
      "role": "developer",
      "complexity": "medium",
      "files": ["docs/USAGE.md"],
      "provider": "codex",
      "review": true
    }
  ]
}
```

```powershell
orq run --repo C:\src\my-app --plan C:\src\my-app\.orq\plan.json --concurrency 1 --wait
orq status --repo C:\src\my-app
orq resume --repo C:\src\my-app --concurrency 1 --wait
orq retry --repo C:\src\my-app --task T1
orq corrigir --repo C:\src\my-app --task T1 --texto "Aplique a correção exata do coordenador."
orq resume --repo C:\src\my-app --concurrency 1 --wait
orq report --repo C:\src\my-app
```

`retry` reabre uma tarefa parada e não concluída, zerando as tentativas; ele não a executa. `resume` continua o plano armazenado. Ctrl+C salva o estado. `--simulate` usa provedores fictícios sem consumir tokens, mas ainda cria worktrees e commits locais reais: use um repositório descartável para ensaiar. `--sim-limit codex` simula um limite. `--only T1,T2` restringe uma frente a esses IDs; `--nome "Lane A"` dá nome a ela. `scripts/watchdog.ps1` pode reabrir lançadores nomeados existentes; veja [Arquitetura](docs/ARQUITETURA.md).

O motor cria commits e merges locais. Nunca faz push. Revise o resultado e então incorpore a branch de integração à sua branch quando decidir.

Use `corrigir` para registrar o diagnóstico do coordenador com o prefixo `Instrução do coordenador:`: o comando zera tentativas, runner/owner e devolve a tarefa não concluída a Dev/Pending. Assim, a fase Teste não sobrescreve a instrução antes do desenvolvimento. Informe exatamente uma opção: `--texto "<instrução>"` ou `--arquivo <caminho.md>`. Uma frente viva é recusada, salvo com `--forcar` explícito. O evento `corrigir` fica registrado; continue com `orq resume` para executar.

Falhas transitórias de rede salvam checkpoint e repetem automaticamente a mesma tarefa na mesma sessão do provedor. A espera cresce de 1 para 2, 4 e no máximo 8 minutos, com eventos `rede`. Após `redeMaxTentativas` novas tentativas, a tarefa fica Interrupted. Limites de uso continuam no fluxo separado de cooldown.

## Referência de configuração

A configuração fica em `.orq/config.json` no repositório de destino. O JSON usa camelCase; as chaves existentes em português continuam parte do contrato. Estas são todas as propriedades de `OrqConfig`, incluindo `Context`, usada apenas em execução.

| Chave | Tipo | Padrão | Finalidade |
|---|---|---|---|
| `baseBranch` | string ou null | `null` | Referência inicial de uma nova branch de integração; null usa `HEAD`. |
| `integrationBranch` | string | `orq/integration` | Branch local que recebe tarefas aprovadas. |
| `testCommand` | string ou null | `null` | Comando de validação completa no worktree; null/vazio pula a etapa. |
| `quickTestCommand` | string ou null | `null` | Validação focada antes da revisão; quando definido, a completa roda na integração. |
| `maxConcurrency` | inteiro | `2` | Tarefas simultâneas nesta instância do motor; `--concurrency` sobrepõe. |
| `taskTimeoutMinutes` | inteiro | `20` | Tempo máximo de cada chamada a um agente. |
| `testTimeoutMinutes` | inteiro | `10` | Tempo máximo de cada comando de validação. |
| `maxAttemptsPerTask` | inteiro | `3` | Máximo de tentativas de desenvolvimento/análise; chamada sem capacidade disponível não consome tentativa. |
| `cooldownMinutes` | inteiro | `30` | Espera após limite quando não há horário de retorno reconhecido. |
| `redeEsperaInicialMinutos` | inteiro | `1` | Espera inicial após falha de rede; dobra até o teto de 8 minutos. |
| `redeMaxTentativas` | inteiro | `6` | Novas tentativas após a chamada inicial com falha de rede, na mesma tarefa/provedor/sessão; esgotadas, a tarefa fica Interrupted. |
| `escalarAposReprovacoes` | inteiro | `2` | Reprovações da revisão oficial antes de escalar o provedor; `0` desliga. |
| `escalarModeloAposReprovacoes` | inteiro | `0` | Reprovações antes de usar o modelo de escalada do provedor atual, antes de escalar o provedor; `0` desliga. |
| `escalationProvider` | string | `claude` | Provedor usado após a escalada, quando habilitado e registrado. |
| `selfReviewProvider` | string ou null | `null` | Provedor opcional da auto-revisão; vazio desliga a chamada adicional. |
| `execucaoSoNoPreferido` | booleano | `false` | Restringe a execução ao `provider` habilitado e registrado da tarefa; escalada pode sobrepor. |
| `revisorProvider` | string | `claude` | Revisor oficial; a revisão aguarda quando ele está indisponível. |
| `revisaoIncrementalMaxArquivos` | inteiro | `6` | Máximo de arquivos alterados para revisão incremental; todos precisam ter sido revisados antes. |
| `arquivosLivres` | array de strings | `[]` | Arquivos/globs editáveis por qualquer tarefa sem reserva; conflitos compartilhados exigem resolução manual. |
| `skillRoots` | objeto provedor → caminho | `{}` | Diretórios de cada provedor contendo `<skill>/SKILL.md`. Use caminhos absolutos. |
| `context` (só em execução) | string ou null | `null` | `Context` é ignorada no JSON; o CLI carrega `.orq/contexto.md` para os prompts. Não a defina em config.json. |
| `limits` | objeto | valores abaixo | Orçamentos de uma invocação do motor. |
| `providers` | objeto | `claude`, `codex`, `ollama` | Configuração dos provedores; todos habilitados com uma chamada simultânea por padrão. |

Configurações internas dos orçamentos e provedores:

| Chave | Tipo | Padrão | Finalidade |
|---|---|---|---|
| `limits.maxCallsPerRun` | inteiro | `30` | Para de despachar chamadas quando a contagem registrada atinge esse valor. |
| `limits.maxCostUsdPerRun` | número | `5.0` | Para com base no custo informado em dólares; custos desconhecidos contam como zero. |
| `limits.maxTokensPerRun` | inteiro de 64 bits | `3000000` | Para com base nos tokens registrados de entrada + saída. |
| `providers.<name>.enabled` | booleano | `true` | Habilita o provedor no roteamento normal. |
| `providers.<name>.maxConcurrent` | inteiro | `1` | Chamadas simultâneas ao provedor por instância do motor (mínimo efetivo: 1). |
| `providers.<name>.model` | string ou null | `null`; Ollama: `gemma3:4b` | Modelo de reserva; null usa o padrão do CLI. |
| `providers.<name>.url` | string ou null | `null`; Ollama: `http://localhost:11434` | URL base da API Ollama; ignorada pelos provedores de CLI. |
| `providers.<name>.models` | objeto de strings | `{}` | Modelos por trabalho: `simple`, `medium`, `complex`, `review`, `escalation`. Chaves ausentes usam a complexidade e depois `model`. |

Os orçamentos são conferidos antes das chamadas e contam chamadas concluídas; trabalho concorrente pode ultrapassar um limiar. Uma nova invocação do motor, inclusive uma repetição de `--wait`, começa com contadores novos. São condições operacionais de parada, não limites de gasto pré-pagos. O uso é informado pelo provedor ou estimado; os saldos restantes das assinaturas são desconhecidos.

Exemplo de configuração (os scripts de validação pertencem à sua aplicação; crie-os antes de usar):

```json
{
  "baseBranch": "main",
  "integrationBranch": "orq/integration",
  "testCommand": "powershell -NoProfile -File scripts/validate.ps1",
  "quickTestCommand": "powershell -NoProfile -File scripts/validate.ps1 -Quick",
  "maxConcurrency": 1,
  "taskTimeoutMinutes": 20,
  "testTimeoutMinutes": 10,
  "maxAttemptsPerTask": 3,
  "cooldownMinutes": 30,
  "redeEsperaInicialMinutos": 1,
  "redeMaxTentativas": 6,
  "escalarAposReprovacoes": 0,
  "escalarModeloAposReprovacoes": 0,
  "escalationProvider": "claude",
  "selfReviewProvider": null,
  "execucaoSoNoPreferido": true,
  "revisorProvider": "claude",
  "revisaoIncrementalMaxArquivos": 6,
  "arquivosLivres": ["docs/PROJECT_STATE.md", "tests/e2e/**"],
  "skillRoots": {
    "claude": "C:\\src\\skills\\claude",
    "codex": "C:\\src\\skills\\codex"
  },
  "limits": {
    "maxCallsPerRun": 30,
    "maxCostUsdPerRun": 5.0,
    "maxTokensPerRun": 3000000
  },
  "providers": {
    "claude": { "enabled": true, "maxConcurrent": 1, "model": null, "models": {} },
    "codex": { "enabled": true, "maxConcurrent": 1, "model": null, "models": {} },
    "ollama": { "enabled": false, "maxConcurrent": 1, "model": "gemma3:4b", "url": "http://localhost:11434", "models": {} }
  }
}
```

Caminhos como `C:\src\my-app` e `~/projects/my-app` são exemplos genéricos; caminhos de configuração não são expandidos pelo shell. Em particular, substitua `~` por um diretório absoluto antes de usá-lo em `skillRoots`.

## Referência do plano

Um plano é um objeto JSON com um array `tasks`. Reutilizar um ID preserva tarefas concluídas/em execução; recarregar atualiza o contrato das demais tarefas existentes sem zerar seu estado. Use `retry` quando necessário.

| Campo da tarefa | Tipo / padrão | Significado |
|---|---|---|
| `id` | string / vazia | Identificador obrigatório não vazio; use IDs únicos seguros para nomes de branches/diretórios. |
| `title` | string / vazia | Rótulo curto da tarefa. |
| `objective` | string / vazia | Resultado concreto pedido. |
| `acceptance` | array de strings / `[]` | Critérios de aceite verificáveis. |
| `role` | string / `developer` | `requirements`, `impact` são análises somente leitura; `developer`, `tester` escrevem. `reviewer` e `integrator` também existem no enum, mas normalmente pertencem às fases do motor. |
| `complexity` | string / `medium` | `simple`, `medium`, `complex`; seleciona roteamento e modelo. |
| `files` | array de strings / `[]` | Arquivos literais relativos ao repositório, diretórios terminados em `/` ou globs `*`/`**`. Vazio significa o repositório inteiro. |
| `dependsOn` | array de strings / `[]` | IDs de tarefas que precisam estar concluídas antes. |
| `review` | booleano / `true` | Exige revisão oficial antes da integração. |
| `provider` | string ou null / `null` | Executor preferido: `claude`, `codex`, `ollama`. |
| `lane` | string ou null / `null` | A mesma faixa nomeada serializa tarefas dentro de um motor; faixas nomeadas distintas ignoram a checagem de sobreposição, portanto distribua trabalhos disjuntos com cuidado. |
| `skills` | array de strings / `[]` | Nomes obrigatórios de pastas de skills em `skillRoots` do provedor executor; cada uma precisa estar instalada e declarada no JSON do agente. |
| `brief` | string ou null / `null` | Briefing adicional relativo a `.orq`, incluído nos prompts de execução novos. |

O complemento automático de reservas adiciona arquivos Angular associados durante Dev; reserve-os explicitamente também no plano para o agendamento conhecer o escopo completo desde o início. Arquivos livres são edições permitidas, não resolução automática de conflitos. Várias frentes compartilham estado e trava de Git, mas não oferecem um agendador global de reservas de tarefas/arquivos; use conjuntos `--only` e escopos disjuntos. Veja [Arquitetura](docs/ARQUITETURA.md).

## Comandos de validação

`quickTestCommand` e `testCommand` rodam no worktree da tarefa por `cmd.exe /d /c`, com `testTimeoutMinutes`. Os comandos pertencem à aplicação: o motor não seleciona specs nem publica sua aplicação por você. Saída com código diferente de zero ou tempo esgotado devolve a tarefa a Dev com os diagnósticos.

Coloque o erro real no FIM da saída do script de validação, depois de snapshots de acessibilidade e outros contextos extensos. O motor guarda os últimos **6.000 caracteres** da combinação stdout seguida de stderr em caso de falha (só os últimos 200 no sucesso). Preserve o código de saída de falha do comando. Um snapshot grande impresso depois do erro pode esconder o único diagnóstico útil.

No fluxo original, um snapshot de acessibilidade do Playwright e um atendente de testes por arquivos, fora do sandbox do agente, melhoraram o retorno. São integrações do lado da aplicação descritas em [Metodologia](docs/METODOLOGIA.md), não serviços incluídos.

## Resultados de um projeto real

Até 20/11/2026, um SaaS real (sistema de gestão: .NET 8 + Angular + PostgreSQL) forneceu 101 tarefas planejadas, 71+ integradas e cerca de 550 chamadas de agentes. As primeiras execuções registraram 112 falhas na validação rápida e 49 alterações fora da reserva desfeitas. Mais tarde, as tarefas eram integradas em cerca de 34 minutos em média, uma por vez, com poucos ciclos de correção. São observações de desenvolvimento de um projeto, não um benchmark controlado ou taxa de sucesso prometida; veja [História](docs/HISTORIA.md).

## Documentação e licença

Leia o [README em inglês](README.md), [História](docs/HISTORIA.md), [Metodologia](docs/METODOLOGIA.md), [Arquitetura](docs/ARQUITETURA.md) e [Changelog](CHANGELOG.md). Distribuído sob a [licença MIT](LICENSE).

## Palavras-chave

multi-agent, ai-coding, claude-code, codex-cli, llm-orchestration, code-review, devtools, dotnet

## Roadmap

**Versão futura — relatório de falhas recorrentes (planejado).** Quando a mesma assinatura de falha aparecer em tarefas diferentes, listar o caso com evidências: teste/arquivo, contagem de ocorrências, IDs das tarefas afetadas e mensagens de erro registradas. Um comando como `orq licoes` (ou interface equivalente) sugeriria um texto de lição. A pessoa aprova, edita ou descarta; só o texto aprovado entra no arquivo de contexto. Esse relatório e fluxo de aprovação não estão implementados na v1.1.0.0.
