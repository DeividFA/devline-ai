# Dos builds internos ao DevLine AI 1.0.0.0

O DevLine AI começou como um orquestrador .NET local fora das sessões dos agentes. Executava Git e testes, isolava cada tarefa de escrita em um Git worktree e guardava estado em SQLite para retomar após reinício. Os builds internos v2–v21 evoluíram durante o desenvolvimento de um SaaS real (sistema de gestão: .NET 8 + Angular + PostgreSQL), com cerca de cem tarefas de backend, painel administrativo Angular e Playwright.

Esta linha do tempo descreve problemas, mudanças e efeitos observados. Os números de builds internos são marcos de desenvolvimento, não versões públicas. [English](HISTORY.md) · [README](../README.pt-BR.md) · [Metodologia](METODOLOGIA.md) · [Arquitetura](ARQUITETURA.md) · [Changelog](../CHANGELOG.md)

## Linha do tempo

| Marco | Problema → mudança → efeito |
|---|---|
| v2–v8: coordenação local | Os agentes precisavam de coordenação durável fora das conversas → o orquestrador assumiu agendamento, Git, testes, worktrees e checkpoints em SQLite → trabalho interrompido podia continuar pelo estado guardado, sem um humano reconstruir cada sessão. |
| v9: encurtar a trava de merge | A trava cobria toda a validação e parava as frentes → passou a cobrir só operações Git → outras frentes podiam criar worktrees e continuar trabalhando durante a validação. |
| v11: escalada | Reprovações repetidas voltavam ao mesmo nível de execução → a escalada acrescentou modelo mais forte no mesmo provedor e depois outro provedor → correções difíceis ganharam um caminho configurável de reforço. Configurações posteriores permitiram desligar a escalada de provedor com `escalarAposReprovacoes: 0`, junto à execução só no preferido quando necessário. |
| v14: validar uma base atual | Tarefas validavam sobre base velha → incorporar a integração à tarefa antes da validação rápida → testes podiam incluir trabalho recém-integrado quando a atualização juntava sem conflito. |
| v16–v19: normalizar o contrato | Planos usavam sinônimos como `high` e `small` → normalizá-los para os valores aceitos → diferenças inofensivas de nomenclatura deixaram de quebrar a carga do plano. |
| v16–v19: completar reservas | Uma edição de template precisava de import no `.ts` fora da reserva; o orquestrador o desfazia e o build falhou seis vezes → reservar automaticamente o `.ts` e `.spec.ts` existente; gerar `reserva-incompleta` após reversões repetidas de um arquivo → a reserva faltante virou diagnóstico explícito, em vez de outra correção às cegas. |
| v16–v19: esperar capacidade real | Esperas fixas não correspondiam ao retorno do limite de uso → ler o horário de mensagens reconhecidas do provedor, mantendo uma espera de reserva → novas tentativas podiam seguir o retorno informado, não apenas um intervalo fixo. |
| v16–v19: liberar documentos compartilhados | Revisores exigiam um documento de estado fora da reserva da tarefa, criando impasse → adicionar `arquivosLivres` → tarefas puderam atualizar arquivos compartilhados aprovados sem a checagem de escopo desfazer o trabalho. |
| v16–v19: delimitar revisão e execução | Correções provocavam revisões amplas repetidas, e a execução por alternativa gastava capacidade do revisor → adicionar revisão incremental da correção, `revisorProvider` configurável, `execucaoSoNoPreferido` e “corrija SOMENTE os pontos listados pelo revisor” → responsabilidades de executor e revisor ficaram mais claras. |
| Prática de revisão | Revisão vaga ou aos poucos causava negociação repetida → exigir todos os bloqueios de uma vez, com arquivo:linha, defeito, cenário, CORREÇÃO EXATA antes/depois e verificação → observaram-se menos ciclos de retrabalho. |
| Prática da esteira | Sobreposição sem limite dificultava a supervisão → limitar inicialmente a duas tarefas: implementar A, revisar A enquanto implementa B, depois aplicar só as correções pedidas → execução e revisão podiam se sobrepor com trabalho em andamento limitado. |
| Limites da máquina | Disputa por CPU tornava testes instáveis sob carga paralela → depois executar uma tarefa por vez → evitar repetições instáveis custava menos que o ganho do paralelo. |
| Retorno de testes fora do sandbox | O agente não conseguia publicar a aplicação do sandbox e não via o estado da página na falha → anexar snapshot de acessibilidade Playwright e executar atendente externo por `.orq-teste/pedido.txt` → `.orq-teste/resultado.txt` → agentes podiam pedir specs focados e receber diagnósticos úteis. Eram ferramentas da aplicação. |
| v20: globs livres e contratos de teste | A redação da interface mudava enquanto testes esperavam o texto antigo → arquivos livres passaram a aceitar globs como `tests/e2e/**`; a validação da aplicação detectava texto antigo esperado com arquivo:linha → a mesma tarefa podia reparar testes afetados, e expectativas antigas eram identificadas em segundos. |
| v21: preservar o erro | Uma cauda de 1.500 caracteres e um snapshot impresso depois do erro cortavam a falha real → ampliar a cauda para 6.000 caracteres e colocar o erro depois do snapshot → o executor recebia o diagnóstico em vez de corrigir às cegas. |
| 1.0.0.0 — 2026-11-20 | A ferramenta interna precisava de identidade pública e referências utilizáveis → o DevLine AI manteve `orq`, adotou MIT, exemplos genéricos, metadados de versão e documentação em inglês/português → o repositório foi preparado para a primeira versão pública. |

O coletor de snapshots, o atendente de testes e o detector de texto antigo eram integrações da aplicação em desenvolvimento. Exemplificam o uso dos comandos de validação; não são recursos incluídos no orquestrador. Veja na [Metodologia](METODOLOGIA.md) as regras de trabalho e oito lições que surgiram deles.

## Observações até 20/11/2026

| Medida | Observação registrada |
|---|---|
| Tarefas planejadas | 101 |
| Tarefas integradas | 71+ |
| Chamadas de agentes | Aproximadamente 550 |
| Reprovações iniciais na validação rápida | 112 |
| Alterações iniciais fora da reserva desfeitas | 49 |
| Tempo médio posterior para integrar uma tarefa | Aproximadamente 34 minutos, uma tarefa por vez, com poucos ciclos de correção |

Os números descrevem um projeto e builds internos em evolução. Não são um experimento controlado antes/depois, benchmark geral ou garantia. As contagens iniciais de falhas e a média posterior se referem a etapas distintas do desenvolvimento; não se infere delas uma taxa numérica de melhoria.
