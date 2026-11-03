namespace FireflyMcpGateway.Slimming;

/// <summary>
/// Settings for slimming Firefly III tool results, bound from the <c>Slimming</c> section.
/// A list that is left unset takes its default from <see cref="SlimmingDefaults"/>.
/// </summary>
public sealed class SlimmingOptions
{
    /// <summary>Slim tool results on their way to the client. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>A response or event larger than this is passed through untouched.</summary>
    public int MaxMessageBytes { get; set; } = 8 * 1024 * 1024;

    /// <summary>
    /// The Firefly object types (the <c>type</c> of the items in <c>data</c>) that are slimmed.
    /// A result of any other shape is passed through byte for byte.
    /// </summary>
    public string[]? Types { get; set; }

    /// <summary>Fields removed wherever they occur in a slimmed result.</summary>
    public string[]? DropFields { get; set; }

    /// <summary>Fields whose name ends with one of these are removed wherever they occur.</summary>
    public string[]? DropFieldSuffixes { get; set; }

    /// <summary>The fields kept on each split of a transaction; every other split field is removed.</summary>
    public string[]? KeepSplitFields { get; set; }

    /// <summary><see cref="TransactionLayout.Rows"/> returns transactions as one table of columns and rows.</summary>
    public TransactionLayout TransactionLayout { get; set; } = TransactionLayout.Objects;
}

public enum TransactionLayout
{
    /// <summary>The Firefly API shape, with fewer fields.</summary>
    Objects,

    /// <summary>One row per split under a single list of column names, so field names are not repeated.</summary>
    Rows
}

public static class SlimmingDefaults
{
    public static readonly string[] Types =
        ["transactions", "accounts", "bills", "budgets", "budget_limits", "categories", "tags", "rules"];

    /// <summary>Bookkeeping and display fields that no follow-up call or decision needs.</summary>
    public static readonly string[] DropFields =
    [
        "links", "created_at", "updated_at", "user", "user_group", "object_has_currency_setting",
        "import_hash_v2", "currency_name", "foreign_currency_name", "primary_currency_name"
    ];

    public static readonly string[] DropFieldSuffixes = ["_decimal_places", "_symbol"];

    /// <summary>What a split keeps: everything needed to read it, reason about it and write it back.</summary>
    public static readonly string[] KeepSplitFields =
    [
        "transaction_journal_id", "type", "date", "amount", "currency_code", "foreign_amount", "foreign_currency_code",
        "description", "source_id", "source_name", "source_type", "destination_id", "destination_name",
        "destination_type", "category_id", "category_name", "budget_id", "budget_name", "bill_id", "bill_name",
        "tags", "notes", "external_id", "source_balance_after", "destination_balance_after"
    ];
}
