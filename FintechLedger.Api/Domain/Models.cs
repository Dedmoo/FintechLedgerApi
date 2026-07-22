namespace FintechLedger.Api.Domain;

public sealed class Account
{
    public required string AccountId { get; init; }
    public required string OwnerName { get; init; }
    public required string Currency { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class LedgerLine
{
    public required string AccountId { get; init; }
    public required decimal Debit { get; init; }
    public required decimal Credit { get; init; }
}

public sealed class JournalEntry
{
    public required string EntryId { get; init; }
    public required string Description { get; init; }
    public required DateTimeOffset PostedAt { get; init; }
    public required IReadOnlyList<LedgerLine> Lines { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? ReversesEntryId { get; init; }
}

public sealed class AuditEvent
{
    public required string EventId { get; init; }
    public required string Action { get; init; }
    public required string Details { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required string PreviousHash { get; init; }
    public required string EventHash { get; init; }
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
