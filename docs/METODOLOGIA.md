# O método da esteira

O DevLine AI usa uma esteira de tarefas pequenas: implementar, validar, revisar, corrigir só os itens listados, validar por completo e integrar. O método surgiu durante o desenvolvimento de um SaaS real (sistema de gestão: .NET 8 + Angular + PostgreSQL). [English](METHODOLOGY.md) · [README](../README.pt-BR.md) · [Arquitetura](ARQUITETURA.md) · [História](HISTORIA.md)

## Separar execução de julgamento

Use um modelo rápido e econômico para implementar e corrigir. Use um modelo mais forte para planejar e revisar. O planejamento ocorre fora do motor: escreva tarefas pequenas e detalhadas, com arquitetura definida, critérios de aceite concretos, dependências e reservas completas. O mesmo executor pode implementar e corrigir; são responsabilidades separadas, não necessariamente três modelos distintos.

Reserve todos os arquivos afetados, incluindo o `.ts` por trás de cada template Angular e os `.spec.ts` existentes. O complemento automático é uma proteção, não um substituto para um plano inicial completo. Mantenha mudanças pequenas o bastante para o revisor inspecionar um diff coerente. Skills e briefing podem levar instruções especializadas; `.orq/contexto.md` leva o contexto compartilhado do projeto.

Defina o executor em `provider` na tarefa, escolha `revisorProvider` separadamente e use `execucaoSoNoPreferido: true` para preservar a capacidade do revisor quando o executor atingir um limite. Para impedir escalada de provedor, use `escalarAposReprovacoes: 0`; a escalada de modelo é uma configuração separada. Modelos e orçamentos disponíveis determinam a configuração, não uma dupla fixa de marcas.

## Regras por área no contexto do projeto

Registre as regras de desenvolvimento de cada área em `.orq/contexto.md`. A CLI carrega o arquivo e inclui seu conteúdo em todo prompt de execução e de revisão, inclusive correções retomadas, auto-revisão e revisão incremental. Atualize-o com lições aprovadas por uma pessoa para todos os papéis receberem as mesmas regras do projeto.

Exemplo genérico de regra de front:

> Um comportamento = um componente; proibido montar tabela/modal/botão próprio na tela: use os componentes compartilhados. Código sem uso é apagado. Uma guarda automática no teste reprova violações. Se faltar recurso, evolua o componente compartilhado.

Escreva regras igualmente concretas para as demais áreas, indicando onde fica o código compartilhado e qual verificação automática garante a regra. Mantenha regras verificáveis, sem nomes de projeto ou cliente.

## Tornar a revisão executável

Peça todos os bloqueios numa única rodada. Cada bloqueio precisa de:

1. Arquivo e linha.
2. Defeito em uma frase.
3. Cenário concreto de falha.
4. CORREÇÃO EXATA: código atual/novo ou passos numerados sem ambiguidade.
5. Teste, asserção ou comando que comprove a correção.

Preferências de estilo e melhorias opcionais ficam em observações e não devem reprovar. Se a correção precisa de arquivo fora da reserva, o revisor deve nomeá-lo para o coordenador ajustar o contrato. O executor corrige SOMENTE os itens listados; deve explicar uma instrução impossível em vez de redesenhar a tarefa silenciosamente. Revise incrementalmente o diff da correção quando o escopo permitir. Isso reduziu negociações repetidas sobre correções no desenvolvimento; não substitui a validação.

## Adequar a validação ao ciclo

Execute specs focados na área afetada a cada correção com `quickTestCommand`. Execute a validação completa da aplicação na integração com `testCommand`. Os comandos pertencem à aplicação de destino e precisam preservar códigos de saída de falha. Antes do teste rápido, o motor tenta incorporar a integração mais recente para a validação refletir o trabalho recém-integrado.

Entregue ao agente a falha real. Anexe um snapshot de acessibilidade do Playwright no momento do erro (a “foto da página”) e imprima o erro no FIM. É uma visão estruturada da página, não necessariamente uma imagem. O motor guarda só os últimos 6.000 caracteres da saída de validação com falha; coloque diagnósticos longos antes da mensagem final que permite agir.

No ambiente original, o sandbox do agente não permitia publicar a aplicação. Um atendente de testes separado, fora do sandbox, recebia os arquivos de spec pedidos por `.orq-teste/pedido.txt` e escrevia `.orq-teste/resultado.txt`. Era uma integração da aplicação; o DevLine AI não inclui esse serviço, coletor de snapshots Playwright ou detector de texto antigo. Seus comandos de validação podem chamar ferramentas fornecidas por você.

Na SEGUNDA falha do mesmo teste, o coordenador diagnostica e registra a correção exata. Não continue gastando tentativas com diagnósticos incompletos. Um erro novo compreendido vira regra escrita no mesmo dia, no contexto de lições aprendidas passado aos agentes; a correção precisa chegar inteira ao agente.

## Autocorreção e ciclo de aprendizado

O motor devolve automaticamente falhas de validação e correções da revisão à execução, repetindo validação/revisão até a tarefa passar ou esgotar as tentativas. Esperas por capacidade e conflitos bloqueados podem pausar o ciclo. O executor corrige só o apontado. Entregue arquivo:linha, correção exata prescrita, snapshot da página no erro e exceções pertinentes do servidor pela revisão ou pelo comando de validação. Diagnósticos completos que permitam agir são essenciais: o motor guarda no máximo os últimos 6.000 caracteres da saída com falha, portanto imprima o erro depois do contexto extenso.

As proteções embutidas codificam lições de erros reais: auto-reserva de `.ts`/`.spec.ts` existente, detecção de reserva incompleta, cooldown pela mensagem, atualização com a integração antes do teste e retomada após reinício. Detectar texto antigo ainda esperado em testes foi outra regra corretiva na validação da aplicação original, não um recurso incluído no motor.

O arquivo de lições é uma memória mantida por uma pessoa. `.orq/contexto.md` entra em todo prompt de execução e revisão; correções retomadas também recebem o contexto atual e o novo retorno. O motor registra testes, violações, tentativas e eventos para inspeção. A pessoa pode comparar tarefas, identificar teste/arquivo e mensagem recorrentes e contar repetições; violações de reserva repetidas já informam contagens explicitamente. A pessoa escolhe qual observação vira lição e o texto exato. É correção por retorno com regras mantidas por humanos, não aprendizado autônomo ou IA que modifica a si própria.

Para uma versão futura, um relatório planejado de falhas recorrentes agruparia a mesma assinatura entre tarefas e mostraria teste/arquivo, contagens, IDs, mensagens de erro e sugestão de lição por `orq licoes` ou interface equivalente. A pessoa aprovaria, editaria ou descartaria a sugestão; só o texto aprovado entraria no arquivo de contexto. Agrupamento entre tarefas e esse fluxo de aprovação são itens de roadmap, não comandos atuais.

## Oito lições para levar aos prompts

1. **Texto visível é contrato de teste.** Ao mudar rótulo, título ou mensagem, atualize as asserções pertinentes na mesma tarefa. Globs de testes livres como `tests/e2e/**` podem permitir isso. Um detector na validação para texto antigo esperado pode informar arquivo:linha em segundos.
2. **Corrija padrões de interação no apoio compartilhado.** Se uma interação repetida mudar, atualize o helper em vez de remendar só um teste enquanto os demais continuam no comportamento antigo.
3. **Abas/etapas roláveis exigem ativação deliberada.** Ative pelo teclado e espere `aria-selected` antes de verificar o painel; não deduza seleção apenas pelo clique.
4. **Use localizadores por papel com nomes exatos.** Prefira papel acessível e nome acessível exato a buscas amplas por texto que possam atingir vários controles.
5. **Isole os dados de teste.** Dois workers num banco compartilhado não podem sobrescrever ou consumir registros um do outro. Dê aos testes dados independentes e limites de limpeza.
6. **Faça orçamento de altura no celular.** Confira a altura combinada de títulos, controles, navegação e conteúdo numa tela pequena, não apenas a ausência de vazamento horizontal.
7. **Sem shell, inspecione dependências de compilação.** Confira imports, declarações de componentes, inputs e tipos com cuidado; a verificação real do build continua com o coordenador.
8. **Relate toda mudança de texto visível.** Liste a redação de interface alterada na passagem de trabalho para validação e revisão identificarem as asserções afetadas.

## A concorrência segue a máquina

A esteira inicial permitia no máximo duas tarefas por vez: o executor implementa A; enquanto o revisor revisa A, o executor começa B; depois o executor aplica apenas as correções pedidas. Papéis separados ajudam a sobrepor trabalho útil sem pedir implementação ao revisor.

Mais tarde, a disputa por CPU tornou os testes instáveis. Uma tarefa por vez custava menos que as repetições causadas pela carga paralela, então o ajuste adotado foi `--concurrency 1`. Escolha concorrência pela estabilidade medida da validação, não pela quantidade máxima de modelos disponíveis. Com vários processos, use conjuntos disjuntos de tarefas e escopos; nomes de faixas não criam reservas globais.

O coordenador continua responsável por escopo, diagnósticos exatos de falhas repetidas, reparo manual de conflitos e decisão de incorporar a branch de integração local. A [História](HISTORIA.md) registra as observações e os números das execuções de desenvolvimento.
