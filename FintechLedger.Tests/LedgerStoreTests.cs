using FintechLedger.Api.Domain;

namespace FintechLedger.Tests;

public class LedgerStoreTests
{
    [Fact]
    public void Fund_ThenTransfer_PostsBalancedDoubleEntry()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            var customer = store.CreateAccount("Ali Veli", "TRY");
            store.Fund(new FundAccountRequest(customer.AccountId, 250m, "Opening"));
            Assert.Equal(250m, store.GetBalance(customer.AccountId));
            Assert.True(store.IsLedgerBalanced());
            Assert.True(store.VerifyAuditChain());
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Transfer_RejectsInsufficientFunds()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            var a = store.CreateAccount("Ali", "TRY");
            var b = store.CreateAccount("Ayse", "TRY");
            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.Transfer(new TransferRequest(a.AccountId, b.AccountId, 10m)));
            Assert.Contains("Insufficient", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Statement_IncludesRunningBalance()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            var alice = store.CreateAccount("Alice", "TRY");
            var bob = store.CreateAccount("Bob", "TRY");
            store.Fund(new FundAccountRequest(alice.AccountId, 500m, "Open"));
            store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 120m, "Pay"));

            var statement = store.GetStatement(alice.AccountId);
            Assert.Equal(2, statement.Count);
            Assert.Equal(380m, statement[0].RunningBalance);
            Assert.Equal(500m, statement[1].RunningBalance);
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Reverse_RestoresBalancesWithoutMutatingHistory()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            var alice = store.CreateAccount("Alice", "TRY");
            var bob = store.CreateAccount("Bob", "TRY");
            store.Fund(new FundAccountRequest(alice.AccountId, 200m));
            var posted = store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 50m));
            store.Reverse(new ReverseEntryRequest(posted.EntryId, "Ops correction"));

            Assert.Equal(200m, store.GetBalance(alice.AccountId));
            Assert.Equal(0m, store.GetBalance(bob.AccountId));
            Assert.True(store.IsLedgerBalanced());
            Assert.Equal(2, store.GetStatement(bob.AccountId).Count);
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Idempotency_RejectsKeyReuseWithDifferentPayload()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            var alice = store.CreateAccount("Alice", "TRY");
            var bob = store.CreateAccount("Bob", "TRY");
            store.Fund(new FundAccountRequest(alice.AccountId, 300m));
            store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 50m, IdempotencyKey: "k1"));

            var ex = Assert.Throws<InvalidOperationException>(() =>
                store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 60m, IdempotencyKey: "k1")));
            Assert.Contains("different payload", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void CreateAccount_RejectsInvalidCurrency()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            Assert.Throws<InvalidOperationException>(() => store.CreateAccount("X", "XXX"));
            Assert.Throws<InvalidOperationException>(() => store.CreateAccount("X", "TRYX"));
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Transfer_IdempotencyKey_ReplaysSameEntry()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            var alice = store.CreateAccount("Alice", "TRY");
            var bob = store.CreateAccount("Bob", "TRY");
            store.Fund(new FundAccountRequest(alice.AccountId, 300m));
            var first = store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 50m, IdempotencyKey: "txn-1"));
            var second = store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 50m, IdempotencyKey: "txn-1"));
            Assert.Equal(first.EntryId, second.EntryId);
            Assert.True(second.Replayed);
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Transfer_RejectsCurrencyMismatch()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            using var db = TestDbContextFactory.OpenFileBased(dbPath);
            var store = new LedgerStore(db);
            var tryAccount = store.CreateAccount("TRY Holder", "TRY");
            var usdAccount = store.CreateAccount("USD Holder", "USD");
            store.Fund(new FundAccountRequest(tryAccount.AccountId, 10m));
            Assert.Throws<InvalidOperationException>(() =>
                store.Transfer(new TransferRequest(tryAccount.AccountId, usdAccount.AccountId, 10m)));
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }

    [Fact]
    public void Account_SurvivesNewDbContextScope()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        try
        {
            string accountId;
            using (var first = TestDbContextFactory.OpenFileBased(dbPath))
            {
                var store = new LedgerStore(first);
                accountId = store.CreateAccount("Persist", "TRY").AccountId;
                store.Fund(new FundAccountRequest(accountId, 100m, IdempotencyKey: "p-fund"));
            }

            using var second = TestDbContextFactory.OpenFileBased(dbPath);
            var reloaded = new LedgerStore(second);
            Assert.Equal(100m, reloaded.GetBalance(accountId));
            Assert.True(reloaded.VerifyAuditChain());
            Assert.True(reloaded.IsLedgerBalanced());
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }
}
