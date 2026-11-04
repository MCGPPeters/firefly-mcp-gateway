using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace FireflyMcpGateway.Tests;

/// <summary>The OAuth 2.1 resource-server behaviour the README documents under "Verified behaviour".</summary>
public sealed class AuthTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static StringContent Initialize() =>
        new("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Protected_resource_metadata_is_public()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-protected-resource");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(GatewayFactory.Resource, document.RootElement.GetProperty("resource").GetString());
        Assert.Equal(GatewayFactory.Issuer, document.RootElement.GetProperty("authorization_servers")[0].GetString());
    }

    [Fact]
    public async Task Path_suffixed_metadata_is_served_too()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_is_public()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_call_without_a_token_is_challenged_with_the_metadata_pointer()
    {
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/mcp", Initialize());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = response.Headers.WwwAuthenticate.ToString();
        Assert.Contains("resource_metadata=\"https://mcp.example.com/.well-known/oauth-protected-resource\"", challenge);
    }

    [Fact]
    public async Task A_token_for_another_audience_is_rejected()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GatewayFactory.CreateToken(audience: "https://elsewhere.example.com/mcp"));

        var response = await client.PostAsync("/mcp", Initialize());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("error=\"invalid_token\"", response.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task A_valid_token_is_let_through()
    {
        factory.Upstream.Respond = () => FakeUpstream.Json("""{"jsonrpc":"2.0","id":1,"result":{}}""");
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsync("/mcp", Initialize());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
