using GuitarPractice.App.ViewModels;
using GuitarPractice.Audio;
using GuitarPractice.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace GuitarPractice.App;

/// <summary>
/// The services every head (desktop, phones) registers the same way. A head adds its own platform pieces on top:
/// <see cref="IAudioDevices"/> (the audio backend) and <see cref="IToolWindows"/> (windows or pages), plus anything
/// only it has (desktop: window placement, the updater).
/// </summary>
public static class SharedServices
{
    public static IServiceCollection AddTrellisShared(this IServiceCollection services)
    {
        services.AddPracticeInfrastructure();
        services.AddSingleton<INotificationSound, DeviceNotificationSound>(); // the ding, through the same audio backend as everything else
        services.AddSingleton<ThemeService>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<MidiPlayerViewModel>();
        services.AddTransient<MetronomeViewModel>();
        services.AddTransient<TunerViewModel>();
        services.AddTransient<RecorderViewModel>();
        services.AddTransient<ProgressViewModel>();
        services.AddTransient<EarTrainerViewModel>();
        services.AddTransient<RhythmTrainerViewModel>();
        services.AddTransient<MainWindowViewModel>();
        return services;
    }
}
