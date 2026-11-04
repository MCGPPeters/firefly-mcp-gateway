using System.Text;
using System.Text.Json.Nodes;
using FireflyMcpGateway.Slimming;

namespace FireflyMcpGateway.Tests;

public sealed class SseRewriterTests
{
    private static readonly ResultSlimmer Slimmer = new(new SlimmingOptions());

    private static async Task<byte[]> Rewrite(byte[] input, int chunkSize, ResultSlimmer? slimmer = null)
    {
        using var output = new MemoryStream();
        await SseRewriter.RewriteAsync(new ChunkedStream(input, chunkSize), output, slimmer ?? Slimmer, CancellationToken.None);
        return output.ToArray();
    }

    /// <summary>One event the way the MCP TypeScript SDK writes it when it keeps an event store.</summary>
    private static byte[] Event(string data, string newline = "\n") =>
        Encoding.UTF8.GetBytes($"event: message{newline}id: stream-1_7{newline}data: {data}{newline}{newline}");

    private static string ToolText(string data) =>
        JsonNode.Parse(data)!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    [Theory]
    [InlineData("search_transactions", "\n", 7)]
    [InlineData("search_transactions", "\n", 65536)]
    [InlineData("search_transactions", "\r\n", 3)]
    [InlineData("get_account", "\n", 1)]
    public async Task Slims_a_tool_result_and_keeps_the_event_fields(string fixture, string newline, int chunkSize)
    {
        var input = Event(Encoding.UTF8.GetString(Fixture.Message(fixture)), newline);

        var output = Encoding.UTF8.GetString(await Rewrite(input, chunkSize));

        var lines = output.Split(newline);
        Assert.Equal("event: message", lines[0]);
        Assert.Equal("id: stream-1_7", lines[1]);
        Assert.StartsWith("data: ", lines[2]);
        Assert.Equal("", lines[3]);
        Assert.Equal(5, lines.Length);
        Assert.Equal(Slimmer.SlimToolText(Fixture.Text(fixture)), ToolText(lines[2]["data: ".Length..]));
        Assert.True(output.Length < input.Length * 0.6, $"{output.Length} of {input.Length} bytes");
    }

    [Fact]
    public async Task Copies_events_it_does_not_change_byte_for_byte()
    {
        var input = Encoding.UTF8.GetBytes(
            ": keep-alive\n\n" +
            "event: message\nid: stream-1_1\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"progress\":1}}\n\n" +
            "event: message\nid: stream-1_2\ndata: " + Fixture.Envelope("Error: Unknown tool requested: frobnicate") + "\r\n\r\n" +
            "retry: 1000\n\n");

        Assert.Equal(input, await Rewrite(input, chunkSize: 5));
    }

    [Fact]
    public async Task Slims_only_the_events_that_carry_a_known_result()
    {
        var notification = "event: message\nid: stream-1_1\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{}}\n\n";
        var input = Encoding.UTF8.GetBytes(notification).Concat(Event(Encoding.UTF8.GetString(Fixture.Message("list_bill")))).ToArray();

        var output = Encoding.UTF8.GetString(await Rewrite(input, chunkSize: 4096));

        Assert.StartsWith(notification, output);
        var slimmedEvent = output[notification.Length..].Split('\n');
        Assert.Equal(Slimmer.SlimToolText(Fixture.Text("list_bill")), ToolText(slimmedEvent[2]["data: ".Length..]));
    }

    [Fact]
    public async Task Joins_data_spread_over_several_lines()
    {
        var message = Encoding.UTF8.GetString(Fixture.Message("get_account"));
        // A line feed between two JSON tokens is whitespace, so the joined value is the same message.
        var input = Encoding.UTF8.GetBytes("event: message\ndata: {\ndata: " + message[1..] + "\n\n");

        var output = Encoding.UTF8.GetString(await Rewrite(input, chunkSize: 64));

        var lines = output.Split('\n');
        Assert.Equal("event: message", lines[0]);
        Assert.Single(lines, line => line.StartsWith("data:", StringComparison.Ordinal));
        Assert.Equal(Slimmer.SlimToolText(Fixture.Text("get_account")), ToolText(lines[1]["data: ".Length..]));
    }

    [Fact]
    public async Task Copies_an_event_cut_off_by_the_end_of_the_stream_unchanged()
    {
        var input = Encoding.UTF8.GetBytes("event: message\ndata: " + Encoding.UTF8.GetString(Fixture.Message("get_account")));

        Assert.Equal(input, await Rewrite(input, chunkSize: 100));
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(100)]
    public async Task Copies_an_event_over_the_size_limit_unchanged(int chunkSize)
    {
        // Between the two events: search_transactions is about 100 KB, get_account about 3 KB.
        var small = new ResultSlimmer(new SlimmingOptions { MaxMessageBytes = 16 * 1024 });
        var big = Event(Encoding.UTF8.GetString(Fixture.Message("search_transactions")));
        var after = Event(Encoding.UTF8.GetString(Fixture.Message("get_account")));
        var input = big.Concat(after).ToArray();

        var output = await Rewrite(input, chunkSize, small);

        // The large event passes untouched; the one after it is small enough to slim again.
        Assert.Equal(big, output[..big.Length]);
        var rest = Encoding.UTF8.GetString(output[big.Length..]).Split('\n');
        Assert.Equal(Slimmer.SlimToolText(Fixture.Text("get_account")), ToolText(rest[2]["data: ".Length..]));
    }
}
