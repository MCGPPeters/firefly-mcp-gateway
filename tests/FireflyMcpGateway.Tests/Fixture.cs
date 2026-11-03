using System.Text;
using System.Text.Json.Nodes;

namespace FireflyMcpGateway.Tests;

/// <summary>The synthetic Firefly III responses in Fixtures/ (see Fixtures/README.md).</summary>
public static class Fixture
{
    public static readonly string[] TransactionLists = ["search_transactions", "list_transaction_by_account"];

    public static readonly string[] Others =
        ["list_account", "get_account", "list_bill", "list_budget", "list_budget_limit_by_budget"];

    public static IEnumerable<string> All => TransactionLists.Concat(Others);

    /// <summary>A tool result's text as firefly-iii-mcp produces it: the Firefly API response indented by two spaces.</summary>
    public static string Text(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json"));

    /// <summary>A JSON-RPC response carrying the fixture as its tool result, serialised compactly.</summary>
    public static byte[] Message(string name, int id = 1) => Encoding.UTF8.GetBytes(Envelope(Text(name), id));

    public static string Envelope(string text, int id = 1) =>
        new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text })
            },
            ["jsonrpc"] = "2.0",
            ["id"] = id
        }.ToJsonString();
}
