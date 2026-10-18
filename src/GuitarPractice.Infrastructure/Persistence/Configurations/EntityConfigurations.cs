using GuitarPractice.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace GuitarPractice.Infrastructure.Persistence.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> b)
    {
        b.Property(c => c.Name).HasMaxLength(100);
        b.Property(c => c.ColorHex).HasMaxLength(9);
        b.HasIndex(c => c.SortOrder);
    }
}

internal sealed class SubcategoryConfiguration : IEntityTypeConfiguration<Subcategory>
{
    public void Configure(EntityTypeBuilder<Subcategory> b)
    {
        b.ToTable("Subcategories", t => t.HasCheckConstraint(
            "CK_Subcategory_SessionRange", "MinSessionMinutes > 0 AND MaxSessionMinutes >= MinSessionMinutes"));
        b.Property(s => s.Name).HasMaxLength(200);
        b.Property(s => s.Notes).HasMaxLength(4000);
        b.Property(s => s.Artist).HasMaxLength(200);
        b.Property(s => s.SongKey).HasMaxLength(20);
        b.Property(s => s.ReferenceUrl).HasMaxLength(2000);
        b.Ignore(s => s.DisplayName);

        b.HasOne(s => s.Category)
            .WithMany(c => c.Subcategories)
            .HasForeignKey(s => s.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);  // archive, don't delete
        b.HasOne(s => s.Tuning)
            .WithMany()
            .HasForeignKey(s => s.TuningId)
            .OnDelete(DeleteBehavior.SetNull);
        b.HasIndex(s => new { s.CategoryId, s.SortOrder });
    }
}

internal sealed class TuningConfiguration : IEntityTypeConfiguration<Tuning>
{
    public void Configure(EntityTypeBuilder<Tuning> b)
    {
        b.Property(t => t.Name).HasMaxLength(100);
        b.Property(t => t.Notes).HasMaxLength(100);
        b.Ignore(t => t.NoteNames);
    }
}

internal sealed class RoutineConfiguration : IEntityTypeConfiguration<Routine>
{
    public void Configure(EntityTypeBuilder<Routine> b)
    {
        b.Property(r => r.Name).HasMaxLength(100);
        b.HasMany(r => r.Items).WithOne(i => i.Routine).HasForeignKey(i => i.RoutineId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(r => r.DayBudgets).WithOne(d => d.Routine).HasForeignKey(d => d.RoutineId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RoutineItemConfiguration : IEntityTypeConfiguration<RoutineItem>
{
    public void Configure(EntityTypeBuilder<RoutineItem> b)
    {
        b.HasKey(i => new { i.RoutineId, i.SubcategoryId });
        b.HasOne(i => i.Subcategory).WithMany().HasForeignKey(i => i.SubcategoryId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_RoutineItem_Weight", "Weight BETWEEN 0 AND 10"));
    }
}

internal sealed class RoutineDayBudgetConfiguration : IEntityTypeConfiguration<RoutineDayBudget>
{
    public void Configure(EntityTypeBuilder<RoutineDayBudget> b)
    {
        b.HasKey(d => new { d.RoutineId, d.Day });
        b.ToTable(t => t.HasCheckConstraint("CK_RoutineDayBudget_Minutes", "Minutes >= 0"));
    }
}

internal sealed class WeeklyPlanConfiguration : IEntityTypeConfiguration<WeeklyPlan>
{
    public void Configure(EntityTypeBuilder<WeeklyPlan> b)
    {
        b.Property(p => p.Name).HasMaxLength(100);
        b.HasIndex(p => new { p.IsTemplate, p.WeekStart });
        b.HasIndex(p => p.Name).IsUnique().HasFilter("\"IsTemplate\" = 1"); // saved-week names are unique
        b.HasOne(p => p.Routine).WithMany().HasForeignKey(p => p.RoutineId).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(p => p.Days).WithOne(d => d.WeeklyPlan).HasForeignKey(d => d.WeeklyPlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PlanDayConfiguration : IEntityTypeConfiguration<PlanDay>
{
    public void Configure(EntityTypeBuilder<PlanDay> b)
    {
        b.Ignore(d => d.TotalMinutes);
        b.HasIndex(d => new { d.WeeklyPlanId, d.Date }).IsUnique();
        b.HasMany(d => d.Entries).WithOne(e => e.PlanDay).HasForeignKey(e => e.PlanDayId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class PlanEntryConfiguration : IEntityTypeConfiguration<PlanEntry>
{
    public void Configure(EntityTypeBuilder<PlanEntry> b)
    {
        b.HasOne(e => e.Subcategory).WithMany().HasForeignKey(e => e.SubcategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.PlanDayId, e.Order });
        b.HasIndex(e => e.SubcategoryId); // stats queries: history per subcategory
    }
}

internal sealed class PracticeLogConfiguration : IEntityTypeConfiguration<PracticeLog>
{
    public void Configure(EntityTypeBuilder<PracticeLog> b)
    {
        b.HasIndex(l => new { l.Date, l.SubcategoryId }).IsUnique();
        b.HasOne(l => l.Subcategory).WithMany().HasForeignKey(l => l.SubcategoryId).OnDelete(DeleteBehavior.Restrict);
        b.ToTable(t => t.HasCheckConstraint("CK_PracticeLog_Elapsed", "ElapsedSeconds >= 0"));
    }
}

internal sealed class ProgressEntryConfiguration : IEntityTypeConfiguration<ProgressEntry>
{
    public void Configure(EntityTypeBuilder<ProgressEntry> b)
    {
        b.HasIndex(e => new { e.Date, e.SubcategoryId }).IsUnique();
        b.HasIndex(e => e.SubcategoryId);
        b.HasOne(e => e.Subcategory).WithMany().HasForeignKey(e => e.SubcategoryId).OnDelete(DeleteBehavior.Restrict);
        b.Property(e => e.Notes).HasMaxLength(ProgressEntry.MaxNotesLength);
        b.Ignore(e => e.IsEmpty);
        b.HasMany(e => e.Exercises).WithOne(x => x.ProgressEntry).HasForeignKey(x => x.ProgressEntryId).OnDelete(DeleteBehavior.Cascade);
        b.ToTable(t => t.HasCheckConstraint("CK_ProgressEntry_Rating", "Rating IS NULL OR (Rating >= 1 AND Rating <= 5)"));
    }
}

internal sealed class ExerciseTargetConfiguration : IEntityTypeConfiguration<ExerciseTarget>
{
    public void Configure(EntityTypeBuilder<ExerciseTarget> b)
    {
        b.HasIndex(x => new { x.SubcategoryId, x.Key }).IsUnique();
        b.HasOne(x => x.Subcategory).WithMany().HasForeignKey(x => x.SubcategoryId).OnDelete(DeleteBehavior.Restrict);
        b.Property(x => x.Key).HasMaxLength(ProgressExercise.MaxNameLength + 8);
        b.ToTable(t => t.HasCheckConstraint("CK_ExerciseTarget_Bpm", "TargetBpm >= 1 AND TargetBpm <= 400"));
    }
}

internal sealed class ProgressExerciseConfiguration : IEntityTypeConfiguration<ProgressExercise>
{
    public void Configure(EntityTypeBuilder<ProgressExercise> b)
    {
        b.HasIndex(x => new { x.ProgressEntryId, x.SortOrder });
        b.Property(x => x.Name).HasMaxLength(ProgressExercise.MaxNameLength).IsRequired();
        b.Property(x => x.NoteValue).HasConversion<int?>();
        b.ToTable(t =>
        {
            t.HasCheckConstraint("CK_ProgressExercise_Bpm", "Bpm IS NULL OR (Bpm >= 1 AND Bpm <= 400)");
            t.HasCheckConstraint("CK_ProgressExercise_NoteValue", "NoteValue IS NULL OR (NoteValue >= 1 AND NoteValue <= 6)");
            t.HasCheckConstraint("CK_ProgressExercise_RemovedRun", "RemovedRun IS NULL OR RemovedRun >= 1");
        });
    }
}

internal sealed class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> b)
    {
        b.HasKey(s => s.Key);
        b.Property(s => s.Key).HasMaxLength(100);
    }
}
