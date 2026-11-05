using FluentAssertions;
using GarageOS.Application.Abstractions;
using GarageOS.Contracts.V1;
using GarageOS.Domain.Entities;
using GarageOS.Domain.Enums;
using GarageOS.Domain.Repositories;
using GarageOS.Infrastructure.Saga;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Reqnroll;

namespace GarageOS.OsService.Bdd.Steps;

/// <summary>A saga real e a OS real, com os outros serviços simulados pelos passos</summary>
[Binding]
public class CicloDeVidaDaOSSteps
{
    private static readonly TimeSpan Espera = TimeSpan.FromSeconds(10);

    private readonly Mock<IOrdemDeServicoRepository> _repositorio = new();
    private readonly Mock<IMetricasDeNegocio> _metricas = new();

    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private OrdemDeServico _os = null!;
    private readonly Guid _orcamentoId = Guid.NewGuid();
    private readonly Guid _pagamentoId = Guid.NewGuid();

    [BeforeScenario]
    public async Task Iniciar()
    {
        _provider = new ServiceCollection()
            .AddSingleton(_repositorio.Object)
            .AddSingleton(_metricas.Object)
            .Configure<SagaOptions>(o => { o.PrazoAprovacaoMinutos = 5; o.PrazoPagamentoMinutos = 5; })
            .AddMassTransitTestHarness(x =>
            {
                x.AddDelayedMessageScheduler();
                x.AddSagaStateMachine<OrdemDeServicoStateMachine, OrdemDeServicoSagaState>().InMemoryRepository();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseDelayedMessageScheduler();
                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

        _harness = _provider.GetRequiredService<ITestHarness>();
        _harness.TestTimeout = Espera;
        await _harness.Start();
    }

    [AfterScenario]
    public async Task Encerrar()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
    }

    [Given("uma ordem de serviço com peças")]
    public void DadaUmaOS()
    {
        _os = new OrdemDeServico("OS-2026-00099", Guid.NewGuid(), Guid.NewGuid(),
            [(Guid.NewGuid(), "Revisão", 200m)],
            [(Guid.NewGuid(), "Filtro", 30m, 2)]);
        _repositorio.Setup(r => r.ObterPorIdAsync(_os.Id)).ReturnsAsync(_os);
    }

    [When("a oficina pede o orçamento")]
    public async Task QuandoAOficinaPedeOOrcamento()
    {
        await _harness.Bus.Publish(new OrdemDeServicoAberta(_os.Id, _os.NumeroOS, _os.ClienteId));
        await _harness.Bus.Publish(new OrcamentoSolicitado(_os.Id, _os.NumeroOS, _os.ClienteId,
            _os.Servicos.Select(s => new ItemDoPedido("Servico", s.ServicoId, s.Nome, 1, s.PrecoUnitario))
                .Concat(_os.Pecas.Select(p => new ItemDoPedido("Peca", p.EstoqueId, p.Nome, p.Quantidade, p.ValorUnitario))).ToList(),
            _os.Pecas.Select(p => new PecaDoPedido(p.EstoqueId, p.Quantidade)).ToList(),
            _os.Servicos.Select(s => new ServicoDoPedido(s.ServicoId, s.Nome)).ToList()));
    }

    [When("o Billing envia o orçamento ao cliente")]
    public Task QuandoOBillingEnvia() => _harness.Bus.Publish(new OrcamentoEnviado(_os.Id, _orcamentoId, 260m));

    [When("o cliente aprova o orçamento")]
    public Task QuandoOClienteAprova() => _harness.Bus.Publish(new OrcamentoAprovado(_os.Id, _orcamentoId));

    [When("o cliente recusa o orçamento")]
    public Task QuandoOClienteRecusa() => _harness.Bus.Publish(new OrcamentoRecusado(_os.Id, _orcamentoId));

    [When("o Execução reserva as peças")]
    public Task QuandoOExecucaoReserva() => _harness.Bus.Publish(new PecasReservadas(_os.Id));

    [When("o Execução informa que falta peça")]
    public Task QuandoFaltaPeca() => _harness.Bus.Publish(new PecasIndisponiveis(_os.Id, "Filtro sem estoque"));

    [When("o Mercado Pago confirma o pagamento")]
    public Task QuandoOPagamentoEConfirmado() =>
        _harness.Bus.Publish(new PagamentoConfirmado(_os.Id, _orcamentoId, _pagamentoId));

    [When("o Mercado Pago recusa o pagamento")]
    public Task QuandoOPagamentoERecusado() =>
        _harness.Bus.Publish(new PagamentoRecusado(_os.Id, _orcamentoId, _pagamentoId, "Cartão recusado"));

    [When("o Execução finaliza o serviço")]
    public Task QuandoOServicoTermina() => _harness.Bus.Publish(new ExecucaoFinalizada(_os.Id));

    [When("o Execução informa que o serviço falhou")]
    public Task QuandoOServicoFalha() => _harness.Bus.Publish(new ExecucaoFalhou(_os.Id, "Motor travado"));

    [Then(@"o (?:Billing|Execução) recebe o comando ""(.*)""")]
    public async Task EntaoOComandoEEnviado(string comando) =>
        (await FoiEnviado(comando)).Should().BeTrue($"a saga devia enviar {comando}");

    [Then(@"a ordem de serviço fica ""(.*)""")]
    public async Task EntaoAOSFica(StatusOrdemDeServico status)
    {
        // A saga é assíncrona: o status chega um pouco depois do evento.
        var limite = DateTime.UtcNow + Espera;
        while (_os.Status != status && DateTime.UtcNow < limite)
            await Task.Delay(20);

        _os.Status.Should().Be(status);
    }

    [Then("nenhuma compensação é enviada")]
    public async Task EntaoNadaECompensado()
    {
        foreach (var comando in new[] { "CancelarOrcamento", "LiberarReservaPecas", "EstornarPagamento" })
            (await FoiEnviado(comando)).Should().BeFalse($"o caminho feliz não compensa com {comando}");
    }

    [Then(@"a saga registra a compensação ""(.*)""")]
    public async Task EntaoACompensacaoERegistrada(string compensacao)
    {
        var limite = DateTime.UtcNow + Espera;
        while (_metricas.Invocations.All(i => i.Method.Name != nameof(IMetricasDeNegocio.SagaCompensou)) && DateTime.UtcNow < limite)
            await Task.Delay(20);

        _metricas.Verify(m => m.SagaCompensou(_os.NumeroOS, It.IsAny<string>(), compensacao), Times.Once);

        if (compensacao != "nenhuma")
            foreach (var comando in compensacao.Split(", "))
                (await FoiEnviado(comando)).Should().BeTrue($"a compensação inclui {comando}");
    }

    private async Task<bool> FoiEnviado(string comando) => comando switch
    {
        "GerarOrcamento" => await _harness.Published.Any<GerarOrcamento>(m => m.Context.Message.OrdemDeServicoId == _os.Id),
        "ReservarPecas" => await _harness.Published.Any<ReservarPecas>(m => m.Context.Message.OrdemDeServicoId == _os.Id),
        "SolicitarPagamento" => await _harness.Published.Any<SolicitarPagamento>(m => m.Context.Message.OrdemDeServicoId == _os.Id),
        "IniciarExecucao" => await _harness.Published.Any<IniciarExecucao>(m => m.Context.Message.OrdemDeServicoId == _os.Id),
        "CancelarOrcamento" => await _harness.Published.Any<CancelarOrcamento>(m => m.Context.Message.OrdemDeServicoId == _os.Id),
        "LiberarReservaPecas" => await _harness.Published.Any<LiberarReservaPecas>(m => m.Context.Message.OrdemDeServicoId == _os.Id),
        "EstornarPagamento" => await _harness.Published.Any<EstornarPagamento>(m => m.Context.Message.PagamentoId == _pagamentoId),
        _ => throw new ArgumentOutOfRangeException(nameof(comando), comando, "Comando desconhecido")
    };
}
