using FluentAssertions;
using GarageOS.Infrastructure.Mensageria;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace GarageOS.UnitTests.Infrastructure;

/// <summary>O correlationId da requisição precisa atravessar as mensagens</summary>
public class CorrelacaoNasMensagensTests : IAsyncLifetime
{
    public record Pedido(Guid OrdemDeServicoId);
    public record Resposta(Guid OrdemDeServicoId);

    public class ConsumidorDoPedido : IConsumer<Pedido>
    {
        public static string? Visto;

        public Task Consume(ConsumeContext<Pedido> context)
        {
            Visto = Correlacao.Atual;
            return context.Publish(new Resposta(context.Message.OrdemDeServicoId));
        }
    }

    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<ConsumidorDoPedido>();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UsarCorrelacao(context);
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);
        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    [Fact]
    public async Task IdDaRequisicao_DeveSeguirNaMensagemENaQueEleGera()
    {
        var os = Guid.NewGuid();
        Correlacao.Atual = "req-123";
        try
        {
            await _harness.Bus.Publish(new Pedido(os));
        }
        finally
        {
            Correlacao.Atual = null;
        }

        (await _harness.Consumed.Any<Pedido>(m => m.Context.Message.OrdemDeServicoId == os)).Should().BeTrue();
        ConsumidorDoPedido.Visto.Should().Be("req-123");

        var resposta = _harness.Published.Select<Resposta>(m => m.Context.Message.OrdemDeServicoId == os).Single();
        resposta.Context.Headers.Get<string>(Correlacao.Cabecalho).Should().Be("req-123");
    }

    [Fact]
    public async Task MensagemSemId_DeveGanharUmNovoQueSegueAdiante()
    {
        var os = Guid.NewGuid();
        Correlacao.Atual = null;

        await _harness.Bus.Publish(new Pedido(os));

        (await _harness.Published.Any<Resposta>(m => m.Context.Message.OrdemDeServicoId == os)).Should().BeTrue();
        var id = _harness.Published.Select<Resposta>(m => m.Context.Message.OrdemDeServicoId == os).Single()
            .Context.Headers.Get<string>(Correlacao.Cabecalho);
        id.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CabecalhoJaDefinido_DeveSerRespeitado()
    {
        var os = Guid.NewGuid();
        Correlacao.Atual = "do-contexto";
        try
        {
            await _harness.Bus.Publish(new Pedido(os), c => c.Headers.Set(Correlacao.Cabecalho, "do-chamador"));
        }
        finally
        {
            Correlacao.Atual = null;
        }

        (await _harness.Consumed.Any<Pedido>(m => m.Context.Message.OrdemDeServicoId == os)).Should().BeTrue();
        _harness.Published.Select<Pedido>(m => m.Context.Message.OrdemDeServicoId == os).Single()
            .Context.Headers.Get<string>(Correlacao.Cabecalho).Should().Be("do-chamador");
    }
}
