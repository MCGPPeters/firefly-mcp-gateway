using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FireflyMcpGateway.Slimming;

namespace FireflyMcpGateway.Tests;

public sealed class SlimmingTests
{
    private static readonly ResultSlimmer Slimmer = new(new SlimmingOptions());

    // The split fields that must survive (cycle prompt, constraints): ids, names, type, date, amount,
    // currency, foreign amount and currency, description, source and destination, category, budget,
    // bill, tags, notes, external_id and the balance-after fields.
    private static readonly string[] RequiredSplitFields =
    [
        "transaction_journal_id", "type", "date", "amount", "currency_code", "foreign_amount", "foreign_currency_code",
        "description", "source_id", "source_name", "destination_id", "destination_name", "category_id",
        "category_name", "budget_id", "budget_name", "bill_id", "bill_name", "tags", "notes", "external_id",
        "source_balance_after", "destination_balance_after"
    ];

    [Theory]
    [InlineData("search_transactions", 80)]
    [InlineData("list_transaction_by_account", 80)]
    [InlineData("list_account", 50)]
    [InlineData("get_account", 50)]
    [InlineData("list_bill", 50)]
    [InlineData("list_budget", 50)]
    [InlineData("list_budget_limit_by_budget", 50)]
    public void Reduces_the_size_of_each_fixture(string fixture, int minimumPercent)
    {
        var text = Fixture.Text(fixture);

        var slimmed = Slimmer.SlimToolText(text);

        Assert.NotNull(slimmed);
        var reduction = Reduction(text, slimmed);
        Assert.True(reduction >= minimumPercent, $"{fixture}: {reduction:F1}% is less than {minimumPercent}%");
    }

    [Theory]
    [InlineData("search_transactions")]
    [InlineData("list_transaction_by_account")]
    public void Keeps_every_required_split_field_with_its_value(string fixture)
    {
        var text = Fixture.Text(fixture);
        var before = Splits(JsonNode.Parse(text)!);
        var after = Splits(JsonNode.Parse(Slimmer.SlimToolText(text)!)!)
            .ToDictionary(split => split["transaction_journal_id"]!.GetValue<string>());

        Assert.NotEmpty(before);

        foreach (var split in before)
        {
            var id = split["transaction_journal_id"]!.GetValue<string>();
            var slim = after[id];

            foreach (var field in RequiredSplitFields)
            {
                var value = split[field];

                if (IsEmpty(value))
                {
                    continue;
                }

                Assert.True(slim.ContainsKey(field), $"{fixture}: split {id} lost {field}");
                Assert.True(SameValue(value, slim[field]), $"{fixture}: split {id} changed {field}");
            }
        }
    }

    [Theory]
    [InlineData("list_account")]
    [InlineData("get_account")]
    [InlineData("list_bill")]
    [InlineData("list_budget")]
    [InlineData("list_budget_limit_by_budget")]
    public void Keeps_every_field_that_is_not_empty_noise_or_a_duplicate(string fixture)
    {
        var text = Fixture.Text(fixture);
        var before = Items(JsonNode.Parse(text)!["data"]).ToList();
        var after = Items(JsonNode.Parse(Slimmer.SlimToolText(text)!)!["data"])
            .ToDictionary(item => item["id"]!.GetValue<string>());

        Assert.NotEmpty(before);

        foreach (var item in before)
        {
            var id = item["id"]!.GetValue<string>();
            var slim = after[id];
            Assert.Equal(item["type"]!.GetValue<string>(), slim["type"]!.GetValue<string>());

            var attributes = item["attributes"]!.AsObject();
            var slimAttributes = slim["attributes"]!.AsObject();

            foreach (var (key, value) in attributes)
            {
                if (IsNoise(key) || IsEmpty(value) || value is JsonObject || IsDuplicate(attributes, key, value))
                {
                    continue;
                }

                Assert.True(slimAttributes.ContainsKey(key), $"{fixture}: {id} lost {key}");

                if (value is JsonValue)
                {
                    Assert.True(SameValue(value, slimAttributes[key]), $"{fixture}: {id} changed {key}");
                }
            }
        }
    }

    [Fact]
    public void Keeps_the_pagination()
    {
        var text = Fixture.Text("search_transactions");

        var before = JsonNode.Parse(text)!["meta"];
        var after = JsonNode.Parse(Slimmer.SlimToolText(text)!)!["meta"];

        Assert.True(JsonNode.DeepEquals(before, after));
    }

    [Theory]
    [InlineData("""
        {
          "message": "Error executing tool 'get_account': 404 Not Found",
          "error": "{\"message\":\"Resource not found\"}"
        }
        """)]
    [InlineData("Error: Unknown tool requested: frobnicate")]
    [InlineData("""
        {
          "data": [],
          "meta": {}
        }
        """)]
    [InlineData("""
        {
          "data": [
            {
              "type": "piggy_banks",
              "id": "1",
              "attributes": {
                "name": "x",
                "notes": null
              }
            }
          ]
        }
        """)]
    [InlineData("{ this is not json")]
    public void Leaves_results_it_does_not_know_alone(string text)
    {
        Assert.Null(Slimmer.SlimToolText(text));
    }

    [Fact]
    public void Leaves_messages_without_a_known_tool_result_alone()
    {
        Assert.Null(Slimmer.SlimMessage(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Unknown tool"}}""")));
        Assert.Null(Slimmer.SlimMessage(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","id":1,"result":{"tools":[]}}""")));
        Assert.Null(Slimmer.SlimMessage(Encoding.UTF8.GetBytes("""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progress":1}}""")));
        Assert.Null(Slimmer.SlimMessage(Encoding.UTF8.GetBytes(Fixture.Envelope("Error: Unknown tool requested: frobnicate"))));
        Assert.Null(Slimmer.SlimMessage("not json"u8));
    }

    [Fact]
    public void Slims_the_tool_result_inside_a_message_and_keeps_the_envelope()
    {
        var slimmed = Slimmer.SlimMessage(Fixture.Message("search_transactions", id: 7));

        Assert.NotNull(slimmed);
        var message = JsonNode.Parse(slimmed)!;
        Assert.Equal(7, message["id"]!.GetValue<int>());
        Assert.Equal("2.0", message["jsonrpc"]!.GetValue<string>());
        Assert.Equal(
            Slimmer.SlimToolText(Fixture.Text("search_transactions")),
            message["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Slims_every_message_in_a_batch()
    {
        var batch = $"[{Fixture.Envelope(Fixture.Text("get_account"), 1)},{Fixture.Envelope("plain text", 2)}]";

        var slimmed = Slimmer.SlimMessage(Encoding.UTF8.GetBytes(batch));

        Assert.NotNull(slimmed);
        var messages = JsonNode.Parse(slimmed)!.AsArray();
        Assert.Equal(Slimmer.SlimToolText(Fixture.Text("get_account")), messages[0]!["result"]!["content"]![0]!["text"]!.GetValue<string>());
        Assert.Equal("plain text", messages[1]!["result"]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Trims_trailing_zeros_without_changing_a_value()
    {
        var split = SlimSplit("""
            {
              "transaction_journal_id": "1",
              "currency_decimal_places": 2,
              "amount": "12.340000000000",
              "foreign_amount": "1200.000000000000",
              "foreign_currency_code": "JPY",
              "foreign_currency_decimal_places": 0,
              "source_balance_after": "-0.500000000000",
              "destination_balance_after": "7.125000000000",
              "description": "1.500",
              "notes": "3.000000"
            }
            """);

        Assert.Equal("12.34", split["amount"]!.GetValue<string>());
        Assert.Equal("1200", split["foreign_amount"]!.GetValue<string>());
        Assert.Equal("-0.50", split["source_balance_after"]!.GetValue<string>());
        Assert.Equal("7.125", split["destination_balance_after"]!.GetValue<string>());
        Assert.Equal("1.500", split["description"]!.GetValue<string>());
        Assert.Equal("3.000000", split["notes"]!.GetValue<string>());
    }

    [Fact]
    public void Removes_nulls_and_empty_arrays_but_keeps_empty_strings_and_false()
    {
        var attributes = SlimAttributes("accounts", """{"name":"A","notes":null,"tags":[],"iban":"","active":false}""");

        Assert.False(attributes.ContainsKey("notes"));
        Assert.False(attributes.ContainsKey("tags"));
        Assert.Equal("", attributes["iban"]!.GetValue<string>());
        Assert.False(attributes["active"]!.GetValue<bool>());
    }

    [Fact]
    public void Drops_primary_currency_fields_only_when_they_repeat_the_base_field()
    {
        var attributes = SlimAttributes("accounts", """
            {
              "name": "A",
              "currency_code": "EUR",
              "primary_currency_code": "EUR",
              "current_balance": "10.000000000000",
              "pc_current_balance": "10.000000000000",
              "opening_balance": "5.000000000000",
              "pc_opening_balance": "4.500000000000"
            }
            """);

        Assert.False(attributes.ContainsKey("primary_currency_code"));
        Assert.False(attributes.ContainsKey("pc_current_balance"));
        Assert.Equal("4.50", attributes["pc_opening_balance"]!.GetValue<string>());
    }

    [Fact]
    public void Honours_configured_lists()
    {
        var keepEverything = new ResultSlimmer(new SlimmingOptions { DropFields = ["nothing"], DropFieldSuffixes = ["_nothing"] });

        var attributes = SlimAttributes("accounts", """{"name":"A","created_at":"2026-11-03T10:00:00+01:00","currency_symbol":"€"}""", keepEverything);

        Assert.True(attributes.ContainsKey("created_at"));
        Assert.Equal("€", attributes["currency_symbol"]!.GetValue<string>());
        Assert.Null(new ResultSlimmer(new SlimmingOptions { Types = ["bills"] }).SlimToolText(Fixture.Text("get_account")));
    }

    [Fact]
    public void Can_return_transactions_as_rows()
    {
        var rows = new ResultSlimmer(new SlimmingOptions { TransactionLayout = TransactionLayout.Rows });
        var text = Fixture.Text("search_transactions");

        var slimmed = rows.SlimToolText(text);

        Assert.NotNull(slimmed);
        var table = JsonNode.Parse(slimmed)!;
        Assert.Equal("rows", table["layout"]!.GetValue<string>());
        var columns = table["columns"]!.AsArray().Select(column => column!.GetValue<string>()).ToList();
        Assert.Equal("group_id", columns[0]);
        Assert.Contains("transaction_journal_id", columns);
        Assert.Equal(Splits(JsonNode.Parse(text)!).Count, table["rows"]!.AsArray().Count);
        Assert.All(table["rows"]!.AsArray(), row => Assert.Equal(columns.Count, row!.AsArray().Count));
        Assert.True(slimmed.Length < Slimmer.SlimToolText(text)!.Length);
    }

    [Fact]
    public void Adds_less_than_20_ms_per_call_at_the_95th_percentile()
    {
        var message = Fixture.Message("search_transactions");

        for (var i = 0; i < 20; i++)
        {
            Slimmer.SlimMessage(message);
        }

        var timings = new List<double>();

        for (var i = 0; i < 200; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            Slimmer.SlimMessage(message);
            timings.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        timings.Sort();
        var p95 = timings[(int)Math.Ceiling(0.95 * timings.Count) - 1];
        Assert.True(p95 < 20, $"p95 was {p95:F2} ms");
    }

    /// <summary>Writes the before/after table to the file named in SLIMMING_REPORT, for the CI summary.</summary>
    [Fact]
    public void Reports_the_sizes()
    {
        var rows = new ResultSlimmer(new SlimmingOptions { TransactionLayout = TransactionLayout.Rows });
        var report = new StringBuilder()
            .AppendLine("| Fixture | Before (chars) | After (chars) | Reduction | With rows layout |")
            .AppendLine("|---|---:|---:|---:|---:|");

        foreach (var name in Fixture.All)
        {
            var text = Fixture.Text(name);
            var slimmed = Slimmer.SlimToolText(text)!;
            var withRows = Fixture.TransactionLists.Contains(name)
                ? Reduction(text, rows.SlimToolText(text)!).ToString("F1", CultureInfo.InvariantCulture) + "%"
                : "-";

            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {name} | {text.Length} | {slimmed.Length} | {Reduction(text, slimmed):F1}% | {withRows} |");
        }

        var path = Environment.GetEnvironmentVariable("SLIMMING_REPORT");

        if (!string.IsNullOrEmpty(path))
        {
            File.WriteAllText(path, report.ToString());
        }

        Assert.Contains("search_transactions", report.ToString());
    }

    private static double Reduction(string before, string after) => 100.0 * (before.Length - after.Length) / before.Length;

    private static JsonObject SlimSplit(string split, ResultSlimmer? slimmer = null)
    {
        var text = "{\"data\":[{\"type\":\"transactions\",\"id\":\"1\",\"attributes\":{\"transactions\":[" + split + "]}}]}";
        return Splits(JsonNode.Parse((slimmer ?? Slimmer).SlimToolText(text)!)!).Single();
    }

    private static JsonObject SlimAttributes(string type, string attributes, ResultSlimmer? slimmer = null)
    {
        var text = "{\"data\":{\"type\":\"" + type + "\",\"id\":\"1\",\"attributes\":" + attributes + "}}";
        return JsonNode.Parse((slimmer ?? Slimmer).SlimToolText(text)!)!["data"]!["attributes"]!.AsObject();
    }

    private static List<JsonObject> Splits(JsonNode root) =>
        Items(root["data"])
            .SelectMany(group => group["attributes"]?["transactions"] is JsonArray splits
                ? splits.OfType<JsonObject>()
                : Enumerable.Empty<JsonObject>())
            .ToList();

    private static IEnumerable<JsonObject> Items(JsonNode? data) => data switch
    {
        JsonObject single => new[] { single },
        JsonArray list => list.OfType<JsonObject>(),
        _ => Enumerable.Empty<JsonObject>()
    };

    private static bool IsEmpty(JsonNode? value) =>
        value is null || value is JsonArray { Count: 0 } || value is JsonObject { Count: 0 };

    private static bool IsNoise(string key) =>
        SlimmingDefaults.DropFields.Contains(key)
        || SlimmingDefaults.DropFieldSuffixes.Any(suffix => key.EndsWith(suffix, StringComparison.Ordinal));

    private static bool IsDuplicate(JsonObject obj, string key, JsonNode? value)
    {
        var baseKey =
            key.StartsWith("pc_", StringComparison.Ordinal) ? key[3..] :
            key.StartsWith("native_", StringComparison.Ordinal) ? key[7..] :
            key.StartsWith("primary_currency_", StringComparison.Ordinal) ? "currency_" + key[17..] :
            null;

        return baseKey is not null && obj.TryGetPropertyValue(baseKey, out var baseValue) && SameValue(value, baseValue);
    }

    /// <summary>Equal JSON, where two amount strings count as equal when they are the same number.</summary>
    private static bool SameValue(JsonNode? before, JsonNode? after)
    {
        if (before is JsonValue a && after is JsonValue b
            && a.TryGetValue<string>(out var left) && b.TryGetValue<string>(out var right)
            && decimal.TryParse(left, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var x)
            && decimal.TryParse(right, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var y))
        {
            return x == y;
        }

        return JsonNode.DeepEquals(before, after);
    }
}
