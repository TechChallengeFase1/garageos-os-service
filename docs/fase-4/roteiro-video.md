# Roteiro do vídeo da Fase 4

Até **15 minutos**, publicado no YouTube como **não listado**. Cobre os cinco itens pedidos:

| Item | Bloco |
|---|---|
| Fluxo completo de uma OS | 3 |
| Saga com falha e compensação | 4 |
| Deploy automático com os testes passando | 5 |
| Monitoramento no New Relic | 6 |
| Link no YouTube testado | depois da gravação |

Os comandos são para o **PowerShell**, rodados na pasta do repositório `garageos-os-service`. Precisam
da AWS CLI logada na conta do projeto e do Python.

---

## Antes de gravar

### No dia anterior, ou 1 hora antes: subir a infra (cerca de 40 minutos)

Na ordem. Em cada repositório: **Actions → workflow → Run workflow**, com os campos indicados, e aprovar
quando pedir.

| # | Repositório | Workflow | Campos | Tempo |
|---|---|---|---|---|
| 1 | `garageos-infra-database` | Terraform | `producao`, `apply` | 1 min |
| 2 | `garageos-infra-k8s` | Terraform | `producao`, `apply` | 15 a 20 min |
| 3 | `garageos-os-service`, `garageos-execucao-service`, `garageos-billing-service` | Infra | `apply` | 10 min, os três juntos |
| 4 | `garageos-lambda-auth` | Terraform | `apply` | 1 min |

Ou pelo terminal, um por vez:

```powershell
gh workflow run terraform.yml -R TechChallengeFase1/garageos-infra-database -f ambiente=producao -f acao=apply
gh workflow run terraform.yml -R TechChallengeFase1/garageos-infra-k8s -f ambiente=producao -f acao=apply
gh workflow run infra.yml -R TechChallengeFase1/garageos-os-service -f acao=apply
gh workflow run infra.yml -R TechChallengeFase1/garageos-execucao-service -f acao=apply
gh workflow run infra.yml -R TechChallengeFase1/garageos-billing-service -f acao=apply
gh workflow run terraform.yml -R TechChallengeFase1/garageos-lambda-auth -f acao=apply
```

### Credenciais do Mercado Pago (o segredo é recriado vazio)

Troque só o `<Access Token>`, copiado do painel do Mercado Pago, aba **Teste**:

```powershell
New-Item -ItemType Directory -Force C:\Temp | Out-Null
$json = @'
{"accessToken":"<Access Token>","publicKey":"<Public Key>","webhookSecret":"<assinatura secreta>"}
'@
[IO.File]::WriteAllText('C:\Temp\mp.json', $json)
aws secretsmanager put-secret-value --secret-id garageos-producao-billing/mercadopago --secret-string file://C:/Temp/mp.json --region us-east-1
Remove-Item C:\Temp\mp.json
```

### URL do webhook (o API Gateway volta com outro endereço)

```powershell
aws ssm get-parameter --name /garageos/producao/apigw/url --region us-east-1 --query Parameter.Value --output text
```

No painel do Mercado Pago, aba **Teste → Webhooks**: `<url>/billing/api/webhooks/mercadopago`, evento
**Pagamentos (legacy)**, salvar. Não gere uma assinatura nova.

### Deploy dos três serviços

**Actions → CD → Run workflow** em `garageos-os-service`, `garageos-execucao-service` e
`garageos-billing-service`. Confira os três de pé:

```powershell
$GW = aws ssm get-parameter --name /garageos/producao/apigw/url --region us-east-1 --query Parameter.Value --output text
foreach ($s in "os","billing","execucao") { "$s -> " + (Invoke-WebRequest "$GW/$s/health/ready" -UseBasicParsing).StatusCode }
```

Os três devem responder `200`.

### Gerar dados para o dashboard (30 minutos antes)

O dashboard mostra o último dia. Rode um caminho feliz e uma recusa, para os gráficos não aparecerem
vazios:

```powershell
python scripts\e2e\saga_e2e.py recusar
python scripts\e2e\saga_e2e.py iniciar
```

Pague o link do segundo comando (como no bloco 3) e rode `python scripts\e2e\saga_e2e.py continuar <osId>`.

### Abas abertas, nesta ordem

1. [docs/arquitetura-fase4.md](../arquitetura-fase4.md) no GitHub, com o diagrama renderizado.
2. [README do OS, seção da saga](../../README.md#saga-pattern-por-que-orquestração).
3. Janela anônima vazia, para o checkout do Mercado Pago.
4. Painel do Mercado Pago, **Pagamentos de teste**.
5. Actions do `garageos-os-service`, com o último run da `main` aberto.
6. O PR bloqueado: [garageos-execucao-service#6](https://github.com/TechChallengeFase1/garageos-execucao-service/pull/6).
7. SonarCloud, os três projetos.
8. New Relic: **APM & Services** e o dashboard **GarageOS - Saga da OS**.
9. Terminal na pasta do repositório, com a fonte aumentada.

**Cuidado na tela:** não mostre o Access Token, a assinatura secreta, a senha de Admin nem o `.env`. O
script e a coleção do Postman leem a senha sem exibi-la.

---

## O vídeo

| Bloco | Tempo | Conteúdo |
|---|---|---|
| 1 | 0:00 – 1:00 | Abertura |
| 2 | 1:00 – 3:00 | Arquitetura e por que a saga é orquestrada |
| 3 | 3:00 – 6:30 | Fluxo completo de uma OS |
| 4 | 6:30 – 9:00 | Falha e compensação |
| 5 | 9:00 – 11:30 | CI/CD e qualidade |
| 6 | 11:30 – 14:00 | New Relic |
| 7 | 14:00 – 15:00 | Encerramento |

### Bloco 1 — Abertura (0:00 – 1:00)

**Falar:** quem é o grupo, o desafio da Fase 4 (sair do monólito para microsserviços, com saga, Mercado
Pago, CI/CD por serviço e observabilidade) e o que vai ser mostrado.

### Bloco 2 — Arquitetura (1:00 – 3:00)

**Mostrar:** a aba 1, o diagrama geral.

**Falar:**
- Três serviços, cada um com o seu banco: OS e Execução em PostgreSQL, Billing em DynamoDB, que é o banco
  não relacional.
- Eles se falam por SNS e SQS, com MassTransit. A única chamada HTTP entre serviços é o OS conferindo
  peças no Execução na abertura da OS.
- Um só ponto de entrada: o API Gateway, com a Lambda de login por CPF e o authorizer de JWT. Atrás dele,
  um NLB e o Traefik, que escolhe o serviço pelo prefixo `/os`, `/billing` ou `/execucao`.

**Mostrar:** a aba 2, a seção da saga.

**Falar:** por que orquestrada e não coreografada:
- o fluxo é linear e já existia como máquina de estados;
- a compensação depende de onde a falha aconteceu;
- os prazos (aprovação e pagamento) precisam de um dono;
- o estado fica consultável num só lugar (`GET /saga`).

O custo aceito é o OS conhecer os contratos dos outros dois.

### Bloco 3 — Fluxo completo de uma OS (3:00 – 6:30)

**Rodar:**

```powershell
python scripts\e2e\saga_e2e.py iniciar
```

**Falar enquanto roda:** o script cadastra cliente, veículo, serviço e peça, abre a OS e pede o orçamento.
A partir daí é a saga:
1. o OS manda `GerarOrcamento` ao Billing;
2. o cliente aprova;
3. o Execução reserva as peças;
4. o Billing abre a cobrança no Mercado Pago.

Cada passo aparece com o tempo que levou.

**Mostrar:** abrir o link impresso na janela anônima. Entrar com a conta **compradora** de teste e pagar
com o cartão de teste do painel e o nome `APRO`. **O link vale 10 minutos**, e o prazo de pagamento da
saga também: se passar disso, a saga cancela sozinha.

**Falar:** o Mercado Pago avisa o Billing pelo webhook. O Billing confere a assinatura, consulta o
pagamento na API e publica `PagamentoConfirmado`. A saga manda o Execução iniciar.

**Rodar**, com o `osId` impresso:

```powershell
python scripts\e2e\saga_e2e.py continuar <osId>
```

**Mostrar:** a saída final.
- Histórico `Recebida → EmDiagnostico → AguardandoAprovacao → AguardandoPagamento → EmExecucao → Finalizada`
- Pagamento **Confirmado**, com o id do Mercado Pago
- Reserva **Consumida**

**Opcional:** o mesmo fluxo pelo Postman, com a coleção
`Code/Postman/GarageOS.Fase4.postman_collection.json`. A pasta `06 - Cliente` mostra o login por CPF e o
acompanhamento público da OS.

### Bloco 4 — Falha e compensação (6:30 – 9:00)

**Rodar:**

```powershell
python scripts\e2e\saga_e2e.py iniciar
```

Pague o link do mesmo jeito, com `APRO`, e então:

```powershell
python scripts\e2e\saga_e2e.py falhar <osId>
```

**Falar:** a execução falha (`Motor travado`). A saga desfaz o que tinha acontecido, na ordem inversa:
1. estorna o pagamento no Mercado Pago;
2. libera as peças reservadas;
3. cancela o orçamento.

**Mostrar:** a saída, com OS **Cancelada**, Pagamento **Estornado** e Reserva **Liberada**. Depois o painel
do Mercado Pago, em **Pagamentos de teste**, com o pagamento devolvido.

**Falar:** mensagem repetida não causa efeito duplicado:
- outbox e inbox no PostgreSQL do OS e do Execução;
- idempotência por chave de negócio no Billing;
- o estorno usa uma chave de idempotência, então um aviso repetido não devolve o dinheiro duas vezes.

**Opcional, sem pagamento:** `python scripts\e2e\saga_e2e.py recusar`, em que o cliente recusa e a OS é
cancelada na hora.

### Bloco 5 — CI/CD e qualidade (9:00 – 11:30)

**Mostrar:** a aba 5, o último run da `main`.

**Falar:** um pipeline por serviço:
1. build e testes: unitários, integração com Testcontainers e, no OS, BDD com Reqnroll;
2. Sonar, com Quality Gate;
3. trava de cobertura em 80%;
4. imagem no ECR, com a tag do commit;
5. deploy no EKS, com migrations e rollout;
6. smoke test pelo API Gateway.

Tudo com OIDC, sem chave da AWS guardada no GitHub. Com a infra destruída, o deploy é pulado com um aviso.

**Mostrar:** o resumo do run, com a tabela de cobertura, e o artifact `cobertura`.

**Mostrar:** a aba 6, o PR com teste quebrado e o merge **bloqueado**.

**Falar:** a regra da `main` exige o check da CI, então nem teste quebrado nem Quality Gate reprovado entram.

**Mostrar:** a aba 7, o SonarCloud dos três, com Quality Gate aprovado e a cobertura.

**Opcional, ao vivo** (o deploy leva uns 5 minutos, então abra o PR antes de gravar e só faça o merge no
vídeo): mude uma linha do README, faça o merge e mostre CI e CD rodando.

### Bloco 6 — New Relic (11:30 – 14:00)

**Mostrar:** **APM & Services**, com `garageos-os`, `garageos-billing` e `garageos-execucao`, cada um com
tempo de resposta, vazão e erros.

**Mostrar:** o dashboard **GarageOS - Saga da OS**:
- **Saga:** OS criadas, finalizadas e canceladas, o funil por etapa, as compensações por motivo e o tempo
  em cada status.
- **Servicos:** requisições, p95 e erros por serviço.
- **Rastrear uma OS:** a linha do tempo de uma OS pelo número, com o `correlationId` de cada passo.

**Falar:** o `X-Correlation-Id` da requisição é carimbado em cada mensagem e volta aos logs e aos eventos
no consumidor. Uma operação aparece ligada nos três serviços. Mostre o id que nasceu no webhook do
Mercado Pago, no Billing, aparecendo no evento do OS.

**Rodar** em **Query your data**, com o `osId` do bloco 3:

```sql
SELECT entity.name, context.CorrelationId, context.MessageType, message FROM Log
WHERE context.OrdemDeServicoId = '<osId>' AND message NOT LIKE 'Executed DbCommand%' SINCE 1 hour ago
```

**Opcional:** **Distributed tracing** de uma requisição do OS.

### Bloco 7 — Encerramento (14:00 – 15:00)

**Falar:** o resumo, com os pontos abaixo:
- três serviços com bancos próprios;
- saga orquestrada com compensação;
- Mercado Pago real no sandbox;
- CI/CD por serviço com Quality Gate e 80% de cobertura;
- observabilidade com rastreamento entre serviços.

Mostre os links dos repositórios.

---

## Depois de gravar

1. **Destruir a infra** na ordem inversa da subida: Lambda e Billing, depois OS e Execução, depois o
   cluster, por último o `infra-database`. Em cada um: **Run workflow** com `destroy`.
2. **Publicar no YouTube** como **não listado**.
3. **Testar o link numa janela anônima**, sem estar logado, antes de colocar no PDF de entrega.
