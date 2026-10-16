using GuitarPractice.Domain;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Tests;

/// <summary>
/// A private in-memory SQLite database per test: one open connection (the DB lives as long as it does), schema +
/// seed via <c>EnsureCreated</c>, and the real stores on top. Never touches %LOCALAPPDATA%.
/// </summary>
public sealed class TestDatabase : IDisposable, IDbContextFactory<PracticeDbContext>
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<PracticeDbContext> _options;

    public TestDatabase(bool create = true)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = ":memory:",
            ForeignKeys = true,
        }.ToString());
        _connection.Open();
        _options = new DbContextOptionsBuilder<PracticeDbContext>().UseSqlite(_connection).Options;

        if (create)
        {
            using var db = CreateDbContext();
            db.Database.EnsureCreated();
        }

        Library = new LibraryStore(this);
        Logs = new PracticeLogStore(this, Clock);
        Weeks = new WeekStore(this, Clock);
    }

    public FixedClock Clock { get; } = new(new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero));
    public LibraryStore Library { get; }
    public PracticeLogStore Logs { get; }
    public WeekStore Weeks { get; }

    public PracticeDbContext CreateDbContext() => new(_options);

    /// <summary>Runs a query against a fresh context (the stores' own contexts are already disposed).</summary>
    public T Query<T>(Func<PracticeDbContext, T> query)
    {
        using var db = CreateDbContext();
        return query(db);
    }

    /// <summary>
    /// A real planner week over the first subcategory of four exercise categories (the warmup one pinned),
    /// 60 min every day.
    /// </summary>
    public (PlanResult Plan, PlanRequest Request) MakePlan(DateOnly weekStart, int seed = 1)
    {
        var subcategories = Library.LoadLibrary()
            .Where(c => c.Kind == CategoryKind.Exercise)
            .Select(c => c.Subcategories.First())
            .Take(4)
            .ToList();
        var tasks = subcategories.Select(s => s.ToPlanSpec(s.Pin == PinMode.None ? 5 : 1)).ToList();
        var budgets = Enum.GetValues<DayOfWeek>().ToDictionary(d => d, _ => 60);
        var request = new PlanRequest(weekStart, budgets, tasks, new PlannerOptions(), seed);
        return (WeeklyPlanner.Generate(request), request);
    }

    public void Dispose() => _connection.Dispose();
}

public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
