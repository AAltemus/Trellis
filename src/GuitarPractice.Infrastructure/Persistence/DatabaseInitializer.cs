using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace GuitarPractice.Infrastructure.Persistence;

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "GuitarPractice");

    public static string DatabasePath => Path.Combine(DataDirectory, "practice.db");

    public static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        ForeignKeys = true,
        Pooling = true,
    }.ToString();
}

/// <summary>
/// Startup: back up the existing DB if migrations are pending, migrate, switch to WAL.
/// Synchronous on purpose: it runs once before the first window, and blocking the UI thread on
/// async EF calls under Avalonia's SynchronizationContext is a deadlock waiting to happen.
/// </summary>
public sealed class DatabaseInitializer(IDbContextFactory<PracticeDbContext> contextFactory, ILogger<DatabaseInitializer> logger)
{
    public void Initialize()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        using var db = contextFactory.CreateDbContext();

        if (!db.Database.GetMigrations().Any())
            throw new InvalidOperationException(
                "No EF Core migrations found. Run scripts/bootstrap.ps1 (or: dotnet ef migrations add InitialCreate " +
                "-p src/GuitarPractice.Infrastructure -s src/GuitarPractice.Infrastructure -o Persistence/Migrations).");

        // Fail with a clear message instead of a "no such table" error deep inside the first query.
        if (db.Database.HasPendingModelChanges())
            throw new InvalidOperationException(
                "The data model changed since the last migration. Run scripts/sync-db.ps1 " +
                "(VS Code runs it automatically before every F5 launch).");

        var pending = db.Database.GetPendingMigrations().ToList();
        if (pending.Count > 0 && File.Exists(AppPaths.DatabasePath))
            Backup();

        if (pending.Count > 0)
        {
            logger.LogInformation("Applying {Count} migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
            db.Database.Migrate();
        }

        ReserveBuiltInIds(db);
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    /// <summary>
    /// Reserve IDs below 10,000 for built-in library rows so future seed migrations never collide with
    /// user-created categories/subcategories/tunings. Idempotent; rows exist once HasData has inserted into the table.
    /// </summary>
    internal static void ReserveBuiltInIds(PracticeDbContext db) =>
        db.Database.ExecuteSqlRaw(
            "UPDATE sqlite_sequence SET seq = 10000 WHERE name IN ('Categories', 'Subcategories', 'Tunings') AND seq < 10000;");

    /// <summary>SQLite online backup API: consistent even with a WAL file present.</summary>
    private void Backup()
    {
        var backupPath = AppPaths.DatabasePath + ".bak";
        using var source = new SqliteConnection(AppPaths.ConnectionString);
        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath }.ToString());
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
        logger.LogInformation("Backed up database to {Path}", backupPath);
    }
}
