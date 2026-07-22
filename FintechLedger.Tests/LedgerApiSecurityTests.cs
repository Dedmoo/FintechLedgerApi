using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace FintechLedger.Tests;

public class LedgerApiSecurityTests : IClassFixture<LedgerWebApplicationFactory>
{
    private readonly HttpClient _client;

    public LedgerApiSecurityTests(LedgerWebApplicationFactory factory)
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

public class LedgerPersistenceEndpointTests
{
    [Fact]
    public async Task AccountBalance_SurvivesNewWebApplicationFactoryInstance()
    {
        var dbPath = TestDbContextFactory.CreateTempDbPath();
        string aliceId;
        try
        {
            await using (var first = new LedgerWebApplicationFactory(dbPath, ownsDbFile: false))
            {
                var client = first.CreateClient();
                var create = await client.PostAsJsonAsync("/api/accounts", new { ownerName = "Persist Alice", currency = "TRY" });
                create.EnsureSuccessStatusCode();
                var body = await create.Content.ReadFromJsonAsync<JsonElement>();
                aliceId = body.GetProperty("accountId").GetString()!;
                var fund = await client.PostAsJsonAsync($"/api/accounts/{aliceId}/fund", new { amount = 75, idempotencyKey = "persist-fund" });
                fund.EnsureSuccessStatusCode();
            }

            await using (var second = new LedgerWebApplicationFactory(dbPath, ownsDbFile: false))
            {
                var client = second.CreateClient();
                var balance = await client.GetFromJsonAsync<JsonElement>($"/api/accounts/{aliceId}/balance");
                Assert.Equal(75m, balance.GetProperty("balance").GetDecimal());
            }
        }
        finally
        {
            TestDbContextFactory.DeleteDbFile(dbPath);
        }
    }
}
