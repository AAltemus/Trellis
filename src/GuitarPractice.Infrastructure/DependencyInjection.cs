using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace GuitarPractice.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddPracticeInfrastructure(this IServiceCollection services)
    {
        // Factory, not scoped context: desktop apps have no request scope; ViewModels create short-lived contexts.
        services.AddDbContextFactory<PracticeDbContext>(o => o.UseSqlite(AppPaths.ConnectionString));
        services.AddSingleton<DatabaseInitializer>();
        services.AddSingleton<LibraryStore>();
        services.AddSingleton<PracticeLogStore>();
        services.AddSingleton<WeekStore>();
        services.AddSingleton<SettingsStore>();
        services.AddSingleton<ProgressStore>();
        services.AddSingleton(sp => new MidiLibraryStore(sp.GetRequiredService<SettingsStore>()));
        services.AddSingleton<ProgressTracking>();
        services.AddSingleton<TakeLinkStore>();
        services.AddSingleton(TimeProvider.System);
        return services;
    }
}
