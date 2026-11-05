# language: pt
Funcionalidade: Ciclo de vida da ordem de serviço
  A saga orquestrada leva a OS do pedido de orçamento até a finalização.
  Quando um passo falha, ela desfaz só o que já tinha acontecido.

  Os outros serviços são simulados: cada passo publica o evento que o Billing
  ou o Execução publicariam, no transporte em memória do MassTransit.

  Contexto:
    Dada uma ordem de serviço com peças
    Quando a oficina pede o orçamento
    Então o Billing recebe o comando "GerarOrcamento"
    Quando o Billing envia o orçamento ao cliente
    Então a ordem de serviço fica "AguardandoAprovacao"

  Cenário: Caminho feliz, do orçamento à finalização
    Quando o cliente aprova o orçamento
    Então o Execução recebe o comando "ReservarPecas"
    Quando o Execução reserva as peças
    Então o Billing recebe o comando "SolicitarPagamento"
    E a ordem de serviço fica "AguardandoPagamento"
    Quando o Mercado Pago confirma o pagamento
    Então o Execução recebe o comando "IniciarExecucao"
    E a ordem de serviço fica "EmExecucao"
    Quando o Execução finaliza o serviço
    Então a ordem de serviço fica "Finalizada"
    E nenhuma compensação é enviada

  Cenário: Cliente recusa o orçamento, sem nada a desfazer
    Quando o cliente recusa o orçamento
    Então a ordem de serviço fica "Cancelada"
    E a saga registra a compensação "nenhuma"

  Cenário: Peça indisponível cancela o orçamento
    Quando o cliente aprova o orçamento
    E o Execução informa que falta peça
    Então a ordem de serviço fica "Cancelada"
    E a saga registra a compensação "CancelarOrcamento"

  Cenário: Pagamento recusado libera a reserva
    Quando o cliente aprova o orçamento
    E o Execução reserva as peças
    E o Mercado Pago recusa o pagamento
    Então a ordem de serviço fica "Cancelada"
    E a saga registra a compensação "LiberarReservaPecas, CancelarOrcamento"

  Cenário: Execução falhou estorna o pagamento
    Quando o cliente aprova o orçamento
    E o Execução reserva as peças
    E o Mercado Pago confirma o pagamento
    Então a ordem de serviço fica "EmExecucao"
    Quando o Execução informa que o serviço falhou
    Então a ordem de serviço fica "Cancelada"
    E a saga registra a compensação "EstornarPagamento, LiberarReservaPecas, CancelarOrcamento"
