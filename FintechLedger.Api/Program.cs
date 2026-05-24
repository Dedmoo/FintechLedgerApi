using FintechLedger.Api.Domain;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.AddSingleton<LedgerStore>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/", () => Results.Redirect("/openapi/v1.json"));

app.MapPost("/api/accounts", (CreateAccountRequest request, LedgerStore store) =>
{
    try
    {
        var account = store.CreateAccount(request.OwnerName, request.Currency);
        return Results.Created($"/api/accounts/{account.AccountId}", account);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/accounts/{accountId}", (string accountId, LedgerStore store) =>
{
    var account = store.GetAccount(accountId);
    return account is null ? Results.NotFound() : Results.Ok(account);
});

app.MapGet("/api/accounts/{accountId}/balance", (string accountId, LedgerStore store) =>
{
    try
    {
        return Results.Ok(new { accountId, balance = store.GetBalance(accountId) });
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapPost("/api/transfers", (TransferRequest request, LedgerStore store) =>
{
    try
    {
        var result = store.Transfer(request);
        return Results.Ok(result);
    }
    catch (KeyNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/accounts/{accountId}/statement", (string accountId, LedgerStore store) =>
{
    try
    {
        return Results.Ok(store.GetStatement(accountId));
    }
    catch (KeyNotFoundException)
    {
        return Results.NotFound();
    }
});

app.MapGet("/api/audit", (LedgerStore store) => Results.Ok(store.GetAuditTrail()));

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "fintech-ledger-api" }));

app.Run();

public partial class Program;
