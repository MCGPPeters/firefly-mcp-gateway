using System.Net.Http.Headers;
using System.Text;
using FireflyMcpGateway.Slimming;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Yarp.ReverseProxy.Forwarder;

namespace FireflyMcpGateway.Tests;

/// <summary>
/// Hosts the gateway in memory against <see cref="FakeUpstream"/>, with tokens signed by a local key
/// instead of Keycloak. Everything else comes from src/FireflyMcpGateway/appsettings.json.
/// </summary>
public class GatewayFactory : WebApplicationFactory<Program>
{
    // The example values in appsettings.json.
    public const string Issuer = "https://keycloak.example.com/realms/main";
    public const string Resource = "https://mcp.example.com/mcp";

    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes("a-test-only-signing-key-that-is-long-enough-for-hs256"));

    public FakeUpstream Upstream { get; } = new();

    /// <summary>Replaces the slimmer the gateway builds from configuration.</summary>
    public SlimmingOptions? Slimming { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IForwarderHttpClientFactory>(new FakeUpstreamClientFactory(Upstream));

            if (Slimming is not null)
            {
                services.AddSingleton(new ResultSlimmer(Slimming));
            }

            // A static configuration keeps the handler from fetching Keycloak's discovery document.
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Configuration = new OpenIdConnectConfiguration { Issuer = Issuer };
                options.Configuration.SigningKeys.Add(SigningKey);
                options.TokenValidationParameters.IssuerSigningKey = SigningKey;
            });
        });
    }

    public HttpClient CreateAuthorizedClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", CreateToken());
        return client;
    }

    public static string CreateToken(string audience = Resource) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["preferred_username"] = "test" }
        });
}
