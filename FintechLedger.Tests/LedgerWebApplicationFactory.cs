using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace FintechLedger.Tests;

public sealed class LedgerWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly bool _ownsDbFile;

    public LedgerWebApplicationFactory() : this(dbPath: null, ownsDbFile: true)
    {
    }

    internal LedgerWebApplicationFactory(string? dbPath, bool ownsDbFile)
    {
        DbPath = dbPath ?? TestDbContextFactory.CreateTempDbPath();
        _ownsDbFile = ownsDbFile;
    }

    public string DbPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:LedgerDb"] = $"Data Source={DbPath}"
            });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _ownsDbFile)
            TestDbContextFactory.DeleteDbFile(DbPath);
    }
}
