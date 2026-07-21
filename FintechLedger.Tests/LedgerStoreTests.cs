using FintechLedger.Api.Domain;

namespace FintechLedger.Tests;

public class LedgerStoreTests
{
    [Fact]
    public void Fund_ThenTransfer_PostsBalancedDoubleEntry()
    {
        var store = new LedgerStore();
        var customer = store.CreateAccount("Ali Veli", "TRY");

        store.Fund(new FundAccountRequest(customer.AccountId, 250m, "Opening"));

        Assert.Equal(250m, store.GetBalance(customer.AccountId));
        Assert.True(store.IsLedgerBalanced());
    }

    [Fact]
    public void Transfer_RejectsInsufficientFunds()
    {
        var store = new LedgerStore();
        var a = store.CreateAccount("Ali", "TRY");
        var b = store.CreateAccount("Ayse", "TRY");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.Transfer(new TransferRequest(a.AccountId, b.AccountId, 10m)));

        Assert.Contains("Insufficient", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Transfer_CustomerPayment_UpdatesBalancesAndAudit()
    {
        var store = new LedgerStore();
        var alice = store.CreateAccount("Alice", "TRY");
        var bob = store.CreateAccount("Bob", "TRY");

        store.Fund(new FundAccountRequest(alice.AccountId, 500m, "Open Alice"));
        var result = store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 120m, "Payment"));

        Assert.False(result.Replayed);
        Assert.Equal(380m, store.GetBalance(alice.AccountId));
        Assert.Equal(120m, store.GetBalance(bob.AccountId));
        Assert.True(store.IsLedgerBalanced());
        Assert.NotEmpty(store.GetAuditTrail());
        Assert.Single(store.GetStatement(bob.AccountId));
    }

    [Fact]
    public void Transfer_IdempotencyKey_ReplaysSameEntry()
    {
        var store = new LedgerStore();
        var alice = store.CreateAccount("Alice", "TRY");
        var bob = store.CreateAccount("Bob", "TRY");
        store.Fund(new FundAccountRequest(alice.AccountId, 300m));

        var first = store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 50m, IdempotencyKey: "txn-1"));
        var second = store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 50m, IdempotencyKey: "txn-1"));

        Assert.Equal(first.EntryId, second.EntryId);
        Assert.True(second.Replayed);
        Assert.Equal(250m, store.GetBalance(alice.AccountId));
        Assert.Equal(50m, store.GetBalance(bob.AccountId));
    }

    [Fact]
    public void Transfer_RejectsCurrencyMismatch()
    {
        var store = new LedgerStore();
        var tryAccount = store.CreateAccount("TRY Holder", "TRY");
        var usdAccount = store.CreateAccount("USD Holder", "USD");
        store.Fund(new FundAccountRequest(tryAccount.AccountId, 10m));

        Assert.Throws<InvalidOperationException>(() =>
            store.Transfer(new TransferRequest(tryAccount.AccountId, usdAccount.AccountId, 10m)));
    }
}
