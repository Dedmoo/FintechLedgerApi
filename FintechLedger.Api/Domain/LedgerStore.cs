using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FintechLedger.Api.Domain;

public sealed class LedgerStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly List<JournalEntry> _entries = [];
    private readonly Dictionary<string, IdempotencyRecord> _idempotency = new(StringComparer.Ordinal);
    private readonly List<AuditEvent> _audit = [];
    private string _lastAuditHash = "GENESIS";

    private sealed record IdempotencyRecord(string EntryId, string Fingerprint);

    public Account CreateAccount(string ownerName, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        var trimmed = ownerName.Trim();
        if (trimmed.Length > 120)
            throw new InvalidOperationException("Owner name must be at most 120 characters.");
        var ccy = Iso4217.NormalizeOrThrow(currency);

        lock (_gate)
        {
            var account = new Account
            {
                AccountId = $"ACC-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
                OwnerName = trimmed,
                Currency = ccy
            };
            _accounts[account.AccountId] = account;
            WriteAudit("account.created", $"accountId={account.AccountId}; owner={account.OwnerName}; ccy={ccy}");
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

    public TransferResult Fund(FundAccountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            throw new InvalidOperationException("Funding amount must be greater than zero.");
        ValidateMoneyScale(request.Amount);

        lock (_gate)
        {
            var account = EnsureAccount(request.AccountId);
            var fingerprint = Fingerprint("fund", account.AccountId, request.Amount.ToString(CultureInfo.InvariantCulture));
            if (TryReplay("fund:", request.IdempotencyKey, fingerprint, out var replayedId))
            {
                return new TransferResult(
                    replayedId!,
                    BalanceUnlocked(SystemClearingId(account.Currency)),
                    BalanceUnlocked(account.AccountId),
                    Replayed: true);
            }

            var clearingId = EnsureSystemClearing(account.Currency);
            var entry = PostBalanced(
                description: string.IsNullOrWhiteSpace(request.Description)
                    ? $"Opening deposit {account.AccountId}"
                    : request.Description.Trim(),
                idempotencyKey: request.IdempotencyKey,
                reversesEntryId: null,
                new LedgerLine { AccountId = clearingId, Debit = 0m, Credit = request.Amount },
                new LedgerLine { AccountId = account.AccountId, Debit = request.Amount, Credit = 0m });

            RememberIdempotency("fund:", request.IdempotencyKey, fingerprint, entry.EntryId);
            WriteAudit(
                "account.funded",
                $"entryId={entry.EntryId}; accountId={account.AccountId}; amount={request.Amount}");

            return new TransferResult(
                entry.EntryId,
                BalanceUnlocked(clearingId),
                BalanceUnlocked(account.AccountId),
                Replayed: false);
        }
    }

    public TransferResult Transfer(TransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            throw new InvalidOperationException("Transfer amount must be greater than zero.");
        ValidateMoneyScale(request.Amount);
        if (string.Equals(request.FromAccountId, request.ToAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Source and destination accounts must differ.");

        lock (_gate)
        {
            var fingerprint = Fingerprint(
                "xfer",
                request.FromAccountId,
                request.ToAccountId,
                request.Amount.ToString(CultureInfo.InvariantCulture));
            if (TryReplay("xfer:", request.IdempotencyKey, fingerprint, out var replayedId))
            {
                return new TransferResult(
                    replayedId!,
                    BalanceUnlocked(request.FromAccountId),
                    BalanceUnlocked(request.ToAccountId),
                    Replayed: true);
            }

            var from = EnsureAccount(request.FromAccountId);
            var to = EnsureAccount(request.ToAccountId);
            if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal))
                throw new InvalidOperationException("Currency mismatch between accounts.");

            var fromBalance = BalanceUnlocked(from.AccountId);
            if (fromBalance < request.Amount)
                throw new InvalidOperationException("Insufficient funds.");

            var entry = PostBalanced(
                description: string.IsNullOrWhiteSpace(request.Description)
                    ? $"Transfer {from.AccountId} -> {to.AccountId}"
                    : request.Description.Trim(),
                idempotencyKey: request.IdempotencyKey,
                reversesEntryId: null,
                new LedgerLine { AccountId = from.AccountId, Debit = 0m, Credit = request.Amount },
                new LedgerLine { AccountId = to.AccountId, Debit = request.Amount, Credit = 0m });

            RememberIdempotency("xfer:", request.IdempotencyKey, fingerprint, entry.EntryId);
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

    /// <summary>
    /// Append-only correction: posts a reversing journal that mirrors the original lines.
    /// Posted history is never mutated.
    /// </summary>
    public TransferResult Reverse(ReverseEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntryId);

        lock (_gate)
        {
            var fingerprint = Fingerprint("rev", request.EntryId);
            if (TryReplay("rev:", request.IdempotencyKey, fingerprint, out var replayedId))
            {
                var original = _entries.First(e => e.EntryId == request.EntryId);
                var a = original.Lines[0].AccountId;
                var b = original.Lines[1].AccountId;
                return new TransferResult(replayedId!, BalanceUnlocked(a), BalanceUnlocked(b), Replayed: true);
            }

            var source = _entries.FirstOrDefault(e => e.EntryId == request.EntryId)
                ?? throw new KeyNotFoundException($"Journal entry not found: {request.EntryId}");
            if (source.ReversesEntryId is not null)
                throw new InvalidOperationException("Cannot reverse a reversing entry directly.");
            if (_entries.Any(e => e.ReversesEntryId == source.EntryId))
                throw new InvalidOperationException("Entry already reversed.");

            var mirrored = source.Lines
                .Select(l => new LedgerLine
                {
                    AccountId = l.AccountId,
                    Debit = l.Credit,
                    Credit = l.Debit
                })
                .ToArray();

            var reason = string.IsNullOrWhiteSpace(request.Reason)
                ? $"Reversal of {source.EntryId}"
                : request.Reason.Trim();

            var entry = PostBalanced(reason, request.IdempotencyKey, source.EntryId, mirrored);
            RememberIdempotency("rev:", request.IdempotencyKey, fingerprint, entry.EntryId);
            WriteAudit("transfer.reversed", $"entryId={entry.EntryId}; reverses={source.EntryId}");

            return new TransferResult(
                entry.EntryId,
                BalanceUnlocked(mirrored[0].AccountId),
                BalanceUnlocked(mirrored[1].AccountId),
                Replayed: false);
        }
    }

    public IReadOnlyList<StatementLine> GetStatement(string accountId)
    {
        lock (_gate)
        {
            EnsureAccount(accountId);
            var chronological = _entries
                .Where(e => e.Lines.Any(l => l.AccountId == accountId))
                .OrderBy(e => e.PostedAt)
                .ThenBy(e => e.EntryId)
                .ToList();

            decimal running = 0m;
            var lines = new List<StatementLine>(chronological.Count);
            foreach (var entry in chronological)
            {
                var line = entry.Lines.First(l => l.AccountId == accountId);
                running += line.Debit - line.Credit;
                lines.Add(new StatementLine
                {
                    EntryId = entry.EntryId,
                    PostedAt = entry.PostedAt,
                    Description = entry.Description,
                    Debit = line.Debit,
                    Credit = line.Credit,
                    RunningBalance = running,
                    ReversesEntryId = entry.ReversesEntryId
                });
            }

            lines.Reverse();
            return lines;
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

    public bool VerifyAuditChain()
    {
        lock (_gate)
        {
            var previous = "GENESIS";
            foreach (var evt in _audit.OrderBy(a => a.OccurredAt).ThenBy(a => a.EventId))
            {
                if (!string.Equals(evt.PreviousHash, previous, StringComparison.Ordinal))
                    return false;
                var expected = Hash(previous + "|" + evt.Action + "|" + evt.Details + "|" + evt.OccurredAt.ToUnixTimeMilliseconds());
                if (!string.Equals(evt.EventHash, expected, StringComparison.Ordinal))
                    return false;
                previous = evt.EventHash;
            }

            return true;
        }
    }

    private JournalEntry PostBalanced(
        string description,
        string? idempotencyKey,
        string? reversesEntryId,
        params LedgerLine[] lines)
    {
        if (lines.Length < 2)
            throw new InvalidOperationException("Journal entry requires at least two lines.");
        if (lines.Sum(l => l.Debit) != lines.Sum(l => l.Credit))
            throw new InvalidOperationException("Unbalanced journal entry.");

        var entry = new JournalEntry
        {
            EntryId = $"JE-{Guid.NewGuid():N}"[..18].ToUpperInvariant(),
            Description = description,
            PostedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey,
            ReversesEntryId = reversesEntryId,
            Lines = lines
        };
        _entries.Add(entry);
        return entry;
    }

    private bool TryReplay(string prefix, string? key, string fingerprint, out string? entryId)
    {
        entryId = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var mapKey = prefix + key;
        if (!_idempotency.TryGetValue(mapKey, out var record))
            return false;
        if (!string.Equals(record.Fingerprint, fingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("Idempotency key reused with a different payload.");
        entryId = record.EntryId;
        return true;
    }

    private void RememberIdempotency(string prefix, string? key, string fingerprint, string entryId)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _idempotency[prefix + key] = new IdempotencyRecord(entryId, fingerprint);
    }

    private static string Fingerprint(params string[] parts) =>
        Hash(string.Join("|", parts));

    private static void ValidateMoneyScale(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount)
            throw new InvalidOperationException("Amount must have at most two decimal places.");
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

    private static string SystemClearingId(string currency) =>
        $"SYS-CLEARING-{currency}";

    private string EnsureSystemClearing(string currency)
    {
        var id = SystemClearingId(currency);
        if (!_accounts.ContainsKey(id))
        {
            _accounts[id] = new Account
            {
                AccountId = id,
                OwnerName = "System Clearing",
                Currency = currency
            };
            WriteAudit("account.created", $"accountId={id}; owner=System Clearing");
        }

        return id;
    }

    private void WriteAudit(string action, string details)
    {
        var occurred = DateTimeOffset.UtcNow;
        var eventHash = Hash(_lastAuditHash + "|" + action + "|" + details + "|" + occurred.ToUnixTimeMilliseconds());
        var evt = new AuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Action = action,
            Details = details,
            OccurredAt = occurred,
            PreviousHash = _lastAuditHash,
            EventHash = eventHash
        };
        _audit.Add(evt);
        _lastAuditHash = eventHash;
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
