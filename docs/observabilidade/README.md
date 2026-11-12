# Observabilidade da Fase 4

Cada serviço reporta ao New Relic com nome próprio (`garageos-os`, `garageos-billing` e
`garageos-execucao`) e os rótulos `servico` e `env`. O agente tem a versão `10.57.0` fixada
nos três Dockerfiles.

## Uma OS de ponta a ponta

O `X-Correlation-Id` da requisição atravessa as mensagens:

1. O `CorrelationIdMiddleware` lê o cabeçalho, ou gera um id, e guarda em `Correlacao.Atual`.
2. Ao publicar ou enviar, um filtro do MassTransit carimba o id no cabeçalho `X-Correlation-Id`
   da mensagem.
3. Ao consumir, outro filtro devolve o id a `Correlacao.Atual`, coloca `CorrelationId`,
   `OrdemDeServicoId` e `MessageType` em cada linha de log e em atributos da transação no
   New Relic. As mensagens que o consumidor publica levam o mesmo id adiante.

Os eventos de negócio (`SagaEtapa`, `SagaCompensacao`, `OrdemDeServicoStatus`, `OrcamentoStatus`,
`ExecucaoStatus` e outros) também levam o `correlationId`.

Uma OS da saga, nos três serviços:

```sql
SELECT entity.name, context.MessageType, message FROM Log
WHERE context.CorrelationId = '<id>' SINCE 1 day ago
```

## Dashboard

[`dashboard-saga.json`](dashboard-saga.json): funil por etapa, compensações por motivo, tempo em
cada status, requisições, p95 e erros por serviço, e duas consultas para rastrear uma OS.

Para importar: New Relic → **Dashboards** → **Import dashboard**, cole o JSON e troque os
`"accountIds": [0]` pelo número da conta (aparece na URL do New Relic, em `account=`).
