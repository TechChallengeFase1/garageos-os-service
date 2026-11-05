using System.Reflection;
using Amazon.SimpleNotificationService;
using Amazon.SQS;
using GarageOS.Infrastructure.Data;
using GarageOS.Infrastructure.Saga;
using GarageOS.Contracts.V1;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace GarageOS.Infrastructure.Mensageria;

/// <summary>Registro do MassTransit deste serviço (ADR 0006)</summary>
public static class MensageriaExtensions
{
    /// <summary>Serviço dono das filas: compõe o nome garageos-{ambiente}-os-*</summary>
    public const string Servico = "os";

    /// <remarks>
    /// Nomes por convenção, que é o que a policy IAM do pod libera:
    ///   filas   garageos-{ambiente}-os-{consumidor}  (e as _error, a DLQ do MassTransit)
    ///   tópicos garageos-{ambiente}-{EntityName da mensagem}
    ///
    /// Retry com intervalo crescente antes de mandar para a _error: falha
    /// transitória (banco piscando, outro serviço reiniciando) se resolve
    /// sozinha; o que sobra fica parado na fila de erro, visível no console.
    /// </remarks>
    public static IServiceCollection AddMensageria(this IServiceCollection services, IConfiguration configuration)
    {
        var opcoes = configuration.GetSection(MensageriaOptions.Secao).Get<MensageriaOptions>() ?? new MensageriaOptions();
        services.Configure<MensageriaOptions>(configuration.GetSection(MensageriaOptions.Secao));

        services.AddMassTransit(x =>
        {
            x.SetEndpointNameFormatter(new KebabCaseEndpointNameFormatter($"{opcoes.Prefixo}-{Servico}", false));

            x.AddDelayedMessageScheduler();

            // Estado da saga no mesmo GarageOSDbContext da OS: a transição da
            // saga, a mudança de status da OS e as mensagens do outbox são
            // gravadas no mesmo commit. Concorrência otimista pelo xmin.
            x.AddSagaStateMachine<OrdemDeServicoStateMachine, OrdemDeServicoSagaState>()
                .EntityFrameworkRepository(r =>
                {
                    r.ConcurrencyMode = ConcurrencyMode.Optimistic;
                    r.ExistingDbContext<GarageOSDbContext>();
                    r.UsePostgres();
                });

            // Outbox transacional: o que os use cases publicam vai para uma
            // tabela na mesma transação e é entregue depois do commit. O inbox
            // descarta mensagem repetida (mesmo MessageId) antes da saga ver.
            x.AddEntityFrameworkOutbox<GarageOSDbContext>(o =>
            {
                o.UsePostgres();
                o.UseBusOutbox();
            });

            x.AddConfigureEndpointsCallback((context, _, cfg) =>
                cfg.UseEntityFrameworkOutbox<GarageOSDbContext>(context));

            if (opcoes.EmMemoria)
            {
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.UseMessageRetry(r => r.Intervals(100, 500, 1000));
                    cfg.ConfigureEndpoints(context);
                });
                return;
            }

            x.UsingAmazonSqs((context, cfg) =>
            {
                cfg.Host(opcoes.Regiao, h =>
                {
                    if (string.IsNullOrWhiteSpace(opcoes.ServiceUrl))
                        return;

                    // LocalStack: mesmo endpoint para SQS e SNS, credencial qualquer.
                    h.Config(new AmazonSQSConfig { ServiceURL = opcoes.ServiceUrl });
                    h.Config(new AmazonSimpleNotificationServiceConfig { ServiceURL = opcoes.ServiceUrl });
                    h.AccessKey("local");
                    h.SecretKey("local");
                });

                cfg.MessageTopology.SetEntityNameFormatter(
                    new PrefixEntityNameFormatter(cfg.MessageTopology.EntityNameFormatter, $"{opcoes.Prefixo}-"));
                PrefixarTopicosDosContratos(cfg, opcoes.Prefixo);

                // Prazos da saga: atraso nativo do SQS (até 15 minutos).
                cfg.UseDelayedMessageScheduler();

                cfg.UseMessageRetry(r => r.Intervals(500, 2000, 5000, 15000));
                cfg.ConfigureEndpoints(context);
            });
        });

        services.Configure<SagaOptions>(configuration.GetSection(SagaOptions.Secao));
        services.AddScoped<GarageOS.Application.Abstractions.IConsultaDaSaga, ConsultaDaSaga>();

        services.AddScoped<GarageOS.Application.Abstractions.IPublicadorDeEventos, PublicadorDeEventos>();

        return services;
    }

    /// <summary>Põe o prefixo do ambiente nos tópicos dos contratos</summary>
    /// <remarks>
    /// O [EntityName] tem prioridade sobre o formatador de nomes, que por isso só vale
    /// para as mensagens sem o atributo, como as de falha (Fault). Sem isto, o tópico
    /// sai sem prefixo, e a role do pod recusa: ela só libera garageos-{ambiente}-*.
    /// </remarks>
    public static void PrefixarTopicosDosContratos(IAmazonSqsBusFactoryConfigurator cfg, string prefixo)
    {
        var definir = typeof(MensageriaExtensions).GetMethod(nameof(DefinirTopico), BindingFlags.NonPublic | BindingFlags.Static)!;

        foreach (var tipo in typeof(GerarOrcamento).Assembly.GetTypes().Where(t => t.Namespace == typeof(GerarOrcamento).Namespace))
        {
            if (tipo.GetCustomAttribute<EntityNameAttribute>() is { } atributo)
                definir.MakeGenericMethod(tipo).Invoke(null, [cfg, $"{prefixo}-{atributo.EntityName}"]);
        }
    }

    private static void DefinirTopico<T>(IAmazonSqsBusFactoryConfigurator cfg, string nome) where T : class =>
        cfg.Message<T>(m => m.SetEntityName(nome));
}
