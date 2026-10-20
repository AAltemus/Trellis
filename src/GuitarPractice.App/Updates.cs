using System.Reflection;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace GuitarPractice.App;

/// <summary>"updates" AppSettings row.</summary>
public sealed record UpdateSettings(bool CheckOnStartup = true);

/// <summary>
/// Velopack updates from the GitHub releases. Only an installed copy (Setup.exe / .pkg / AppImage) can update itself;
/// a build run from VS Code reports <see cref="IsInstalled"/> false and never goes to the network.
/// </summary>
public sealed class UpdateService
{
    public const string RepoUrl = "https://github.com/AAltemus/Trellis";

    // Only when VelopackApp.Build().Run() has run (Program.Main always does it first): UpdateManager throws without it,
    // e.g. under the XAML previewer or in headless checks, which then simply count as "not installed".
    private readonly UpdateManager? _manager = VelopackLocator.IsCurrentSet
        ? new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false))
        : null;

    public bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>The installed version, or the build's own version when running from a build folder.</summary>
    public string Version =>
        _manager?.CurrentVersion?.ToString()
        ?? (typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "dev").Split('+')[0];

    // The members below are used only when IsInstalled (so _manager exists).

    /// <summary>Null when this is the latest release. Throws on network errors (caller shows the message).</summary>
    public Task<UpdateInfo?> CheckAsync() => Manager.CheckForUpdatesAsync();

    public Task DownloadAsync(UpdateInfo update, Action<int> progress) => Manager.DownloadUpdatesAsync(update, progress);

    /// <summary>
    /// Velopack's updater waits for this process to exit, applies the downloaded release and starts the new version.
    /// The caller then closes the app normally, so the timer and tool windows save as on any exit.
    /// </summary>
    public void ApplyAfterExit(UpdateInfo update) =>
        Manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: false, restart: true);

    private UpdateManager Manager => _manager ?? throw new InvalidOperationException("Not installed: no updates.");
}
