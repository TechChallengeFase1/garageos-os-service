using System.Collections.Concurrent;
using System.Reflection;
using MassTransit;
using LogContext = Serilog.Context.LogContext;

namespace GarageOS.Infrastructure.Mensageria;

/// <summary>O correlationId da operação em andamento, seja requisição HTTP ou mensagem</summary>
/// <remarks>
/// AsyncLocal: vale para o fluxo assíncrono atual e some quando ele termina. O
/// middleware HTTP preenche ao receber a requisição; o filtro de consumo, ao
/// receber uma mensagem. Os filtros de envio levam o valor adiante.
/// </remarks>
public static class Correlacao
{
    /// <summary>Cabeçalho da requisição HTTP e da mensagem, com o mesmo nome</summary>
    public const string Cabecalho = "X-Correlation-Id";

    private static readonly AsyncLocal<string?> Valor = new();

    public static string? Atual
    {
        get => Valor.Value;
        set => Valor.Value = value;
    }
}

/// <summary>Carimba o correlationId atual em toda mensagem publicada</summary>
public class CorrelacaoNaPublicacao<T> : IFilter<PublishContext<T>> where T : class
{
    public Task Send(PublishContext<T> context, IPipe<PublishContext<T>> next)
    {
        Carimbar.No(context);
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlacao");
}

/// <summary>Carimba o correlationId atual em toda mensagem enviada direto a uma fila</summary>
public class CorrelacaoNoEnvio<T> : IFilter<SendContext<T>> where T : class
{
    public Task Send(SendContext<T> context, IPipe<SendContext<T>> next)
    {
        Carimbar.No(context);
        return next.Send(context);
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlacao");
}

internal static class Carimbar
{
    // Quem já definiu o cabeçalho (o chamador, por exemplo) é respeitado.
    public static void No(SendContext context)
    {
        if (Correlacao.Atual is { } id && !context.Headers.TryGetHeader(Correlacao.Cabecalho, out _))
            context.Headers.Set(Correlacao.Cabecalho, id);
    }
}

/// <summary>Ao receber uma mensagem, devolve o correlationId aos logs e às mensagens seguintes</summary>
/// <remarks>
/// Os logs do consumidor ganham CorrelationId, OrdemDeServicoId e MessageType,
/// e o New Relic recebe os mesmos atributos na transação. Mensagem sem o
/// cabeçalho (publicada por versão antiga, por exemplo) ganha um id novo.
/// </remarks>
public class CorrelacaoNoConsumo<T> : IFilter<ConsumeContext<T>> where T : class
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo?> PropriedadeDaOS = new();

    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        var id = context.Headers.Get<string>(Correlacao.Cabecalho);
        if (string.IsNullOrWhiteSpace(id))
            id = Guid.NewGuid().ToString("N");

        var osId = PropriedadeDaOS
            .GetOrAdd(typeof(T), t => t.GetProperty("OrdemDeServicoId"))
            ?.GetValue(context.Message)?.ToString();

        var anterior = Correlacao.Atual;
        Correlacao.Atual = id;
        try
        {
            using (LogContext.PushProperty("CorrelationId", id))
            using (LogContext.PushProperty("OrdemDeServicoId", osId))
            using (LogContext.PushProperty("MessageType", typeof(T).Name))
            {
                AtributosNoNewRelic(id, osId);
                await next.Send(context);
            }
        }
        finally
        {
            Correlacao.Atual = anterior;
        }
    }

    public void Probe(ProbeContext context) => context.CreateFilterScope("correlacao");

    private static void AtributosNoNewRelic(string id, string? osId)
    {
        try
        {
            var transacao = NewRelic.Api.Agent.NewRelic.GetAgent().CurrentTransaction;
            transacao.AddCustomAttribute("correlationId", id);
            transacao.AddCustomAttribute("messageType", typeof(T).Name);
            if (osId is not null)
                transacao.AddCustomAttribute("ordemDeServicoId", osId);
        }
        catch (Exception)
        {
            // Sem agente (ambiente local, testes) isto não faz nada.
        }
    }
}

/// <summary>Liga os filtros de correlação no barramento</summary>
public static class CorrelacaoExtensions
{
    public static void UsarCorrelacao(this IBusFactoryConfigurator cfg, IRegistrationContext context)
    {
        cfg.UsePublishFilter(typeof(CorrelacaoNaPublicacao<>), context);
        cfg.UseSendFilter(typeof(CorrelacaoNoEnvio<>), context);
        cfg.UseConsumeFilter(typeof(CorrelacaoNoConsumo<>), context);
    }
}
