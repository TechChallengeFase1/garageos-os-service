using GarageOS.Application.Abstractions;
using Microsoft.Extensions.Logging;
using NewRelic.Api.Agent;

namespace GarageOS.Infrastructure.Observabilidade;

/// <summary>Publica os eventos de negócio como custom events do New Relic</summary>
/// <remarks>
/// Custom events, e não métricas, de propósito: eventos permitem consulta por
/// NRQL com FACET e agregação arbitrária depois do fato. Uma métrica exigiria
/// decidir a agregação na emissão.
///
/// Nenhuma chamada aqui lança: observabilidade quebrada não pode derrubar uma
/// ordem de serviço. Falha vira log e a operação segue.
/// </remarks>
public class MetricasNewRelic : IMetricasDeNegocio
{
    private readonly ILogger<MetricasNewRelic> _logger;

    /// <summary>Inicializa a publicação de métricas com o logger de diagnóstico</summary>
    public MetricasNewRelic(ILogger<MetricasNewRelic> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void OrdemDeServicoCriada(string numeroOS, Guid clienteId) =>
        Registrar("OrdemDeServicoCriada", new Dictionary<string, object>
        {
            ["numeroOS"] = numeroOS,
            ["clienteId"] = clienteId.ToString()
        });

    /// <inheritdoc />
    public void StatusAlterado(string numeroOS, string statusAnterior, string statusNovo, double minutosNoStatusAnterior) =>
        Registrar("OrdemDeServicoStatus", new Dictionary<string, object>
        {
            ["numeroOS"] = numeroOS,
            ["statusAnterior"] = statusAnterior,
            ["statusNovo"] = statusNovo,
            ["minutosNoStatus"] = Math.Round(minutosNoStatusAnterior, 2)
        });

    /// <inheritdoc />
    public void FalhaNoProcessamento(string numeroOS, string motivo) =>
        Registrar("OrdemDeServicoFalha", new Dictionary<string, object>
        {
            ["numeroOS"] = numeroOS,
            ["motivo"] = motivo
        });

    /// <inheritdoc />
    public void SagaAvancou(string numeroOS, string etapa) =>
        Registrar("SagaEtapa", new Dictionary<string, object>
        {
            ["numeroOS"] = numeroOS,
            ["etapa"] = etapa
        });

    /// <inheritdoc />
    public void SagaCompensou(string numeroOS, string falha, string compensacoes) =>
        Registrar("SagaCompensacao", new Dictionary<string, object>
        {
            ["numeroOS"] = numeroOS,
            ["falha"] = falha,
            ["compensacoes"] = compensacoes
        });

    private void Registrar(string evento, Dictionary<string, object> atributos)
    {
        if (Mensageria.Correlacao.Atual is { } correlationId)
            atributos["correlationId"] = correlationId;

        try
        {
            NewRelic.Api.Agent.NewRelic.RecordCustomEvent(evento, atributos);
        }
        catch (Exception ex)
        {
            // Sem agente configurado (ambiente local, por exemplo) isto apenas
            // não faz nada. Nunca propaga.
            _logger.LogDebug(ex, "Nao foi possivel registrar o evento {Evento}", evento);
        }
    }
}
