using Serilog.Context;

namespace GarageOS.Api.Middlewares;

/// <summary>
/// Garante que toda requisição tenha um identificador de correlação, e que ele
/// apareça em todas as linhas de log geradas durante o seu processamento.
/// </summary>
/// <remarks>
/// Atende ao requisito de "logs estruturados com correlação entre requisições".
/// O identificador é publicado no <c>LogContext</c> do Serilog, então qualquer
/// log emitido abaixo deste ponto do pipeline o carrega automaticamente — sem
/// precisar passá-lo à mão por toda a stack.
/// </remarks>
public class CorrelationIdMiddleware
{
    /// <summary>Header usado para receber e devolver o identificador de correlação</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Nome da propriedade sob a qual o identificador aparece nos logs</summary>
    public const string LogPropertyName = "CorrelationId";

    private readonly RequestDelegate _next;

    /// <summary>Inicializa o middleware com o próximo delegate do pipeline</summary>
    public CorrelationIdMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Resolve o identificador, publica no contexto de log e o devolve na resposta</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = ResolverCorrelationId(context);

        // Disponível para quem precisar dele dentro da requisição.
        context.Items[LogPropertyName] = correlationId;

        // Para as mensagens publicadas durante a requisição levarem o mesmo id.
        GarageOS.Infrastructure.Mensageria.Correlacao.Atual = correlationId;

        // Devolvido ao chamador para que ele consiga correlacionar do lado dele.
        // Definido antes de seguir o pipeline, senão a resposta pode já ter
        // começado a ser escrita e o header seria ignorado.
        context.Response.Headers[HeaderName] = correlationId;

        using (LogContext.PushProperty(LogPropertyName, correlationId))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Reaproveita o identificador enviado pelo chamador quando existe — é isso
    /// que permite seguir uma operação que atravessa API Gateway, Lambda e API.
    /// Sem header, gera um novo.
    /// </summary>
    private static string ResolverCorrelationId(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var recebido)
            && !string.IsNullOrWhiteSpace(recebido))
        {
            return recebido.ToString();
        }

        return Guid.NewGuid().ToString("N");
    }
}
