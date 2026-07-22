using FintechLedger.Api.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FintechLedger.Tests;

public static class TestDbContextFactory
{
    public static string CreateTempDbPath() =>
        Path.Combine(Path.GetTempPath(), $"fintechledger-tests-{Guid.NewGuid():N}.db");

    public static LedgerDbContext OpenFileBased(string dbPath)
    {
        var options = new DbContextOptionsBuilder<LedgerDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var context = new LedgerDbContext(options);
        context.Database.EnsureCreated();
        return context;
    }

    public static void DeleteDbFile(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { dbPath, $"{dbPath}-wal", $"{dbPath}-shm", $"{dbPath}-journal" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
