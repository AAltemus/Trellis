using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Tests;

/// <summary>The checked-in migrations, not EnsureCreated, are what the app runs. Check they match the model.</summary>
public sealed class MigrationTests : IDisposable
{
    private readonly TestDatabase _db = new(create: false);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Migrations_are_in_sync_with_the_model()
    {
        using var db = _db.CreateDbContext();
        Assert.NotEmpty(db.Database.GetMigrations());
        Assert.False(db.Database.HasPendingModelChanges(), "Run scripts/sync-db.ps1 to add a migration.");
    }

    [Fact]
    public void Migrate_builds_a_working_database_from_scratch()
    {
        using (var db = _db.CreateDbContext())
        {
            db.Database.Migrate();
            Assert.Empty(db.Database.GetPendingMigrations());
        }

        // The stores work on the migrated schema (seed included).
        var routine = _db.Library.GetOrCreateDefaultRoutine();
        Assert.NotEmpty(routine.Items);
        var (plan, request) = _db.MakePlan(new DateOnly(2026, 10, 12));
        _db.Weeks.Lock(plan, request, routine.Id);
        _db.Weeks.Save("Block A", plan, request, routine.Id);
        Assert.NotNull(_db.Weeks.LoadLocked(plan.WeekStart));
        Assert.Single(_db.Weeks.ListSaved());
    }
}
