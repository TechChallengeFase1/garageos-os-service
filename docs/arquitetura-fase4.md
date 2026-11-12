# Arquitetura da Fase 4

O monólito da Fase 3 virou três microsserviços, cada um com seu banco, que se falam por
mensagens. A ordem de serviço atravessa os três numa **saga orquestrada** pelo OS Service.

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

| Serviço | Dono de | Banco | Repositório |
|---|---|---|---|
| **OS** | Ordem de serviço, clientes, veículos, catálogo de serviços, saga | RDS PostgreSQL | [`garageos-os-service`](https://github.com/TechChallengeFase1/garageos-os-service) |
| **Billing** | Orçamentos e pagamentos (Mercado Pago) | DynamoDB | [`garageos-billing-service`](https://github.com/TechChallengeFase1/garageos-billing-service) |
| **Execução** | Estoque, reserva de peças, execução e aging | RDS PostgreSQL | [`garageos-execucao-service`](https://github.com/TechChallengeFase1/garageos-execucao-service) |

| Infraestrutura | Repositório |
|---|---|
| VPC, OIDC, state do Terraform, segredos compartilhados e o security group dos bancos | [`garageos-infra-database`](https://github.com/TechChallengeFase1/garageos-infra-database) |
| Cluster EKS, nós, Traefik (um NLB para os três), New Relic | [`garageos-infra-k8s`](https://github.com/TechChallengeFase1/garageos-infra-k8s) |
| API Gateway, Lambda de login por CPF e authorizer | [`garageos-lambda-auth`](https://github.com/TechChallengeFase1/garageos-lambda-auth) |

Cada serviço tem o próprio `infra/` (banco, ECR, role IRSA, parâmetros SSM) e o próprio `k8s/`.

## Rotas pelo API Gateway

| Rota | Autenticação | Destino |
|---|---|---|
| `POST /auth` | pública | Lambda: CPF para JWT do cliente |
| `POST /os/api/auth/login` | pública | OS: login de Admin |
| `GET /os/api/ordensdeservico/acompanhar/{numeroOS}` | pública | OS: acompanhamento pelo cliente |
| `POST /billing/api/webhooks/mercadopago` | pública, assinatura `x-signature` | Billing |
| `GET /{servico}/health/ready` | pública | Saúde de cada serviço |
| `ANY /{proxy+}` | JWT, validado pelo authorizer | Traefik, que escolhe o serviço pelo prefixo |

## A saga

Por que orquestrada, os passos e as compensações: [README do OS Service](../README.md#saga-pattern-por-que-orquestração)
e [ADR 0005](adrs/0005-saga-orquestrada.md). Mensageria: [ADR 0006](adrs/0006-mensageria-sqs-masstransit.md).

```mermaid
sequenceDiagram
    autonumber
    participant OS as OS (saga)
    participant B as Billing
    participant MP as Mercado Pago
    participant E as Execução
    OS->>B: GerarOrcamento
    B-->>OS: OrcamentoEnviado
    Note over B: cliente aprova
    B-->>OS: OrcamentoAprovado
    OS->>E: ReservarPecas
    E-->>OS: PecasReservadas
    OS->>B: SolicitarPagamento
    B->>MP: cobrança (Checkout Pro)
    MP-->>B: webhook: pago
    B-->>OS: PagamentoConfirmado
    OS->>E: IniciarExecucao
    E-->>OS: ExecucaoFinalizada
```

## Entrega contínua

```mermaid
flowchart LR
    PR["Pull request"] --> CI["CI: build, testes,<br/>Sonar e cobertura ≥ 80%"]
    CI -->|"Quality Gate e testes<br/>obrigatórios para o merge"| M["merge na main"]
    M --> CI2["CI na main"] --> IMG["imagem no ECR<br/>tag do commit"] --> DEP["deploy no EKS<br/>migrations, rollout"] --> SM["smoke test<br/>pelo API Gateway"]
```

Com a infra destruída, o deploy é pulado com um aviso, em vez de falhar.

## Observabilidade

Nomes por serviço no New Relic, `correlationId` atravessando as mensagens e o dashboard da saga:
[docs/observabilidade](observabilidade/README.md).
