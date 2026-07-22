using FintechLedger.Api.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

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
        Assert.True(store.VerifyAuditChain());
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
    public void Statement_IncludesRunningBalance()
    {
        var store = new LedgerStore();
        var alice = store.CreateAccount("Alice", "TRY");
        var bob = store.CreateAccount("Bob", "TRY");
        store.Fund(new FundAccountRequest(alice.AccountId, 500m, "Open"));
        store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 120m, "Pay"));

        var statement = store.GetStatement(alice.AccountId);
        Assert.Equal(2, statement.Count);
        Assert.Equal(380m, statement[0].RunningBalance);
        Assert.Equal(500m, statement[1].RunningBalance);
    }

    [Fact]
    public void Reverse_RestoresBalancesWithoutMutatingHistory()
    {
        var store = new LedgerStore();
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

    [Fact]
    public void Idempotency_RejectsKeyReuseWithDifferentPayload()
    {
        var store = new LedgerStore();
        var alice = store.CreateAccount("Alice", "TRY");
        var bob = store.CreateAccount("Bob", "TRY");
        store.Fund(new FundAccountRequest(alice.AccountId, 300m));
        store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 50m, IdempotencyKey: "k1"));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            store.Transfer(new TransferRequest(alice.AccountId, bob.AccountId, 60m, IdempotencyKey: "k1")));
        Assert.Contains("different payload", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateAccount_RejectsInvalidCurrency()
    {
        var store = new LedgerStore();
        Assert.Throws<InvalidOperationException>(() => store.CreateAccount("X", "XXX"));
        Assert.Throws<InvalidOperationException>(() => store.CreateAccount("X", "TRYX"));
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

public class LedgerApiSecurityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public LedgerApiSecurityTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsSecurityHeaders()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
    }

    [Fact]
    public async Task CreateAccount_RejectsBlankOwner()
    {
        var response = await _client.PostAsJsonAsync("/api/accounts", new { ownerName = "  ", currency = "TRY" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task OpenApi_IsAvailable()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("openapi", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Transfer_UnknownAccount_ReturnsNotFound()
    {
        var response = await _client.PostAsJsonAsync("/api/transfers", new
        {
            fromAccountId = "ACC-DOESNOTEXIST",
            toAccountId = "ACC-ALSONOTHERE",
            amount = 1
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task EndToEnd_FundTransferStatementAuditVerify()
    {
        var createA = await _client.PostAsJsonAsync("/api/accounts", new { ownerName = "Alice", currency = "TRY" });
        createA.EnsureSuccessStatusCode();
        var alice = await createA.Content.ReadFromJsonAsync<JsonElement>();
        var aliceId = alice.GetProperty("accountId").GetString()!;

        var createB = await _client.PostAsJsonAsync("/api/accounts", new { ownerName = "Bob", currency = "TRY" });
        var bob = await createB.Content.ReadFromJsonAsync<JsonElement>();
        var bobId = bob.GetProperty("accountId").GetString()!;

        var fund = await _client.PostAsJsonAsync($"/api/accounts/{aliceId}/fund", new { amount = 1000, idempotencyKey = "e2e-fund" });
        fund.EnsureSuccessStatusCode();

        var xfer = await _client.PostAsJsonAsync("/api/transfers", new
        {
            fromAccountId = aliceId,
            toAccountId = bobId,
            amount = 250,
            idempotencyKey = "e2e-xfer"
        });
        xfer.EnsureSuccessStatusCode();

        var statement = await _client.GetFromJsonAsync<JsonElement>($"/api/accounts/{aliceId}/statement");
        Assert.True(statement.GetArrayLength() >= 2);
        Assert.True(statement[0].TryGetProperty("runningBalance", out _));

        var verify = await _client.GetFromJsonAsync<JsonElement>("/api/audit/verify");
        Assert.True(verify.GetProperty("intact").GetBoolean());
        Assert.True(verify.GetProperty("balanced").GetBoolean());
    }

    [Fact]
    public async Task CreateAccount_RejectsExcessivelyLongOwnerName()
    {
        var huge = new string('A', 200);
        var response = await _client.PostAsJsonAsync("/api/accounts", new { ownerName = huge, currency = "TRY" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}