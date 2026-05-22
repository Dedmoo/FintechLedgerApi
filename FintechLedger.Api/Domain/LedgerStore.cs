namespace FintechLedger.Api.Domain;

public sealed class LedgerStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly List<JournalEntry> _entries = [];
    private readonly Dictionary<string, string> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEvent> _audit = [];

    public Account CreateAccount(string ownerName, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        lock (_gate)
        {
            var account = new Account
            {
                AccountId = $"ACC-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
                OwnerName = ownerName.Trim(),
                Currency = currency.Trim().ToUpperInvariant()
            };
            _accounts[account.AccountId] = account;
            WriteAudit("account.created", $"accountId={account.AccountId}; owner={account.OwnerName}");
            return account;
        }
    }

    public Account? GetAccount(string accountId)
    {
        lock (_gate)
        {
            return _accounts.TryGetValue(accountId, out var account) ? account : null;
        }
    }

    public decimal GetBalance(string accountId)
    {
        lock (_gate)
        {
            EnsureAccount(accountId);
            return BalanceUnlocked(accountId);
        }
    }

    public TransferResult Transfer(TransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            throw new InvalidOperationException("Transfer amount must be greater than zero.");
        if (string.Equals(request.FromAccountId, request.ToAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Source and destination accounts must differ.");

        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey)
                && _idempotency.TryGetValue(request.IdempotencyKey, out var existingEntryId))
            {
                var existing = _entries.First(e => e.EntryId == existingEntryId);
                return new TransferResult(
                    existing.EntryId,
                    BalanceUnlocked(request.FromAccountId),
                    BalanceUnlocked(request.ToAccountId),
                    Replayed: true);
            }

            var from = EnsureAccount(request.FromAccountId);
            var to = EnsureAccount(request.ToAccountId);
            if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal))
                throw new InvalidOperationException("Currency mismatch between accounts.");

            var fromBalance = BalanceUnlocked(from.AccountId);
            if (!request.AllowNegativeSource && fromBalance < request.Amount)
                throw new InvalidOperationException("Insufficient funds.");

            var entry = new JournalEntry
            {
                EntryId = $"JE-{Guid.NewGuid():N}"[..18].ToUpperInvariant(),
                Description = string.IsNullOrWhiteSpace(request.Description)
                    ? $"Transfer {from.AccountId} -> {to.AccountId}"
                    : request.Description.Trim(),
                PostedAt = DateTimeOffset.UtcNow,
                IdempotencyKey = request.IdempotencyKey,
                Lines =
                [
                    new LedgerLine { AccountId = from.AccountId, Debit = 0m, Credit = request.Amount },
                    new LedgerLine { AccountId = to.AccountId, Debit = request.Amount, Credit = 0m }
                ]
            };

            if (entry.Lines.Sum(l => l.Debit) != entry.Lines.Sum(l => l.Credit))
                throw new InvalidOperationException("Unbalanced journal entry.");

            _entries.Add(entry);
            if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
                _idempotency[request.IdempotencyKey!] = entry.EntryId;

            WriteAudit(
                "transfer.posted",
                $"entryId={entry.EntryId}; from={from.AccountId}; to={to.AccountId}; amount={request.Amount}");

            return new TransferResult(
                entry.EntryId,
                BalanceUnlocked(from.AccountId),
                BalanceUnlocked(to.AccountId),
                Replayed: false);
        }
    }

    public IReadOnlyList<JournalEntry> GetStatement(string accountId)
    {
        lock (_gate)
        {
            EnsureAccount(accountId);
            return _entries
                .Where(e => e.Lines.Any(l => l.AccountId == accountId))
                .OrderByDescending(e => e.PostedAt)
                .ToList();
        }
    }

    public IReadOnlyList<AuditEvent> GetAuditTrail()
    {
        lock (_gate)
        {
            return _audit.OrderByDescending(a => a.OccurredAt).ToList();
        }
    }

    public bool IsLedgerBalanced()
    {
        lock (_gate)
        {
            var debit = _entries.SelectMany(e => e.Lines).Sum(l => l.Debit);
            var credit = _entries.SelectMany(e => e.Lines).Sum(l => l.Credit);
            return debit == credit;
        }
    }

    private decimal BalanceUnlocked(string accountId)
    {
        var debit = _entries.SelectMany(e => e.Lines).Where(l => l.AccountId == accountId).Sum(l => l.Debit);
        var credit = _entries.SelectMany(e => e.Lines).Where(l => l.AccountId == accountId).Sum(l => l.Credit);
        return debit - credit;
    }

    private Account EnsureAccount(string accountId)
    {
        if (!_accounts.TryGetValue(accountId, out var account))
            throw new KeyNotFoundException($"Account not found: {accountId}");
        return account;
    }

    private void WriteAudit(string action, string details)
    {
        _audit.Add(new AuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Action = action,
            Details = details,
            OccurredAt = DateTimeOffset.UtcNow
        });
    }
}
