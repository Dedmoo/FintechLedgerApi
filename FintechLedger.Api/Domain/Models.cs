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
}

public sealed class AuditEvent
{
    public required string EventId { get; init; }
    public required string Action { get; init; }
    public required string Details { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
}

public sealed record CreateAccountRequest(string OwnerName, string Currency = "TRY");

public sealed record TransferRequest(
    string FromAccountId,
    string ToAccountId,
    decimal Amount,
    string? Description = null,
    string? IdempotencyKey = null,
    bool AllowNegativeSource = false);

public sealed record TransferResult(
    string EntryId,
    decimal FromBalance,
    decimal ToBalance,
    bool Replayed);
