namespace FintechLedger.Api.Domain;

public sealed class Account
{
    public string AccountId { get; set; } = "";
    public string OwnerName { get; set; } = "";
    public string Currency { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class LedgerLine
{
    public long Id { get; set; }
    public string JournalEntryId { get; set; } = "";
    public string AccountId { get; set; } = "";
    public decimal Debit { get; set; }
    public decimal Credit { get; set; }
}

public sealed class JournalEntry
{
    public string EntryId { get; set; } = "";
    public string Description { get; set; } = "";
    public DateTimeOffset PostedAt { get; set; }
    public List<LedgerLine> Lines { get; set; } = [];
    public string? IdempotencyKey { get; set; }
    public string? ReversesEntryId { get; set; }
}

public sealed class AuditEvent
{
    public string EventId { get; set; } = "";
    public string Action { get; set; } = "";
    public string Details { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public string PreviousHash { get; set; } = "";
    public string EventHash { get; set; } = "";
}

public sealed class IdempotencyRecord
{
    public string MapKey { get; set; } = "";
    public string EntryId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
}

public sealed class AuditChainState
{
    public int Id { get; set; } = 1;
    public string LastHash { get; set; } = "GENESIS";
}

public sealed class StatementLine
{
    public required string EntryId { get; init; }
    public required DateTimeOffset PostedAt { get; init; }
    public required string Description { get; init; }
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
    public required decimal RunningBalance { get; init; }
    public string? ReversesEntryId { get; init; }
}

public sealed record CreateAccountRequest(string OwnerName, string Currency = "TRY");

public sealed record FundAccountRequest(
    string AccountId,
    decimal Amount,
    string? Description = null,
    string? IdempotencyKey = null);

public sealed record TransferRequest(
    string FromAccountId,
    string ToAccountId,
    decimal Amount,
    string? Description = null,
    string? IdempotencyKey = null);

public sealed record ReverseEntryRequest(string EntryId, string? Reason = null, string? IdempotencyKey = null);

public sealed record TransferResult(
    string EntryId,
    decimal FromBalance,
    decimal ToBalance,
    bool Replayed);

/// <summary>
/// ISO 4217 alphabetic codes commonly used in retail banking demos.
/// Unknown codes are rejected so the API does not silently accept "FOO".
/// </summary>
public static class Iso4217
{
    private static readonly HashSet<string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        "AED", "AUD", "BHD", "BRL", "CAD", "CHF", "CNY", "CZK", "DKK", "EUR",
        "GBP", "HKD", "HUF", "IDR", "ILS", "INR", "JPY", "KRW", "KWD", "MXN",
        "MYR", "NOK", "NZD", "OMR", "PHP", "PLN", "QAR", "RON", "RUB", "SAR",
        "SEK", "SGD", "THB", "TRY", "USD", "ZAR"
    };

    public static string NormalizeOrThrow(string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        var code = currency.Trim().ToUpperInvariant();
        if (code.Length != 3 || !Codes.Contains(code))
            throw new InvalidOperationException($"Unsupported or invalid ISO 4217 currency: {currency}");
        return code;
    }
}
