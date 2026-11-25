using GuitarPractice.App.ViewModels;
using GuitarPractice.Audio;
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

        builder.Services.AddTrellisShared();
        // Desktop pieces.
        builder.Services.AddSingleton<IAudioDevices, PortAudioDevices>(); // each tool window creates + disposes its own streams
        builder.Services.AddSingleton<IToolWindows, ToolWindows>();
        builder.Services.AddSingleton<WindowPlacementService>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<UpdateViewModel>(); // one instance: main window banner + Settings → Updates
        builder.Services.AddTransient<DesktopSettings>(); // the Settings window: shared settings + the updater

        return builder.Build();
    }
}
