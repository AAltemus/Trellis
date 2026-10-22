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

    /// <summary>Tuner input recordings and crash reports.</summary>
    public static string DiagnosticsDirectory => Path.Combine(DataDirectory, "diagnostics");

    public static string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = DatabasePath,
        ForeignKeys = true,
        Pooling = true,
    }.ToString();
}

/// <summary>The practice database was upgraded by a newer version of the app; this version can't open it safely.</summary>
public sealed class NewerDatabaseException(IReadOnlyList<string> unknownMigrations) : InvalidOperationException(
    "Your practice data was last opened by a newer version of Trellis, so this version can't open it safely. " +
    "Install the latest Trellis to open it. " +
    $"(Database changes this version doesn't know: {string.Join(", ", unknownMigrations)}.)")
{
    public IReadOnlyList<string> UnknownMigrations { get; } = unknownMigrations;
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
                "No EF Core migrations found. Run scripts/bootstrap.cs (dotnet run --file scripts/bootstrap.cs; or: dotnet ef migrations add InitialCreate " +
                "-p src/GuitarPractice.Infrastructure -s src/GuitarPractice.Infrastructure -o Persistence/Migrations).");

        EnsureNotNewer(db);

        // Fail with a clear message instead of a "no such table" error deep inside the first query.
        if (db.Database.HasPendingModelChanges())
            throw new InvalidOperationException(
                "The data model changed since the last migration. Run scripts/sync-db.cs (dotnet run --file scripts/sync-db.cs) " +
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
    /// The database has migrations this build doesn't know: a newer Trellis (or a newer dev build) already upgraded it.
    /// Opening it anyway would fail somewhere in the middle (unknown columns/tables), so refuse with a clear message.
    /// </summary>
    internal static void EnsureNotNewer(PracticeDbContext db)
    {
        var known = db.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
        var unknown = db.Database.GetAppliedMigrations().Where(m => !known.Contains(m)).ToList();
        if (unknown.Count > 0) throw new NewerDatabaseException(unknown);
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
