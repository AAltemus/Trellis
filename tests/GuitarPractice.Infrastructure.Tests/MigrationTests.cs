using Microsoft.EntityFrameworkCore.Infrastructure;
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

    private const string SingleBpmMigration = "20261017005301_Auto_20261016_205259";

    [Fact]
    public void Upgrading_keeps_bpms_logged_before_exercise_lines_existed()
    {
        int subcategoryId;
        using (var db = _db.CreateDbContext())
        {
            db.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate(SingleBpmMigration);
            subcategoryId = db.Database.SqlQueryRaw<int>("SELECT MIN(\"Id\") AS \"Value\" FROM \"Subcategories\"").AsEnumerable().First();
            // Two entries the old way: one with a BPM, one with only a rating.
            db.Database.ExecuteSqlRaw(
                "INSERT INTO \"ProgressEntries\" (\"Date\", \"SubcategoryId\", \"BpmReached\", \"Rating\", \"Notes\", \"UpdatedAt\") VALUES " +
                $"('2026-10-12', {subcategoryId}, 132, 4, 'old style', 0), ('2026-10-13', {subcategoryId}, NULL, 3, NULL, 0);");

            db.Database.Migrate(); // → latest
            Assert.Empty(db.Database.GetPendingMigrations());
        }

        using (var db = _db.CreateDbContext())
        {
            var entries = db.ProgressEntries.Include(e => e.Exercises).OrderBy(e => e.Date).ToList();
            Assert.Equal(2, entries.Count); // the table rebuild lost nothing
            Assert.Equal((4, "old style"), (entries[0].Rating, entries[0].Notes));
            var moved = Assert.Single(entries[0].Exercises);
            Assert.Equal(("", (int?)132, (Domain.NoteValue?)null, 0), (moved.Name, moved.Bpm, moved.NoteValue, moved.SortOrder));
            Assert.Empty(entries[1].Exercises);
            Assert.False(db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE name = 'ProgressBpmCarry'").AsEnumerable().First() > 0);
        }

        // And the stores read it as an unnamed exercise.
        var day = Assert.Single(new ProgressStore(_db, _db.Clock).History(subcategoryId), d => d.Exercises.Count > 0);
        Assert.Equal(132, day.Exercises[0].Bpm);
    }

    [Fact]
    public void Downgrading_keeps_the_best_bpm_per_day()
    {
        int subcategoryId;
        using (var db = _db.CreateDbContext())
        {
            db.Database.Migrate();
            subcategoryId = db.Subcategories.Min(s => s.Id);
        }
        new ProgressStore(_db, _db.Clock).Save(new DateOnly(2026, 10, 12), subcategoryId,
            [new("A", 120, Domain.NoteValue.Eighths), new("B", 140, Domain.NoteValue.Sixteenths)], 5, "x");

        using var down = _db.CreateDbContext();
        down.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>().Migrate(SingleBpmMigration);
        var bpm = down.Database.SqlQueryRaw<int>("SELECT \"BpmReached\" AS \"Value\" FROM \"ProgressEntries\"").AsEnumerable().Single();
        Assert.Equal(140, bpm);
    }
}
