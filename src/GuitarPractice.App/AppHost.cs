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
        builder.Services.AddSingleton<INotificationSound, SystemNotificationSound>();
        builder.Services.AddSingleton<IAudioOutput, PortAudioOutput>(); // PortAudio is initialized lazily on first use
        builder.Services.AddSingleton<IToolWindows, ToolWindows>();
        builder.Services.AddSingleton<IAudioInput, PortAudioInput>();
        builder.Services.AddTransient<MetronomeViewModel>();
        builder.Services.AddTransient<TunerViewModel>();
        builder.Services.AddTransient<MainWindowViewModel>();

        return builder.Build();
    }
}
