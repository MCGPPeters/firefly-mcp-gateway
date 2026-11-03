using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FireflyMcpGateway.Slimming;

/// <summary>
/// Removes what a client does not need from Firefly III tool results: indentation, nulls, empty arrays,
/// primary-currency fields that repeat the base field, bookkeeping fields and trailing zeros in amounts.
/// Transaction splits keep a fixed set of fields. Results it does not recognise are left alone.
/// </summary>
/// <remarks>
/// firefly-iii-mcp returns every Firefly API response as one text block holding the JSON indented by two
/// spaces, and never marks errors with <c>isError</c>. A result is recognised by its shape instead: a
/// <c>data</c> object, or a non-empty <c>data</c> array, whose items all carry one of the configured
/// <c>type</c>s. Error texts, empty lists and every other shape do not match and pass through unchanged.
/// </remarks>
public sealed class ResultSlimmer
{
    private static readonly JsonSerializerOptions Compact = new()
    {
        // Leave non-ASCII text (€, accents) as it is instead of escaping it, which would make it longer.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    // Free text that happens to look like a number keeps its exact spelling.
    private static readonly HashSet<string> TextFields = new(StringComparer.Ordinal)
    {
        "description", "notes", "name", "title", "group_title", "tag", "value", "external_id",
        "internal_reference", "external_url", "original_source", "iban", "bic", "account_number"
    };

    private readonly HashSet<string> _types;
    private readonly HashSet<string> _dropFields;
    private readonly string[] _dropSuffixes;
    private readonly HashSet<string> _keepSplitFields;
    private readonly TransactionLayout _layout;

    public ResultSlimmer(SlimmingOptions options)
    {
        Enabled = options.Enabled;
        MaxMessageBytes = options.MaxMessageBytes > 0 ? options.MaxMessageBytes : new SlimmingOptions().MaxMessageBytes;
        _types = new HashSet<string>(OrDefault(options.Types, SlimmingDefaults.Types), StringComparer.Ordinal);
        _dropFields = new HashSet<string>(OrDefault(options.DropFields, SlimmingDefaults.DropFields), StringComparer.Ordinal);
        _dropSuffixes = OrDefault(options.DropFieldSuffixes, SlimmingDefaults.DropFieldSuffixes);
        _keepSplitFields = new HashSet<string>(
            OrDefault(options.KeepSplitFields, SlimmingDefaults.KeepSplitFields), StringComparer.Ordinal);
        _layout = options.TransactionLayout;
    }

    public bool Enabled { get; }

    public int MaxMessageBytes { get; }

    public string Describe() =>
        Enabled
            ? $"on for {string.Join(", ", _types)}; transactions as {_layout.ToString().ToLowerInvariant()}"
            : "off";

    /// <summary>
    /// Slims the tool results in a JSON-RPC message or batch. Returns null when nothing changed, so the
    /// caller can forward the original bytes.
    /// </summary>
    public byte[]? SlimMessage(ReadOnlySpan<byte> utf8Json)
    {
        JsonNode? root;

        try
        {
            root = JsonNode.Parse(utf8Json);
        }
        catch (JsonException)
        {
            return null;
        }

        var changed = false;

        if (root is JsonObject single)
        {
            changed = SlimResult(single);
        }
        else if (root is JsonArray batch)
        {
            foreach (var message in batch.OfType<JsonObject>())
            {
                changed |= SlimResult(message);
            }
        }

        return changed ? Encoding.UTF8.GetBytes(root!.ToJsonString(Compact)) : null;
    }

    /// <summary>Slims the text of one tool result. Returns null when it is not a Firefly result this slimmer knows.</summary>
    public string? SlimToolText(string text)
    {
        var trimmed = text.AsSpan().TrimStart();

        if (trimmed.IsEmpty || trimmed[0] != '{')
        {
            return null;
        }

        JsonObject? root;

        try
        {
            root = JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }

        if (root is null || ObjectType(root["data"]) is not { } type || !_types.Contains(type))
        {
            return null;
        }

        var transactions = type == "transactions";

        if (transactions)
        {
            ProjectSplits(root["data"]);
        }

        Clean(root);

        if (transactions && _layout == TransactionLayout.Rows)
        {
            root = ToRows(root);
        }

        return root.ToJsonString(Compact);
    }

    private bool SlimResult(JsonObject message)
    {
        if (message["result"] is not JsonObject result || result["content"] is not JsonArray content)
        {
            return false;
        }

        var changed = false;

        foreach (var block in content.OfType<JsonObject>())
        {
            if (AsString(block["type"]) != "text" || AsString(block["text"]) is not { } text)
            {
                continue;
            }

            if (SlimToolText(text) is { } slimmed)
            {
                block["text"] = slimmed;
                changed = true;
            }
        }

        return changed;
    }

    private static string? ObjectType(JsonNode? data)
    {
        if (data is JsonObject single)
        {
            return AsString(single["type"]);
        }

        if (data is not JsonArray { Count: > 0 } list)
        {
            return null;
        }

        string? type = null;

        foreach (var item in list)
        {
            var itemType = item is JsonObject obj ? AsString(obj["type"]) : null;

            if (itemType is null || (type is not null && itemType != type))
            {
                return null;
            }

            type = itemType;
        }

        return type;
    }

    private void ProjectSplits(JsonNode? data)
    {
        foreach (var group in Items(data))
        {
            if (group["attributes"] is not JsonObject attributes || attributes["transactions"] is not JsonArray splits)
            {
                continue;
            }

            foreach (var split in splits.OfType<JsonObject>())
            {
                // First, while the decimal-places fields the projection removes are still there.
                NormalizeDecimals(split);

                foreach (var key in split.Select(property => property.Key).ToList())
                {
                    if (!_keepSplitFields.Contains(key))
                    {
                        split.Remove(key);
                    }
                }
            }
        }
    }

    private void Clean(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            CleanObject(obj);
        }
        else if (node is JsonArray array)
        {
            // Elements are cleaned but never removed: their position can mean something.
            foreach (var item in array)
            {
                Clean(item);
            }
        }
    }

    private void CleanObject(JsonObject obj)
    {
        NormalizeDecimals(obj);

        foreach (var key in obj.Select(property => property.Key).ToList())
        {
            var value = obj[key];

            if (IsDropped(key) || IsDuplicate(obj, key, value))
            {
                obj.Remove(key);
                continue;
            }

            Clean(value);

            if (IsEmpty(value))
            {
                obj.Remove(key);
            }
        }
    }

    private bool IsDropped(string key)
    {
        if (_dropFields.Contains(key))
        {
            return true;
        }

        foreach (var suffix in _dropSuffixes)
        {
            if (key.EndsWith(suffix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A primary-currency field adds nothing when it equals the field it converts.</summary>
    private static bool IsDuplicate(JsonObject obj, string key, JsonNode? value)
    {
        var baseKey =
            key.StartsWith("pc_", StringComparison.Ordinal) ? key["pc_".Length..] :
            key.StartsWith("native_", StringComparison.Ordinal) ? key["native_".Length..] :
            key.StartsWith("primary_currency_", StringComparison.Ordinal) ? "currency_" + key["primary_currency_".Length..] :
            null;

        return baseKey is not null
               && obj.TryGetPropertyValue(baseKey, out var baseValue)
               && JsonNode.DeepEquals(value, baseValue);
    }

    private static bool IsEmpty(JsonNode? value) => value switch
    {
        null => true,
        JsonArray array => array.Count == 0,
        JsonObject obj => obj.Count == 0,
        _ => false
    };

    /// <summary>
    /// Firefly writes amounts with twelve decimals ("12.340000000000"). Trailing zeros beyond the currency's
    /// decimal places go; the value itself never changes.
    /// </summary>
    private static void NormalizeDecimals(JsonObject obj)
    {
        var places = AsInt(obj["currency_decimal_places"]) ?? 2;
        var foreignPlaces = AsInt(obj["foreign_currency_decimal_places"]) ?? places;
        var primaryPlaces = AsInt(obj["primary_currency_decimal_places"]) ?? places;

        foreach (var key in obj.Select(property => property.Key).ToList())
        {
            if (TextFields.Contains(key) || key.StartsWith("sepa_", StringComparison.Ordinal))
            {
                continue;
            }

            if (AsString(obj[key]) is not { } text || !IsDecimal(text))
            {
                continue;
            }

            var keyPlaces =
                key.StartsWith("foreign_", StringComparison.Ordinal) ? foreignPlaces :
                key.StartsWith("pc_", StringComparison.Ordinal) || key.StartsWith("native_", StringComparison.Ordinal) ? primaryPlaces :
                places;

            var trimmed = TrimDecimals(text, keyPlaces);

            if (trimmed.Length != text.Length)
            {
                obj[key] = trimmed;
            }
        }
    }

    private static bool IsDecimal(string text)
    {
        var start = text.StartsWith('-') ? 1 : 0;
        var dot = text.IndexOf('.');

        if (dot <= start || dot == text.Length - 1)
        {
            return false;
        }

        for (var i = start; i < text.Length; i++)
        {
            if (i != dot && !char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string TrimDecimals(string text, int places)
    {
        var dot = text.IndexOf('.');
        var keep = dot + 1 + Math.Max(0, places);
        var end = text.Length;

        while (end > keep && text[end - 1] == '0')
        {
            end--;
        }

        if (end == dot + 1)
        {
            end = dot;
        }

        return end == text.Length ? text : text[..end];
    }

    private static JsonObject ToRows(JsonObject root)
    {
        var groups = Items(root["data"]).ToList();
        var hasTitle = groups.Any(group => GroupTitle(group) is not null);
        var columns = new List<string>();

        foreach (var split in groups.SelectMany(Splits))
        {
            foreach (var property in split)
            {
                if (!columns.Contains(property.Key))
                {
                    columns.Add(property.Key);
                }
            }
        }

        var header = new JsonArray { "group_id" };

        if (hasTitle)
        {
            header.Add("group_title");
        }

        foreach (var column in columns)
        {
            header.Add(column);
        }

        var rows = new JsonArray();

        foreach (var group in groups)
        {
            var title = GroupTitle(group);

            foreach (var split in Splits(group))
            {
                var row = new JsonArray { group["id"]?.DeepClone() };

                if (hasTitle)
                {
                    row.Add(title?.DeepClone());
                }

                foreach (var column in columns)
                {
                    row.Add(split[column]?.DeepClone());
                }

                rows.Add(row);
            }
        }

        var table = new JsonObject
        {
            ["layout"] = "rows",
            ["type"] = "transactions",
            ["columns"] = header,
            ["rows"] = rows
        };

        if (root["meta"] is { } meta)
        {
            table["meta"] = meta.DeepClone();
        }

        return table;
    }

    private static IEnumerable<JsonObject> Splits(JsonObject group) =>
        group["attributes"] is JsonObject attributes && attributes["transactions"] is JsonArray splits
            ? splits.OfType<JsonObject>()
            : Enumerable.Empty<JsonObject>();

    private static JsonNode? GroupTitle(JsonObject group) =>
        group["attributes"] is JsonObject attributes ? attributes["group_title"] : null;

    private static IEnumerable<JsonObject> Items(JsonNode? data) => data switch
    {
        JsonObject single => new[] { single },
        JsonArray list => list.OfType<JsonObject>(),
        _ => Enumerable.Empty<JsonObject>()
    };

    private static string? AsString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? AsInt(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    private static string[] OrDefault(string[]? configured, string[] defaults) =>
        configured is { Length: > 0 } ? configured : defaults;
}
