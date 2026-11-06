using FluentAssertions;
using GarageOS.Infrastructure.ExternalServices;
using Microsoft.AspNetCore.Http;

namespace GarageOS.UnitTests.Infrastructure;

public class PropagarCabecalhosHandlerTests
{
    private sealed class Destino : HttpMessageHandler
    {
        public HttpRequestMessage? Recebido { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Recebido = request;
            return Task.FromResult(new HttpResponseMessage());
        }
    }

    private static async Task<HttpRequestMessage> Enviar(HttpContext? contexto, HttpRequestMessage? pedido = null)
    {
        var destino = new Destino();
        var cliente = new HttpClient(new PropagarCabecalhosHandler(new HttpContextAccessor { HttpContext = contexto }) { InnerHandler = destino });

        await cliente.SendAsync(pedido ?? new HttpRequestMessage(HttpMethod.Get, "http://execucao.test/"));

        return destino.Recebido!;
    }

    [Fact]
    public async Task DeveRepassarTokenECorrelacaoDaRequisicaoAtual()
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Headers.Authorization = "Bearer abc";
        contexto.Request.Headers["X-Correlation-Id"] = "corr-1";

        var enviado = await Enviar(contexto);

        enviado.Headers.GetValues("Authorization").Should().ContainSingle("Bearer abc");
        enviado.Headers.GetValues("X-Correlation-Id").Should().ContainSingle("corr-1");
    }

    [Fact]
    public async Task CorrelacaoGeradaPeloMiddleware_DeveSeguirAdiante()
    {
        var contexto = new DefaultHttpContext();
        contexto.Items["CorrelationId"] = "gerado-1";

        var enviado = await Enviar(contexto);

        enviado.Headers.GetValues("X-Correlation-Id").Should().ContainSingle("gerado-1");
        enviado.Headers.Contains("Authorization").Should().BeFalse();
    }

    [Fact]
    public async Task CabecalhoJaDefinido_NaoDeveSerSobrescrito()
    {
        var contexto = new DefaultHttpContext();
        contexto.Request.Headers["X-Correlation-Id"] = "da-requisicao";
        var pedido = new HttpRequestMessage(HttpMethod.Get, "http://execucao.test/");
        pedido.Headers.Add("X-Correlation-Id", "do-chamador");

        var enviado = await Enviar(contexto, pedido);

        enviado.Headers.GetValues("X-Correlation-Id").Should().ContainSingle("do-chamador");
    }

    [Fact]
    public async Task ForaDeUmaRequisicao_DeveSeguirSemCabecalhos()
    {
        // Consumidor da saga, por exemplo: não há HttpContext.
        var enviado = await Enviar(null);

        enviado.Headers.Contains("Authorization").Should().BeFalse();
        enviado.Headers.Contains("X-Correlation-Id").Should().BeFalse();
    }
}
