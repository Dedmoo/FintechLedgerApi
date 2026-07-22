using FintechLedger.Api.Data;
using FintechLedger.Api.Domain;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var contentRootPath = builder.Environment.ContentRootPath;

builder.Services.AddOpenApi();
builder.Services.AddDbContext<LedgerDbContext>((sp, options) =>
{
    var configuration = sp.GetRequiredService<IConfiguration>();
    var connectionString = configuration.GetConnectionString("LedgerDb") ?? "Data Source=fintechledger.db";
    options.UseSqlite(ResolveSqliteConnectionString(connectionString, contentRootPath));
});
builder.Services.AddScoped<LedgerStore>();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);

var app = builder.Build();

using (var startupScope = app.Services.CreateScope())
{
    var db = startupScope.ServiceProvider.GetRequiredService<LedgerDbContext>();
    db.Database.EnsureCreated();
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode='WAL';");
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Cache-Control"] = "no-store";
    await next();
});

app.MapOpenApi();
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
    catch (InvalidOperationException ex)
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

app.MapPost("/api/accounts/{accountId}/fund", (string accountId, FundAccountRequest request, LedgerStore store) =>
{
    try
    {
        var body = request with { AccountId = accountId };
        return Results.Ok(store.Fund(body));
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

app.MapPost("/api/transfers", (TransferRequest request, LedgerStore store) =>
{
    try
    {
        return Results.Ok(store.Transfer(request));
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

app.MapPost("/api/journal/reverse", (ReverseEntryRequest request, LedgerStore store) =>
{
    try
    {
        return Results.Ok(store.Reverse(request));
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

app.MapGet("/api/audit/verify", (LedgerStore store) =>
    Results.Ok(new { intact = store.VerifyAuditChain(), balanced = store.IsLedgerBalanced() }));

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "FintechLedgerApi" }));

app.Run();

static string ResolveSqliteConnectionString(string connectionString, string contentRootPath)
{
    const string prefix = "Data Source=";
    if (!connectionString.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        return connectionString;

    var path = connectionString[prefix.Length..].Trim();
    if (Path.IsPathRooted(path))
        return connectionString;

    return prefix + Path.GetFullPath(Path.Combine(contentRootPath, path));
}

public partial class Program;
