using GarageOS.Api.Middlewares;
using GarageOS.Api.Seguranca;
using GarageOS.Application.UseCases.Clientes;
using GarageOS.Application.UseCases.OrdensDeServico;
using GarageOS.Application.UseCases.Servicos;
using GarageOS.Application.UseCases.Veiculos;
using GarageOS.Application.Validators.Veiculos;
using GarageOS.Domain.Repositories;
using GarageOS.Application.Abstractions;
using GarageOS.Infrastructure.Data;
using GarageOS.Infrastructure.ExternalServices;
using GarageOS.Infrastructure.Observabilidade;
using GarageOS.Infrastructure.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Text;

namespace GarageOS.Api.Extensions;

/// <summary>Extensões de configuração de serviços da aplicação GarageOS</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>Registra a infraestrutura: DbContext e repositórios</summary>
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        services.AddDbContext<GarageOSDbContext>(options =>
            options.UseNpgsql(connectionString));

        services.AddScoped<IServicoRepository, ServicoRepository>();
        services.AddScoped<IVeiculoRepository, VeiculoRepository>();
        services.AddScoped<IClienteRepository, ClienteRepository>();
        services.AddScoped<IOrdemDeServicoRepository, OrdemDeServicoRepository>();

        // Estoque do Execução Service, por HTTP. Base URL por configuração: no
        // cluster é o Service interno do namespace garageos-execucao.
        services.AddHttpContextAccessor();
        services.AddTransient<PropagarCabecalhosHandler>();
        services.AddHttpClient<IEstoqueClient, EstoqueClient>(client =>
                client.BaseAddress = new Uri(configuration["Servicos:Execucao:BaseUrl"]!))
            .AddHttpMessageHandler<PropagarCabecalhosHandler>()
            .AddStandardResilienceHandler();

        services.AddScoped<IContextoDaRequisicao, ContextoDaRequisicao>();

        // Publicacao de eventos de negocio. Singleton porque nao guarda estado
        // por requisicao e o agente do New Relic e global ao processo.
        services.AddSingleton<IMetricasDeNegocio, MetricasNewRelic>();

        return services;
    }

    /// <summary>Registra todos os use cases e validators da camada de aplicação</summary>
    public static IServiceCollection AddApplicationServices(
        this IServiceCollection services)
    {
        services.AddScoped<ListarServicosUseCase>();
        services.AddScoped<CadastrarServicoUseCase>();
        services.AddScoped<ObterServicoUseCase>();
        services.AddScoped<AlterarServicoUseCase>();

        services.AddScoped<ListarVeiculosUseCase>();
        services.AddScoped<ObterVeiculoUseCase>();
        services.AddScoped<CadastrarVeiculoUseCase>();
        services.AddScoped<AlterarVeiculoUseCase>();
        services.AddScoped<CriarVeiculoValidator>();
        services.AddScoped<VincularVeiculoClienteUseCase>();
        services.AddScoped<DeletarVeiculoUseCase>();
        
        services.AddScoped<ListarClientesUseCase>();
        services.AddScoped<CadastrarClienteUseCase>();
        services.AddScoped<ObterClienteUseCase>();
        services.AddScoped<AlterarClienteUseCase>();
        services.AddScoped<DeletarClienteUseCase>();


        services.AddScoped<AbrirOrdemDeServicoCompletaUseCase>();
        services.AddScoped<ListarOrdensDeServicoUseCase>();
        services.AddScoped<ObterOrdemDeServicoUseCase>();
        services.AddScoped<AdicionarServicoNaOSUseCase>();
        services.AddScoped<AdicionarPecaNaOSUseCase>();
        services.AddScoped<AlterarStatusOrdemDeServicoUseCase>();
        services.AddScoped<AcompanharOrdemDeServicoUseCase>();
        services.AddScoped<SolicitarOrcamentoUseCase>();
        services.AddScoped<ObterSagaUseCase>();

        return services;
    }

    /// <summary>Tag que marca os health checks pertencentes à prontidão (readiness)</summary>
    public const string TagProntidao = "ready";

    /// <summary>Registra os middlewares da aplicação GarageOS</summary>
    /// <remarks>
    /// A ordem importa. O correlation ID vem primeiro para que TUDO abaixo dele
    /// — inclusive o log de erro do ExceptionMiddleware — saia carimbado com o
    /// identificador da requisição.
    /// </remarks>
    public static IApplicationBuilder UseGarageOSMiddlewares(
        this IApplicationBuilder app)
    {
        app.UseMiddleware<CorrelationIdMiddleware>();

        // Uma linha estruturada por requisição, com rota, status e duração.
        // Como roda dentro do escopo do correlation ID, herda o identificador.
        app.UseSerilogRequestLogging();

        app.UseMiddleware<ExceptionMiddleware>();

        return app;
    }

    /// <summary>Registra os health checks de liveness e readiness</summary>
    public static IServiceCollection AddGarageOSHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")!;

        services
            .AddHealthChecks()
            .AddNpgSql(
                connectionString,
                name: "postgres",
                tags: [TagProntidao]);

        return services;
    }

    /// <summary>Mapeia os endpoints <c>/health/live</c> e <c>/health/ready</c></summary>
    /// <remarks>
    /// A distinção entre os dois é deliberada e importa em produção.
    ///
    /// O liveness NÃO consulta o banco: se o RDS oscilar, o Kubernetes mataria
    /// todos os pods ao mesmo tempo, transformando uma instabilidade de banco
    /// numa queda total da aplicação.
    ///
    /// O readiness consulta: o pod sai do balanceamento enquanto o banco não
    /// responde, e volta sozinho quando ele voltar. Ninguém é reiniciado.
    /// </remarks>
    public static IEndpointRouteBuilder MapGarageOSHealthChecks(
        this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            // Predicate falso = nenhum check roda. Responde 200 se o processo
            // consegue atender uma requisição HTTP, que é o que liveness mede.
            Predicate = _ => false,
            ResponseWriter = EscreverRespostaHealthAsync
        });

        endpoints.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(TagProntidao),
            ResponseWriter = EscreverRespostaHealthAsync
        });

        return endpoints;
    }

    /// <summary>Serializa o resultado dos health checks em JSON legível</summary>
    private static Task EscreverRespostaHealthAsync(HttpContext context, HealthReport report)
    {
        var resposta = new
        {
            status = report.Status.ToString(),
            duracaoMs = Math.Round(report.TotalDuration.TotalMilliseconds, 1),
            checks = report.Entries.Select(entrada => new
            {
                nome = entrada.Key,
                status = entrada.Value.Status.ToString(),
                erro = entrada.Value.Exception?.Message
            })
        };

        return context.Response.WriteAsJsonAsync(resposta, context.RequestAborted);
    }

    /// <summary>Configura a autenticação JWT com validação de token</summary>
    public static IServiceCollection AddJwtAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var secretKey = configuration["Jwt:SecretKey"]!;
        var issuer = configuration["Jwt:Issuer"]!;
        var audience = configuration["Jwt:Audience"]!;

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                                                  Encoding.UTF8.GetBytes(secretKey)),
                    ClockSkew = TimeSpan.FromMinutes(5)
                };
            });

        // Policies por papel. Até a Fase 3 bastava estar autenticado, então um
        // token de cliente (emitido pela Lambda a partir do CPF) chamava os
        // CRUDs administrativos.
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Politicas.Admin, policy => policy.RequireRole("Admin"));
            options.AddPolicy(Politicas.Cliente, policy => policy.RequireClaim("cpf"));
        });

        return services;
    }

    /// <summary>Configura o Swagger com suporte a autenticação JWT</summary>
    public static IServiceCollection AddSwaggerWithJwt(
        this IServiceCollection services)
    {
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new() { Title = "GarageOS API", Version = "v1" });

            var securityScheme = new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Description = "Informe o token JWT: Bearer {token}",
                In = ParameterLocation.Header,
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = JwtBearerDefaults.AuthenticationScheme
                }
            };

            options.AddSecurityDefinition(JwtBearerDefaults.AuthenticationScheme, securityScheme);
            options.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
                { securityScheme, Array.Empty<string>() }
            });

            var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFile);
            if (System.IO.File.Exists(xmlPath))
            {
                options.IncludeXmlComments(xmlPath);
            }
        });

        return services;
    }
}
