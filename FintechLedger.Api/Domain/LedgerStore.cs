using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FintechLedger.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FintechLedger.Api.Domain;

public sealed class LedgerStore(LedgerDbContext db)
{
    public Account CreateAccount(string ownerName, string currency)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerName);
        var trimmed = ownerName.Trim();
        if (trimmed.Length > 120)
            throw new InvalidOperationException("Owner name must be at most 120 characters.");
        var ccy = Iso4217.NormalizeOrThrow(currency);

        using var tx = db.Database.BeginTransaction();
        var account = new Account
        {
            AccountId = $"ACC-{Guid.NewGuid():N}"[..16].ToUpperInvariant(),
            OwnerName = trimmed,
            Currency = ccy,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Accounts.Add(account);
        WriteAudit("account.created", $"accountId={account.AccountId}; owner={account.OwnerName}; ccy={ccy}");
        db.SaveChanges();
        tx.Commit();
        return account;
    }

    public Account? GetAccount(string accountId) =>
        db.Accounts.AsNoTracking().FirstOrDefault(a => a.AccountId == accountId);

    public decimal GetBalance(string accountId)
    {
        EnsureAccount(accountId);
        return BalanceOf(accountId);
    }

    public TransferResult Fund(FundAccountRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            throw new InvalidOperationException("Funding amount must be greater than zero.");
        ValidateMoneyScale(request.Amount);

        using var tx = db.Database.BeginTransaction();
        var account = EnsureAccount(request.AccountId);
        var fingerprint = Fingerprint("fund", account.AccountId, request.Amount.ToString(CultureInfo.InvariantCulture));
        if (TryReplay("fund:", request.IdempotencyKey, fingerprint, out var replayedId))
        {
            tx.Commit();
            return new TransferResult(
                replayedId!,
                BalanceOf(SystemClearingId(account.Currency)),
                BalanceOf(account.AccountId),
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
        db.SaveChanges();
        tx.Commit();

        return new TransferResult(
            entry.EntryId,
            BalanceOf(clearingId),
            BalanceOf(account.AccountId),
            Replayed: false);
    }

    public TransferResult Transfer(TransferRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount <= 0)
            throw new InvalidOperationException("Transfer amount must be greater than zero.");
        ValidateMoneyScale(request.Amount);
        if (string.Equals(request.FromAccountId, request.ToAccountId, StringComparison.Ordinal))
            throw new InvalidOperationException("Source and destination accounts must differ.");

        using var tx = db.Database.BeginTransaction();
        var fingerprint = Fingerprint(
            "xfer",
            request.FromAccountId,
            request.ToAccountId,
            request.Amount.ToString(CultureInfo.InvariantCulture));
        if (TryReplay("xfer:", request.IdempotencyKey, fingerprint, out var replayedId))
        {
            tx.Commit();
            return new TransferResult(
                replayedId!,
                BalanceOf(request.FromAccountId),
                BalanceOf(request.ToAccountId),
                Replayed: true);
        }

        var from = EnsureAccount(request.FromAccountId);
        var to = EnsureAccount(request.ToAccountId);
        if (!string.Equals(from.Currency, to.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException("Currency mismatch between accounts.");

        var fromBalance = BalanceOf(from.AccountId);
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
        db.SaveChanges();
        tx.Commit();

        return new TransferResult(
            entry.EntryId,
            BalanceOf(from.AccountId),
            BalanceOf(to.AccountId),
            Replayed: false);
    }

    public TransferResult Reverse(ReverseEntryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.EntryId);

        using var tx = db.Database.BeginTransaction();
        var fingerprint = Fingerprint("rev", request.EntryId);
        if (TryReplay("rev:", request.IdempotencyKey, fingerprint, out var replayedId))
        {
            var original = db.JournalEntries.Include(e => e.Lines)
                .First(e => e.EntryId == request.EntryId);
            var a = original.Lines[0].AccountId;
            var b = original.Lines[1].AccountId;
            tx.Commit();
            return new TransferResult(replayedId!, BalanceOf(a), BalanceOf(b), Replayed: true);
        }

        var source = db.JournalEntries.Include(e => e.Lines)
                .FirstOrDefault(e => e.EntryId == request.EntryId)
            ?? throw new KeyNotFoundException($"Journal entry not found: {request.EntryId}");
        if (source.ReversesEntryId is not null)
            throw new InvalidOperationException("Cannot reverse a reversing entry directly.");
        if (db.JournalEntries.Any(e => e.ReversesEntryId == source.EntryId))
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
        db.SaveChanges();
        tx.Commit();

        return new TransferResult(
            entry.EntryId,
            BalanceOf(mirrored[0].AccountId),
            BalanceOf(mirrored[1].AccountId),
            Replayed: false);
    }

    public IReadOnlyList<StatementLine> GetStatement(string accountId)
    {
        EnsureAccount(accountId);
        var chronological = db.JournalEntries
            .AsNoTracking()
            .Include(e => e.Lines)
            .Where(e => e.Lines.Any(l => l.AccountId == accountId))
            .AsEnumerable()
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

    public IReadOnlyList<AuditEvent> GetAuditTrail() =>
        db.AuditEvents.AsNoTracking()
            .AsEnumerable()
            .OrderByDescending(a => a.OccurredAt)
            .ToList();

    public bool IsLedgerBalanced()
    {
        var debit = db.LedgerLines.Sum(l => (decimal?)l.Debit) ?? 0m;
        var credit = db.LedgerLines.Sum(l => (decimal?)l.Credit) ?? 0m;
        return debit == credit;
    }

    public bool VerifyAuditChain()
    {
        var previous = "GENESIS";
        foreach (var evt in db.AuditEvents.AsNoTracking()
                     .AsEnumerable()
                     .OrderBy(a => a.OccurredAt)
                     .ThenBy(a => a.EventId))
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

        var entryId = $"JE-{Guid.NewGuid():N}"[..18].ToUpperInvariant();
        foreach (var line in lines)
            line.JournalEntryId = entryId;

        var entry = new JournalEntry
        {
            EntryId = entryId,
            Description = description,
            PostedAt = DateTimeOffset.UtcNow,
            IdempotencyKey = idempotencyKey,
            ReversesEntryId = reversesEntryId,
            Lines = lines.ToList()
        };
        db.JournalEntries.Add(entry);
        return entry;
    }

    private bool TryReplay(string prefix, string? key, string fingerprint, out string? entryId)
    {
        entryId = null;
        if (string.IsNullOrWhiteSpace(key))
            return false;

        var mapKey = prefix + key;
        var record = db.IdempotencyRecords.FirstOrDefault(r => r.MapKey == mapKey);
        if (record is null)
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
        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            MapKey = prefix + key,
            EntryId = entryId,
            Fingerprint = fingerprint
        });
    }

    private static string Fingerprint(params string[] parts) =>
        Hash(string.Join("|", parts));

    private static void ValidateMoneyScale(decimal amount)
    {
        if (decimal.Round(amount, 2) != amount)
            throw new InvalidOperationException("Amount must have at most two decimal places.");
    }

    private decimal BalanceOf(string accountId)
    {
        var debit = db.LedgerLines.Where(l => l.AccountId == accountId).Sum(l => (decimal?)l.Debit) ?? 0m;
        var credit = db.LedgerLines.Where(l => l.AccountId == accountId).Sum(l => (decimal?)l.Credit) ?? 0m;
        return debit - credit;
    }

    private Account EnsureAccount(string accountId) =>
        db.Accounts.FirstOrDefault(a => a.AccountId == accountId)
        ?? throw new KeyNotFoundException($"Account not found: {accountId}");

    private static string SystemClearingId(string currency) =>
        $"SYS-CLEARING-{currency}";

    private string EnsureSystemClearing(string currency)
    {
        var id = SystemClearingId(currency);
        if (!db.Accounts.Any(a => a.AccountId == id))
        {
            db.Accounts.Add(new Account
            {
                AccountId = id,
                OwnerName = "System Clearing",
                Currency = currency,
                CreatedAt = DateTimeOffset.UtcNow
            });
            WriteAudit("account.created", $"accountId={id}; owner=System Clearing");
        }

        return id;
    }

    private void WriteAudit(string action, string details)
    {
        var state = db.AuditChainStates.FirstOrDefault(s => s.Id == 1);
        if (state is null)
        {
            state = new AuditChainState { Id = 1, LastHash = "GENESIS" };
            db.AuditChainStates.Add(state);
        }

        var occurred = DateTimeOffset.UtcNow;
        var eventHash = Hash(state.LastHash + "|" + action + "|" + details + "|" + occurred.ToUnixTimeMilliseconds());
        db.AuditEvents.Add(new AuditEvent
        {
            EventId = Guid.NewGuid().ToString("N"),
            Action = action,
            Details = details,
            OccurredAt = occurred,
            PreviousHash = state.LastHash,
            EventHash = eventHash
        });
        state.LastHash = eventHash;
    }

    private static string Hash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
