using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FireflyMcpGateway.Slimming;

namespace FireflyMcpGateway.Tests;

/// <summary>Proxying to the upstream, with slimming on (the default).</summary>
public sealed class ProxyTests(GatewayFactory factory) : IClassFixture<GatewayFactory>
{
    private static readonly ResultSlimmer Slimmer = new(new SlimmingOptions());

    private static StringContent ToolCall() =>
        new("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"search_transactions","arguments":{"query":"x"}}}""",
            Encoding.UTF8, "application/json");

    private static string ToolText(byte[] message) =>
        JsonNode.Parse(message)!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Fact]
    public async Task Forwards_the_call_without_the_callers_token_or_compression()
    {
        factory.Upstream.Respond = () => FakeUpstream.Json("""{"jsonrpc":"2.0","id":1,"result":{"content":[]}}""");
        using var client = factory.CreateAuthorizedClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = ToolCall() };
        request.Headers.TryAddWithoutValidation("Accept-Encoding", "gzip, br");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var (forwarded, body) = factory.Upstream.LastRequest;
        Assert.False(forwarded.Headers.Contains("Authorization"));
        Assert.False(forwarded.Headers.Contains("Accept-Encoding"));
        Assert.Contains("\"search_transactions\"", body);
    }

    [Fact]
    public async Task Slims_a_tool_result_returned_as_json()
    {
        factory.Upstream.Respond = () => FakeUpstream.Json(Fixture.Envelope(Fixture.Text("list_bill")));
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsync("/mcp", ToolCall());
        var body = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(body.Length, response.Content.Headers.ContentLength);
        Assert.Equal(Slimmer.SlimToolText(Fixture.Text("list_bill")), ToolText(body));
    }

    [Fact]
    public async Task Slims_a_tool_result_returned_as_an_event_stream()
    {
        var stream = Encoding.UTF8.GetBytes(
            "event: message\nid: stream-1_3\ndata: " + Fixture.Envelope(Fixture.Text("search_transactions")) + "\n\n");
        factory.Upstream.Respond = () => FakeUpstream.EventStream(stream, chunkSize: 1000);
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsync("/mcp", ToolCall());
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("event: message\nid: stream-1_3\ndata: ", body);
        Assert.EndsWith("\n\n", body);
        var data = body.Split('\n')[2]["data: ".Length..];
        Assert.Equal(Slimmer.SlimToolText(Fixture.Text("search_transactions")), ToolText(Encoding.UTF8.GetBytes(data)));
    }

    [Fact]
    public async Task Passes_a_result_it_does_not_know_through_byte_for_byte()
    {
        var envelope = Fixture.Envelope("Error: Unknown tool requested: frobnicate");
        factory.Upstream.Respond = () => FakeUpstream.Json(envelope);
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsync("/mcp", ToolCall());

        Assert.Equal(Encoding.UTF8.GetBytes(envelope), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Passes_an_error_status_through_unchanged()
    {
        var envelope = Fixture.Envelope(Fixture.Text("list_bill"));
        factory.Upstream.Respond = () => FakeUpstream.Json(envelope, HttpStatusCode.BadRequest);
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsync("/mcp", ToolCall());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(envelope, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Leaves_get_streams_alone()
    {
        var stream = Encoding.UTF8.GetBytes(
            "event: message\nid: stream-1_9\ndata: " + Fixture.Envelope(Fixture.Text("get_account")) + "\n\n");
        factory.Upstream.Respond = () => FakeUpstream.EventStream(stream, chunkSize: 1000);
        using var client = factory.CreateAuthorizedClient();

        var response = await client.GetAsync("/mcp");

        Assert.Equal(stream, await response.Content.ReadAsByteArrayAsync());
    }
}

/// <summary>Slimming switched off in configuration.</summary>
public sealed class DisabledSlimmingTests
{
    [Fact]
    public async Task Passes_tool_results_through_unchanged()
    {
        await using var factory = new GatewayFactory { Slimming = new SlimmingOptions { Enabled = false } };
        var envelope = Fixture.Envelope(Fixture.Text("search_transactions"));
        factory.Upstream.Respond = () => FakeUpstream.Json(envelope);
        using var client = factory.CreateAuthorizedClient();

        var response = await client.PostAsync(
            "/mcp", new StringContent("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{}}""", Encoding.UTF8, "application/json"));

        Assert.Equal(envelope, await response.Content.ReadAsStringAsync());
    }
}
