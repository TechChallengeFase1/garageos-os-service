using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FluentAssertions;
using GarageOS.Api.Controllers;
using GarageOS.Application.DTOs.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace GarageOS.UnitTests.Api;

public class AuthControllerTests
{
    private const string Senha = "senha-de-teste";

    private static AuthController Controller(string? senhaConfigurada = Senha) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:Username"] = "admin",
            ["Admin:Password"] = senhaConfigurada,
            ["Jwt:SecretKey"] = "chave-de-teste-com-pelo-menos-32-caracteres",
            ["Jwt:Issuer"] = "emissor",
            ["Jwt:Audience"] = "publico",
            ["Jwt:ExpiresInMinutes"] = "60"
        })
        .Build());

    [Fact]
    public void Login_CredenciaisCertas_DeveDevolverTokenDeAdmin()
    {
        var resposta = Controller().Login(new LoginRequest { Username = "admin", Password = Senha });

        var token = ((OkObjectResult)resposta).Value.Should().BeOfType<TokenResponse>().Subject;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token.Token);
        jwt.Issuer.Should().Be("emissor");
        jwt.Audiences.Should().ContainSingle("publico");
        jwt.Claims.Should().Contain(c => c.Type == ClaimTypes.Role && c.Value == "Admin");
    }

    [Theory]
    [InlineData("admin", "errada")]
    [InlineData("outro", Senha)]
    public void Login_CredenciaisErradas_DeveRetornar401(string usuario, string senha)
    {
        var resposta = Controller().Login(new LoginRequest { Username = usuario, Password = senha });

        resposta.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public void Login_SemSenhaConfigurada_NaoDeveAceitarSenhaVazia()
    {
        var resposta = Controller(senhaConfigurada: null).Login(new LoginRequest { Username = "admin", Password = "" });

        resposta.Should().BeOfType<UnauthorizedObjectResult>();
    }
}
