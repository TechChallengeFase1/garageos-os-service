FROM mcr.microsoft.com/dotnet/sdk:10.0 AS compilacao
WORKDIR /src

COPY ["Code/GarageOS.Api/GarageOS.Api.csproj", "GarageOS.Api/"]
COPY ["Code/GarageOS.Application/GarageOS.Application.csproj", "GarageOS.Application/"]
COPY ["Code/GarageOS.Domain/GarageOS.Domain.csproj", "GarageOS.Domain/"]
COPY ["Code/GarageOS.Infrastructure/GarageOS.Infrastructure.csproj", "GarageOS.Infrastructure/"]

RUN dotnet restore "GarageOS.Api/GarageOS.Api.csproj"

COPY Code/ .

RUN dotnet publish "GarageOS.Api/GarageOS.Api.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN groupadd --system appuser && useradd --system --gid appuser appuser

# ─── Agente do New Relic ─────────────────────────────────────────────────────
#
# Copiado da imagem oficial do agente, em vez de instalado via apt. Assim nao
# entra repositorio extra, chave GPG nem `apt-get update` inflando a imagem.
#
# O agente age como PROFILER DO CLR - dai as variaveis CORECLR_* abaixo. Ele
# instrumenta ASP.NET Core, Entity Framework Core e Npgsql automaticamente, sem
# uma linha de codigo de tracing na aplicacao.
#
# A imagem e a `newrelic-dotnet-init`, publicada pela New Relic para
# auto-instrumentacao no Kubernetes. Dentro dela o agente fica em
# /instrumentation - por isso a origem e o destino do COPY tem nomes
# diferentes. Nao existe imagem `newrelic-dotnet-agent` no Docker Hub.
#
# Versao fixa: a mesma nos tres servicos, e o build nao muda sozinho.
COPY --from=newrelic/newrelic-dotnet-init:10.57.0      /instrumentation /usr/local/newrelic-dotnet-agent

# O agente grava logs proprios dentro do diretorio dele, e a aplicacao roda sem
# privilegio (USER appuser mais abaixo). Sem este chown ele falha ao iniciar.
RUN chown -R appuser:appuser /usr/local/newrelic-dotnet-agent

ENV CORECLR_ENABLE_PROFILING=1     CORECLR_PROFILER="{36032161-FFC0-4B61-B559-F6C5D41BAE5A}"     CORECLR_NEWRELIC_HOME=/usr/local/newrelic-dotnet-agent     CORECLR_PROFILER_PATH=/usr/local/newrelic-dotnet-agent/libNewRelicProfiler.so

# Encaminha os logs da aplicacao ao New Relic e injeta trace.id e span.id em
# cada linha. E isto que liga LOG a TRACE na interface - o requisito de
# "correlacao entre requisicoes" visto ponta a ponta. CONTEXT_DATA leva junto
# as propriedades do LogContext (CorrelationId, OrdemDeServicoId, MessageType).
ENV NEW_RELIC_APPLICATION_LOGGING_ENABLED=true     NEW_RELIC_APPLICATION_LOGGING_FORWARDING_ENABLED=true     NEW_RELIC_APPLICATION_LOGGING_FORWARDING_CONTEXT_DATA_ENABLED=true     NEW_RELIC_DISTRIBUTED_TRACING_ENABLED=true

# NEW_RELIC_LICENSE_KEY e NEW_RELIC_APP_NAME NAO entram na imagem: a chave e
# segredo e viria junto em qualquer push. Vem do ambiente - .env no local,
# Secret do Kubernetes no cluster.

COPY --from=compilacao /app/publish .
RUN chown -R appuser:appuser /app

USER appuser

EXPOSE 8080
ENTRYPOINT ["dotnet", "GarageOS.Api.dll"]