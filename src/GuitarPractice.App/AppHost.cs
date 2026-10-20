using GuitarPractice.App.ViewModels;
using GuitarPractice.Audio;
using GuitarPractice.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace GuitarPractice.App;

internal static class AppHost
{
    public static IHost Build()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Services.AddPracticeInfrastructure();
        builder.Services.AddSingleton<INotificationSound, DeviceNotificationSound>(); // the ding, through PortAudio like everything else
        builder.Services.AddSingleton<IAudioDevices, PortAudioDevices>(); // each tool window creates + disposes its own streams
        builder.Services.AddSingleton<IToolWindows, ToolWindows>();
        builder.Services.AddSingleton<WindowPlacementService>();
        builder.Services.AddSingleton<ThemeService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<UpdateViewModel>(); // one instance: main window banner + Settings → Updates
        builder.Services.AddTransient<SettingsViewModel>();
        builder.Services.AddTransient<MidiPlayerViewModel>();
        builder.Services.AddTransient<MetronomeViewModel>();
        builder.Services.AddTransient<TunerViewModel>();
        builder.Services.AddTransient<RecorderViewModel>();
        builder.Services.AddTransient<ProgressViewModel>();
        builder.Services.AddTransient<EarTrainerViewModel>();
        builder.Services.AddTransient<MainWindowViewModel>();

        return builder.Build();
    }
}
