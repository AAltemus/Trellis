using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace GuitarPractice.Infrastructure.Persistence;

/// <summary>Lets `dotnet ef` build the context without starting the Avalonia app.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PracticeDbContext>
{
    public PracticeDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PracticeDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options);
}
