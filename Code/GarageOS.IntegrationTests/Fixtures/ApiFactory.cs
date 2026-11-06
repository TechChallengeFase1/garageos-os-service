using GarageOS.Application.Abstractions;
using GarageOS.Infrastructure.Data;
using GarageOS.IntegrationTests.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace GarageOS.IntegrationTests.Fixtures;

public class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Estoque do Execução Service, simulado</summary>
    public EstoqueFalso Estoque { get; } = new();

    private readonly PostgreSqlContainer _pgContainer = new PostgreSqlBuilder("postgres:16")
        .WithDatabase("garageos_tests")
        .WithUsername("test")
        .WithPassword("test")
        .Build();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Sem SQS nos testes: o MassTransit roda em memória, com o outbox e o
        // repositório da saga de verdade, no PostgreSQL do container.
        builder.UseSetting("Mensageria:Transporte", "InMemory");
        builder.UseSetting("Jwt:SecretKey", "chave-de-teste-com-pelo-menos-32-caracteres");

        builder.ConfigureServices(services =>
        {
            var toRemove = services
                .Where(d =>
                    d.ServiceType == typeof(DbContextOptions<GarageOSDbContext>) ||
                    d.ServiceType == typeof(GarageOSDbContext) ||
                    (d.ServiceType.IsGenericType &&
                     d.ServiceType.GetGenericTypeDefinition().FullName != null &&
                     d.ServiceType.GetGenericTypeDefinition().FullName!
                         .Contains("IDbContextOptionsConfiguration")))
                .ToList();

            foreach (var descriptor in toRemove)
                services.Remove(descriptor);

            services.AddDbContext<GarageOSDbContext>(options =>
                options.UseNpgsql(_pgContainer.GetConnectionString()));

            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IEstoqueClient)).ToList())
                services.Remove(descriptor);
            services.AddSingleton<IEstoqueClient>(Estoque);

            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            })
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                "Test", options => { });
        });
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        await _pgContainer.StartAsync();

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GarageOSDbContext>();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _pgContainer.DisposeAsync();
        await base.DisposeAsync();
    }
}
