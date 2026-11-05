using System.Reflection;
using FluentAssertions;
using GarageOS.Infrastructure.Mensageria;
using GarageOS.Contracts.V1;
using MassTransit;

namespace GarageOS.UnitTests.Infrastructure;

/// <summary>Os testes de consumidor rodam em memória, onde tópico não existe. Aqui vale o nome real no SNS.</summary>
public class TopicosDaMensageriaTests
{
    private static IMessageTopology Topologia(string prefixo)
    {
        IMessageTopology? topologia = null;

        Bus.Factory.CreateUsingAmazonSqs(cfg =>
        {
            cfg.Host("us-east-1", h =>
            {
                h.AccessKey("x");
                h.SecretKey("x");
            });
            MensageriaExtensions.PrefixarTopicosDosContratos(cfg, prefixo);
            topologia = cfg.MessageTopology;
        });

        return topologia!;
    }

    [Fact]
    public void Contratos_DevemTerOPrefixoQueARoleDoPodLibera()
    {
        var topologia = Topologia("garageos-producao");

        topologia.GetMessageTopology<GerarOrcamento>().EntityName.Should().Be("garageos-producao-gerar-orcamento");
        topologia.GetMessageTopology<PagamentoConfirmado>().EntityName.Should().Be("garageos-producao-pagamento-confirmado");
    }

    [Fact]
    public void TodoContratoComNome_DeveReceberOPrefixo()
    {
        var topologia = Topologia("garageos-producao");
        var contratos = typeof(GerarOrcamento).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(GerarOrcamento).Namespace && t.GetCustomAttribute<EntityNameAttribute>() is not null)
            .ToList();

        contratos.Should().NotBeEmpty();
        foreach (var tipo in contratos)
        {
            var nome = (string)typeof(TopicosDaMensageriaTests).GetMethod(nameof(NomeDoTopico), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(tipo).Invoke(null, [topologia])!;

            nome.Should().StartWith("garageos-producao-");
        }
    }

    private static string NomeDoTopico<T>(IMessageTopology topologia) where T : class => topologia.GetMessageTopology<T>().EntityName;
}
