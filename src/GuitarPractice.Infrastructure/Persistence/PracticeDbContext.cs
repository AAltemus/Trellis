using GuitarPractice.Domain;
using GuitarPractice.Infrastructure.Persistence.Seed;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Persistence;

public sealed class PracticeDbContext(DbContextOptions<PracticeDbContext> options) : DbContext(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Subcategory> Subcategories => Set<Subcategory>();
    public DbSet<Tuning> Tunings => Set<Tuning>();
    public DbSet<Routine> Routines => Set<Routine>();
    public DbSet<RoutineItem> RoutineItems => Set<RoutineItem>();
    public DbSet<RoutineDayBudget> RoutineDayBudgets => Set<RoutineDayBudget>();
    public DbSet<WeeklyPlan> WeeklyPlans => Set<WeeklyPlan>();
    public DbSet<PlanDay> PlanDays => Set<PlanDay>();
    public DbSet<PlanEntry> PlanEntries => Set<PlanEntry>();
    public DbSet<PracticeLog> PracticeLogs => Set<PracticeLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<ProgressEntry> ProgressEntries => Set<ProgressEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PracticeDbContext).Assembly);
        BuiltInData.Seed(modelBuilder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no DateTimeOffset ordering support; store as UTC ticks so ORDER BY works.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter>();
    }
}
