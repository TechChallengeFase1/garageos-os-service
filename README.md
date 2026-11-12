# GarageOS — OS Service

[![CI](https://github.com/TechChallengeFase1/garageos-os-service/actions/workflows/ci.yml/badge.svg)](https://github.com/TechChallengeFase1/garageos-os-service/actions/workflows/ci.yml) [![Quality Gate](https://sonarcloud.io/api/project_badges/measure?project=TechChallengeFase1_garageos-os-service&metric=alert_status)](https://sonarcloud.io/summary/overall?id=TechChallengeFase1_garageos-os-service) [![Cobertura](https://sonarcloud.io/api/project_badges/measure?project=TechChallengeFase1_garageos-os-service&metric=coverage)](https://sonarcloud.io/component_measures?id=TechChallengeFase1_garageos-os-service&metric=coverage) [![Bugs](https://sonarcloud.io/api/project_badges/measure?project=TechChallengeFase1_garageos-os-service&metric=bugs)](https://sonarcloud.io/summary/overall?id=TechChallengeFase1_garageos-os-service) [![Vulnerabilidades](https://sonarcloud.io/api/project_badges/measure?project=TechChallengeFase1_garageos-os-service&metric=vulnerabilities)](https://sonarcloud.io/summary/overall?id=TechChallengeFase1_garageos-os-service)

Sistema de gestão para oficinas mecânicas — controle de clientes, veículos, serviços, estoque e ordens de serviço (OS), com fluxo completo de orçamento, execução e acompanhamento público pelo cliente.

Projeto desenvolvido como **Tech Challenge** da Pós-graduação em Arquitetura de Software.

> **Fase 4:** este repositório (antes `garageos-app`) virou o **OS Service**, um dos três microsserviços. Ele ficou com a ordem de serviço, clientes, veículos, catálogo de serviços, status e histórico. Orçamento e pagamento foram para o [`garageos-billing-service`](https://github.com/TechChallengeFase1/garageos-billing-service) (DynamoDB); estoque, reservas, fila de execução e aging para o [`garageos-execucao-service`](https://github.com/TechChallengeFase1/garageos-execucao-service). As decisões estão nos [ADRs 0004 a 0007](docs/adrs/). Seções abaixo que ainda falam do monólito descrevem a Fase 3.

---

## Índice

- [Objetivo](#objetivo)
- [Stack](#stack)
- [Arquitetura da solução](#arquitetura-da-solução)
- [Documentação da API (Swagger)](#documentação-da-api-swagger)
- [Como rodar localmente](#como-rodar-localmente)
- [Como fazer o deploy na AWS](#como-fazer-o-deploy-na-aws)
- [Health checks](#health-checks)
- [Logs estruturados e observabilidade](#logs-estruturados-e-observabilidade)
- [Escalabilidade (HPA)](#escalabilidade-hpa)
- [Segredos](#segredos)
- [Testes e qualidade](#testes-e-qualidade)
- [Estrutura do repositório](#estrutura-do-repositório)
- [Documentação de arquitetura](#documentação-de-arquitetura)

---

## Objetivo

Entregar uma API REST que cubra a operação ponta a ponta de uma oficina:

- Cadastro de **clientes**, **veículos**, **serviços** e **itens de estoque**.
- Criação e gestão de **ordens de serviço**, com status, serviços executados e peças consumidas.
- Fluxo de **orçamento**: geração, envio ao cliente, aprovação ou recusa (com baixa de estoque na aprovação).
- **Acompanhamento público** da OS pelo cliente, pelo número da OS, sem login.
- **Aging** das OS para análise gerencial do tempo médio de execução.
- Autenticação **JWT** — administrativa na própria API, e por **CPF** via Lambda no API Gateway.

## Stack

| Camada | Tecnologia |
|---|---|
| Runtime | .NET 10 |
| API | ASP.NET Core Web API |
| ORM | Entity Framework Core |
| Banco | PostgreSQL 16 (Amazon RDS) |
| Validação | FluentValidation |
| Auth | JWT Bearer (HS256) |
| Logs | Serilog — JSON compacto com correlation ID |
| Observabilidade | New Relic — agente .NET (profiler do CLR), `nri-bundle` no cluster e custom events de negócio |
| Testes | xUnit, Moq, FluentAssertions, Testcontainers |
| Qualidade | SonarCloud, com Quality Gate obrigatório no PR |
| Mensageria | Amazon SNS/SQS com MassTransit 8.5 (Apache-2.0) |
| Container | Docker + Docker Compose |
| Orquestração | Amazon EKS (Kubernetes) |
| CI/CD | GitHub Actions com OIDC |

Arquitetura de código: **Clean Architecture**, com separação em `Domain`, `Application`, `Infrastructure` e `Api`. As camadas externas dependem das internas, nunca o contrário.

---

## Arquitetura da solução

```mermaid
flowchart LR
    U(["Cliente e equipe"]) -->|HTTPS| GW["API Gateway"]
    MP["Mercado Pago"] -->|"webhook assinado"| GW
    GW -->|"POST /auth"| LA["Lambda auth<br/>CPF para JWT"]
    GW -.->|"valida o token"| AZ["Lambda authorizer"]
    LA -->|"auth_reader,<br/>só leitura"| DBOS
    GW --> NLB["NLB"] --> TR["Traefik<br/>/os /billing /execucao"]

    subgraph EKS["Amazon EKS"]
        TR --> OS["OS Service<br/>orquestrador da saga"]
        TR --> BI["Billing"]
        TR --> EX["Execução"]
    end

    OS --> DBOS[("RDS PostgreSQL<br/>OS + saga + outbox")]
    EX --> DBEX[("RDS PostgreSQL<br/>estoque e execução")]
    BI --> DY[("DynamoDB<br/>orçamentos e pagamentos")]
    BI -->|"Checkout Pro"| MP
    OS -->|"HTTP: confere peças"| EX

    OS <-->|"comandos e eventos"| BUS{{"SNS + SQS<br/>MassTransit"}}
    BI <--> BUS
    EX <--> BUS

    OS & BI & EX -.-> NR["New Relic<br/>APM, logs, eventos"]
```

| Repositório | Responsabilidade |
|---|---|
| **`garageos-os-service`** *(este)* | OS, clientes, veículos, catálogo de serviços e o orquestrador da saga |
| [`garageos-billing-service`](https://github.com/TechChallengeFase1/garageos-billing-service) | Orçamentos e pagamentos (Mercado Pago), em DynamoDB |
| [`garageos-execucao-service`](https://github.com/TechChallengeFase1/garageos-execucao-service) | Estoque, reserva de peças, execução e aging |
| [`garageos-infra-database`](https://github.com/TechChallengeFase1/garageos-infra-database) | VPC, OIDC, state remoto, segredos compartilhados |
| [`garageos-infra-k8s`](https://github.com/TechChallengeFase1/garageos-infra-k8s) | Cluster EKS, Traefik, New Relic |
| [`garageos-lambda-auth`](https://github.com/TechChallengeFase1/garageos-lambda-auth) | API Gateway, login por CPF e authorizer |

Rotas do gateway, sequência da saga e entrega contínua: [docs/arquitetura-fase4.md](docs/arquitetura-fase4.md).

---

## Documentação da API (Swagger)

| Ambiente | URL |
|---|---|
| Local (Docker Compose) | <http://localhost:8080/swagger> |
| Cluster (via Load Balancer) | `http://<hostname-do-nlb>/swagger` |

> O Swagger **não** abre através do API Gateway. Lá todas as rotas ficam sob
> `ANY /{proxy+}`, que exige token, e o navegador não envia `Authorization` ao
> carregar uma página — o gateway responde `401`. Use o Load Balancer.
>
> E use `http://` explícito: o Load Balancer só tem listener na porta 80. Sem o
> esquema na barra de endereço, o navegador tenta `https://`, bate numa porta
> que não escuta e o erro que aparece é um timeout, não um aviso de certificado.

Para descobrir as URLs do ambiente provisionado:

```bash
kubectl get svc garageos-api -n garageos -o jsonpath='{.status.loadBalancer.ingress[0].hostname}'
```

```bash
terraform -chdir=../garageos-lambda-auth output -raw api_gateway_url
```

Também há uma coleção Postman pronta para importar: [Code/Postman/GarageOS.postman_collection.json](Code/Postman/GarageOS.postman_collection.json).

### Principais rotas

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `POST` | `/auth` *(no API Gateway)* | pública | Autenticação do cliente por CPF, devolve JWT |
| `POST` | `/api/Auth/login` | pública | Login administrativo (usuário e senha) |
| `POST` | `/api/OrdensDeServico/abertura-completa` | Admin | Abre a OS em `Recebida`, com nome e preço de serviços e peças copiados |
| `POST` | `/api/OrdensDeServico/{id}/servicos` | Admin | Inclui um serviço (até o diagnóstico) |
| `POST` | `/api/OrdensDeServico/{id}/pecas` | Admin | Inclui uma peça, conferida no Execução Service |
| `PATCH` | `/api/OrdensDeServico/{id}/status` | Admin | Altera o status, só por transições permitidas |
| `GET` | `/api/OrdensDeServico/{id}/historico` | Admin | Histórico de status com origem e correlation id |
| `GET` | `/api/OrdensDeServico/acompanhar/{numeroOS}` | pública | Acompanhamento pelo cliente, com as etapas |
| `GET` | `/health/live` · `/health/ready` | pública | Health checks |

Máquina de estados da OS: `Recebida → EmDiagnostico → AguardandoAprovacao → AguardandoPagamento → EmExecucao → Finalizada → Entregue`, e `Cancelada` a partir de qualquer status não final. Do pedido de orçamento até a finalização quem move a OS é a saga; manualmente, a equipe registra só a entrega.

| Método | Rota | Auth | Descrição |
|---|---|---|---|
| `POST` | `/api/OrdensDeServico/{id}/orcamento/solicitar` | Admin | Fecha o diagnóstico e inicia a saga (202) |
| `GET` | `/api/OrdensDeServico/{id}/saga` | Admin | Etapa atual da saga, IDs do orçamento e do pagamento, motivo do cancelamento |

---

## Saga Pattern: por que orquestração

A transação que no monólito era um único `SaveChanges` (aprovar o orçamento, mudar o status da OS e dar baixa no estoque) hoje atravessa três bancos (dois PostgreSQL e um DynamoDB) e o Mercado Pago. Não existe transação distribuída entre eles, então ela virou uma **saga orquestrada**, com o orquestrador aqui no OS Service ([ADR 0005](docs/adrs/0005-saga-orquestrada.md)).

**Por que orquestrada, e não coreografada:**

- **O fluxo é linear e já existia no domínio.** O enum de status da OS era uma máquina de estados escrita à mão; a orquestração deixa essa ordem explícita num arquivo só ([`OrdemDeServicoStateMachine`](Code/GarageOS.Infrastructure/Saga/OrdemDeServicoStateMachine.cs)), em vez de espalhá-la por handlers em três repositórios.
- **A compensação depende de onde a falha aconteceu.** Antes do pagamento basta liberar as peças e cancelar o orçamento; depois dele, também é preciso estornar. Quem sabe a etapa é o coordenador.
- **Há prazos com dono.** Aprovação e pagamento expiram; alguém precisa agendar e reagir. Na coreografia esse dono não existe.
- **O estado é consultável.** `GET /saga` mostra em que passo a OS está; na coreografia ele só existiria como inferência sobre eventos espalhados, difícil de mostrar e de depurar.
- **O OS já é o dono do ciclo de vida.** Pôr o orquestrador nele não cria acoplamento novo.

O custo aceito: o OS conhece os contratos dos outros dois serviços.

```mermaid
sequenceDiagram
    autonumber
    participant OS as OS Service (saga)
    participant B as Billing
    participant E as Execução
    OS->>B: GerarOrcamento
    B-->>OS: OrcamentoEnviado (OS: AguardandoAprovacao)
    Note over B: cliente aprova (PATCH /resposta)
    B-->>OS: OrcamentoAprovado
    OS->>E: ReservarPecas
    E-->>OS: PecasReservadas
    OS->>B: SolicitarPagamento (OS: AguardandoPagamento)
    B-->>OS: PagamentoConfirmado
    OS->>E: IniciarExecucao (OS: EmExecucao)
    E-->>OS: ExecucaoFinalizada (OS: Finalizada)
```

**Compensações** (cada passo desfaz só o que já tinha acontecido; a OS termina em `Cancelada` com o motivo na saga):

| Falha | O que a saga faz |
|---|---|
| Cliente recusa o orçamento | nada a desfazer |
| Prazo de aprovação expira | `CancelarOrcamento` |
| Peças indisponíveis | `CancelarOrcamento` |
| Pagamento recusado ou prazo expirado | `LiberarReservaPecas` → `CancelarOrcamento` |
| Execução falhou | `EstornarPagamento` → `LiberarReservaPecas` → `CancelarOrcamento` |
| Pagamento confirmado depois do cancelamento | `EstornarPagamento` |

**Mensagem repetida não causa efeito duplicado:**

- *OS e Execução* usam o **outbox transacional** do MassTransit no PostgreSQL: a mudança e a mensagem a publicar vão no mesmo commit, e o **inbox** descarta a mesma mensagem entregue duas vezes.
- *Todo consumidor é idempotente por chave de negócio*: um orçamento por OS (marcador gravado na mesma transação no DynamoDB), uma reserva por OS (índice único), uma execução por OS. Repetido, o pedido devolve o que já existe e confirma de novo.
- *A saga ignora evento fora de hora* (repetido com outro id, fora de ordem) em vez de mandá-lo para a fila de erro, e pula status que a OS já alcançou.
- *Erro técnico* tem retry com intervalo crescente; o que sobra vai para a fila `_error` (a DLQ), visível no console do SQS.

Mensageria: Amazon SQS/SNS com MassTransit 8.5.11 (Apache-2.0), contratos em [`Contracts/V1/Mensagens.cs`](Code/GarageOS.Application/Contracts/V1/Mensagens.cs), idênticos nos três serviços.

Há ainda os CRUDs de `Clientes`, `Veiculos` e `Servicos`, todos protegidos por JWT. O estoque passou ao Execução Service.

---

## Como rodar localmente

Pré-requisitos: Docker e Docker Compose. SDK .NET 10 e `dotnet-ef` apenas se quiser rodar comandos `dotnet` fora do container.

**1. Configurar as variáveis de ambiente**

```bash
cp .env.example .env
```

Preencha ao menos `POSTGRES_*`, `JWT_*` e `ADMIN_*`. As variáveis `NEW_RELIC_*` são opcionais no local — sem elas o agente simplesmente não envia telemetria.

**2. Subir os containers**

```bash
docker compose up -d --build
```

| Serviço | Porta | Descrição |
|---|---|---|
| `garageos-api` | **8080** | API GarageOS |
| `garageos-postgres` | 5432 | Banco principal |
| `garageos-pgadmin` | 5050 | Interface web do Postgres |
| `garageos-sonarqube` | 9000 | Análise de qualidade (opcional) |
| `garageos-sonar-db` | — | Banco do SonarQube |

No Compose, `Database__MigrateOnStartup=true` faz a API aplicar as migrations ao subir — há uma instância só, então é seguro e conveniente. **No cluster isso fica desligado**: quem migra é um Job (ver [Migrations](#migrations)).

**3. Acessar**

- API: <http://localhost:8080>
- **Swagger**: <http://localhost:8080/swagger>
- PgAdmin: <http://localhost:5050>
- SonarQube: <http://localhost:9000>

**4. Autenticar**

```bash
curl -X POST http://localhost:8080/api/Auth/login \
  -H "content-type: application/json" \
  -d '{"username":"admin","password":"<ADMIN_PASSWORD do .env>"}'
```

Use o token nas rotas protegidas com `Authorization: Bearer <token>`.

**5. Encerrar**

```bash
docker compose down
```

Acrescente `-v` para remover também os volumes (zera o banco).

### Acessar o banco da nuvem a partir da máquina

O RDS está em subnet privada com `publicly_accessible = false` — não existe rota da internet até ele, e isso é proposital. Para usar DBeaver, pgAdmin ou `psql`, há um túnel pronto:

```bash
./scripts/tunel-banco.sh
```

Ele sobe um pod `socat` no cluster, que herda o Security Group "crachá" dos nós, e encaminha `localhost:5432` até o RDS.

---

## Como fazer o deploy na AWS

### Pré-requisitos de infraestrutura

O deploy depende da infraestrutura já provisionada, **nesta ordem**:

```text
bootstrap/  →  garageos-infra-database  →  garageos-infra-k8s  →  garageos-app  →  garageos-lambda-auth
```

Se algum passo anterior faltar, a pipeline falha ao procurar os parâmetros no SSM — antes de tocar em qualquer coisa.

### Deploy automático (caminho normal)

Depois que build, testes e Sonar passam na `main`, a CI chama `.github/workflows/cd.yml`. Com a infra destruída, ele avisa e pula o deploy, em vez de falhar.

| Etapa | O que faz |
|---|---|
| Verificar | Confere no SSM se o cluster e o ECR existem |
| Imagem Docker | Build e push para o ECR, com a tag do commit. Rodar de novo o mesmo commit reaproveita a imagem |
| Autenticação na AWS | **OIDC** — nenhuma credencial estática existe neste repositório |
| Descoberta | Lê cluster, namespace e ARNs dos segredos no SSM Parameter Store |
| Secret | Lê o Secrets Manager e cria/atualiza o Secret do Kubernetes |
| Migrations | Aplica o Job e **aguarda concluir** antes do rollout |
| Usuário da Lambda | Cria o `auth_reader`, usado no login por CPF |
| Deploy | `kubectl apply -f k8s/` + `rollout restart` + `rollout status` |
| Smoke test | `curl` em `/os/health/ready`, pelo API Gateway |

O deploy usa o environment `producao` do GitHub — configurar *required reviewers* ali faz o deploy aguardar aprovação em vez de disparar sozinho.

Nada do que o workflow precisa está escrito à mão nele: recriar a infraestrutura do zero **não exige editar este arquivo**. Ver [ADR 0001](docs/adrs/0001-comunicacao-entre-repositorios.md).

### Deploy manual (para diagnóstico)

```bash
aws eks update-kubeconfig --name "$(aws ssm get-parameter --name /garageos/producao/eks/cluster-name --query Parameter.Value --output text)" --region us-east-1
```

```bash
kubectl apply -f k8s/ -n garageos-os
```

```bash
kubectl rollout status deployment/garageos-api -n garageos --timeout=300s
```

### Migrations

As migrations rodam em um **Kubernetes Job** (`k8s/jobs/migrations-job.yaml`), com a mesma imagem da API e `args: ["--migrate"]`.

Não é preferência de estilo: com o HPA ativo, várias réplicas sobem ao mesmo tempo e, se as migrations estivessem no startup, todas tentariam migrar em paralelo, disputando a mesma tabela de histórico do EF Core. O Job roda **uma vez**, antes do rollout.

```bash
kubectl delete job garageos-migrations -n garageos --ignore-not-found
kubectl apply -f k8s/jobs/migrations-job.yaml
kubectl wait --for=condition=complete job/garageos-migrations -n garageos --timeout=300s
```

> `kubectl apply -f k8s/` **não é recursivo** — por isso o Job fica em `k8s/jobs/` e não é reaplicado junto com os manifestos da API.

---

## Health checks

| Endpoint | Consulta o banco? | Usado por |
|---|---|---|
| `/health/live` | **Não** | `livenessProbe` — reinicia o container se travar |
| `/health/ready` | **Sim** | `readinessProbe` — tira o pod do balanceamento |

A separação é deliberada. Se o liveness consultasse o banco, uma oscilação do RDS reprovaria a probe em **todos** os pods ao mesmo tempo e o Kubernetes mataria a aplicação inteira — transformando instabilidade de banco em queda total. Com a divisão atual, o pod apenas sai do balanceamento e volta sozinho quando o banco voltar.

---

## Logs estruturados e observabilidade

Os logs saem em **JSON compacto** (Serilog + `CompactJsonFormatter`) no stdout, que é de onde o Kubernetes e o agente do New Relic coletam.

O `CorrelationIdMiddleware`:

- aceita o header `X-Correlation-Id` do chamador, ou gera um novo;
- publica o valor no `LogContext` do Serilog, então **toda** linha de log da requisição o carrega;
- devolve o header na resposta, para o chamador correlacionar do lado dele.

O agente do New Relic injeta `trace.id` e `span.id` em cada linha, ligando log a trace na interface. Dado um `X-Correlation-Id`, é possível recuperar todas as linhas da requisição e o trace com as consultas SQL que ela executou.

### Métricas de negócio

Latência e taxa de erro o agente coleta sozinho. O que ele **não** tem como saber é quantas OS foram abertas hoje ou quanto tempo uma OS ficou em diagnóstico — isso é dado do domínio, e só a aplicação pode emitir.

A publicação passa pela interface `IMetricasDeNegocio` (`GarageOS.Application/Abstractions`), implementada por `MetricasNewRelic` na Infrastructure. Os use cases não conhecem o fornecedor.

| Evento | Atributos | Emitido em |
|---|---|---|
| `OrdemDeServicoCriada` | `numeroOS`, `clienteId` | `AbrirOrdemDeServicoCompletaUseCase`, após persistir |
| `OrdemDeServicoStatus` | `numeroOS`, `statusAnterior`, `statusNovo`, `minutosNoStatus` | `AlterarStatusOrdemDeServicoUseCase` |
| `OrdemDeServicoFalha` | `numeroOS`, `motivo` | Falhas no processamento de uma ordem de serviço |
| `SagaEtapa` | `numeroOS`, `etapa` | A cada passo da saga |
| `SagaCompensacao` | `numeroOS`, `falha`, `compensacoes` | Quando a saga desfaz passos |

Todo evento leva também o `correlationId`, que atravessa as mensagens entre os três serviços. Como isso
funciona, as consultas e o dashboard da saga: [docs/observabilidade](docs/observabilidade/README.md).

Duas garantias que valem conhecer antes de mexer:

- **`minutosNoStatus` é calculado antes da transição.** Depois de `AlterarStatus()`, o estado anterior não existe mais em lugar nenhum.
- **Telemetria nunca derruba operação.** Nenhuma chamada de métrica propaga exceção: falha vira `LogDebug` e a OS segue. É o que permite rodar localmente, sem agente, sem tratamento especial.

As NRQL dos dashboards e a condição do alerta estão registradas na [RFC 0004](docs/rfcs/0004-ferramenta-de-observabilidade.md) — eles vivem na interface do New Relic, não em código, então essa RFC é a referência para reconstruí-los.

Detalhes e critérios de escolha da ferramenta: [RFC 0004](docs/rfcs/0004-ferramenta-de-observabilidade.md).

---

## Escalabilidade (HPA)

`k8s/hpa.yaml` mantém entre **2 e 10 réplicas**, com alvo de **70% de CPU**.

Para o HPA funcionar de fato, quatro condições precisam estar satisfeitas:

1. `metrics-server` instalado no cluster (feito pelo `garageos-infra-k8s`);
2. `resources.requests.cpu` declarado no Deployment — sem denominador não há percentual;
3. migrations fora do startup;
4. health checks HTTP corretos, para o Service só mandar tráfego a pods prontos.

```bash
kubectl get hpa -n garageos
kubectl top pods -n garageos
```

Racional completo e limites conhecidos: [ADR 0002](docs/adrs/0002-uso-do-hpa.md).

---

## Segredos

**Nenhum segredo está versionado neste repositório.** O antigo `k8s/secret.yaml`, que trazia a `JWT_SECRET_KEY` em Base64, foi removido.

```mermaid
flowchart LR
    SM["AWS Secrets Manager<br/>garageos/app/secrets<br/>garageos/rds/master"] -->|"pipeline de CD le"| K8S["Secret do Kubernetes<br/>garageos-secret"]
    K8S -->|"envFrom"| POD["Pods da API"]
    SM -->|"terraform apply"| LMB["Lambda de autenticacao"]
```

A pipeline lê o Secrets Manager em tempo de deploy e cria o Secret do Kubernetes de forma idempotente. A `JWT_SECRET_KEY` é a **mesma** usada pela Lambda de autenticação — as duas leem do mesmo segredo, e é isso que impede o modo de falha em que a API rejeita silenciosamente todo token emitido pela Lambda.

| Chave no Secret do Kubernetes | Origem |
|---|---|
| `ConnectionStrings__DefaultConnection` | `garageos/rds/master` → `connectionString` |
| `Jwt__SecretKey` | `garageos/app/secrets` → `jwtSecretKey` |
| `Admin__Password` | `garageos/app/secrets` → `adminPassword` |
| `NEW_RELIC_LICENSE_KEY` | `garageos/app/secrets` → `newRelicLicenseKey` |

Todos os valores passam por `::add-mask::` antes de qualquer uso, então um comando que os ecoe por engano sai mascarado no log.

O `ConfigMap` (`k8s/configmap.yaml`) guarda apenas configuração **não sensível**: ambiente, issuer, audience, usuário admin e `NEW_RELIC_APP_NAME` (`garageOS`) — nome distinto do usado no `docker compose` local, para que os dados de desenvolvimento não se misturem aos do cluster.

> A license key é o único segredo que **não é sorteado** pelo Terraform: é emitida pela New Relic e entra uma vez, como variável do bootstrap. Se ficar vazia, o agente sobe, se desliga e a aplicação funciona normalmente — só não reporta nada.

---

## Testes e qualidade

```bash
dotnet test Code/GarageOS.UnitTests/GarageOS.UnitTests.csproj
```

```bash
dotnet test Code/GarageOS.IntegrationTests/GarageOS.IntegrationTests.csproj
```

Os testes de integração sobem um PostgreSQL real via **Testcontainers**, então o Docker precisa estar em execução.

Análise SonarQube (com o Compose no ar):

```bash
cd Code && ./sonar-scan.sh
```

No Windows, use `./sonar-scan.ps1`. Requer `dotnet-sonarscanner` como ferramenta global e `SONAR_TOKEN` preenchido no `.env`.

### Cobertura

**92,1% de linhas**, somando os testes unitários, de integração e BDD. A CI publica o relatório HTML no artifact `cobertura`, mostra o resumo na execução e falha abaixo de 80%.

Ficam de fora só as migrations, geradas pelo EF, e o `Program.cs`, que só liga as peças e é exercitado pelos testes de integração. As regras estão em [`coverlet.runsettings`](Code/coverlet.runsettings). No Sonar saem também `scripts/` e `Code/sonar-scan.sh`, ferramentas auxiliares que não vão para produção.

### Ponta a ponta no cluster

Com os três serviços no ar, pelo API Gateway:

- **Postman:** [`Code/Postman/GarageOS.Fase4.postman_collection.json`](Code/Postman/GarageOS.Fase4.postman_collection.json), com pastas para o caminho feliz, o cliente e os dois cenários de compensação. Validada com o Newman:
  ```bash
  npx newman run Code/Postman/GarageOS.Fase4.postman_collection.json --env-var baseUrl=<url do gateway> --env-var adminPassword=<senha> --folder "01 - Autenticação" --folder "02 - Cadastros" --folder "03 - Ordem de serviço e saga" --folder "Cenário - cliente recusa"
  ```
- **Script:** [`scripts/e2e/saga_e2e.py`](scripts/e2e/saga_e2e.py) faz o mesmo caminho e lê a URL e a senha de Admin da AWS (`python scripts/e2e/saga_e2e.py iniciar`, `continuar`, `falhar`, `recusar`).

### BDD

[`CicloDeVidaDaOS.feature`](Code/GarageOS.OsService.Bdd/Features/CicloDeVidaDaOS.feature) descreve em português o ciclo da OS, com Reqnroll: caminho feliz, orçamento recusado, peça indisponível, pagamento recusado e execução com falha. Roda a saga e a OS reais, com o Billing e o Execução simulados em memória.

```bash
dotnet test Code/GarageOS.OsService.Bdd
```

---

## Estrutura do repositório

```text
garageos-app/
├── .github/workflows/       # ci.yml (build + testes) e cd.yml (imagem + deploy no EKS)
├── Code/
│   ├── GarageOS.Api/            # Controllers, middlewares, health checks, Program.cs
│   ├── GarageOS.Application/    # Use cases, DTOs, validators
│   ├── GarageOS.Domain/         # Entidades, value objects, regras de domínio
│   ├── GarageOS.Infrastructure/ # EF Core, repositórios, migrations
│   ├── GarageOS.UnitTests/
│   ├── GarageOS.IntegrationTests/
│   └── Postman/                 # Coleção pronta para importar
├── docs/
│   ├── rfcs/                # Decisões de tecnologia
│   ├── adrs/                # Decisões de arquitetura
│   └── diagramas/           # Componentes, sequência e ER
├── k8s/
│   ├── api-deployment.yaml  # Deployment com probes HTTP
│   ├── api-service.yaml     # Service type LoadBalancer (NLB)
│   ├── configmap.yaml       # Configuração não sensível
│   ├── hpa.yaml             # 2 a 10 réplicas, CPU 70%
│   └── jobs/                # Job de migrations
├── scripts/                 # Túnel para o RDS, validação da solução
├── Dockerfile               # Multi-stage + agente do New Relic
└── docker-compose.yml
```

---

## Documentação de arquitetura

Índice completo em **[docs/README.md](docs/README.md)**.

| Documento | Assunto |
|---|---|
| [RFC 0001](docs/rfcs/0001-escolha-provedor-cloud.md) | Escolha do provedor de nuvem (AWS) |
| [RFC 0002](docs/rfcs/0002-escolha-banco-de-dados.md) | Escolha do banco de dados (RDS PostgreSQL) |
| [RFC 0003](docs/rfcs/0003-estrategia-de-autenticacao.md) | Estratégia de autenticação (CPF, Lambda, JWT) |
| [RFC 0004](docs/rfcs/0004-ferramenta-de-observabilidade.md) | Ferramenta de observabilidade (New Relic) |
| [ADR 0001](docs/adrs/0001-comunicacao-entre-repositorios.md) | Comunicação entre os repositórios |
| [ADR 0002](docs/adrs/0002-uso-do-hpa.md) | Uso do HPA |
| [Componentes](docs/diagramas/componentes.md) · [Autenticação](docs/diagramas/sequencia-autenticacao.md) · [Abertura de OS](docs/diagramas/sequencia-abertura-os.md) · [ER](docs/diagramas/modelo-er.md) | Diagramas |

Imagem pública no Docker Hub: [`garageosfiap/garageos-api`](https://hub.docker.com/r/garageosfiap/garageos-api).

---

## Autores

Trabalho desenvolvido pelo grupo da Pós-graduação em Arquitetura de Software.
