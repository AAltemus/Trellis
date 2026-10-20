using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Infrastructure;
using Velopack;

namespace GuitarPractice.App.ViewModels;

/// <summary>
/// Update banner under the main menu, Help → Check for updates, and the Updates section in Settings (one shared
/// instance). Checks once at startup when installed and allowed; "Update and restart" downloads, then closes Trellis the
/// normal way (timer and tool windows save) and Velopack restarts it on the new version.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    public const string SettingsKey = "updates";

    private readonly UpdateService _updates;
    private readonly SettingsStore _settings;
    private UpdateInfo? _available;

    public UpdateViewModel(UpdateService updates, SettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(settings);
        _updates = updates;
        _settings = settings;
        _checkOnStartup = (settings.Get<UpdateSettings>(SettingsKey) ?? new UpdateSettings()).CheckOnStartup;
    }

    /// <summary>Set by the app: closes Trellis the normal way (File → Exit).</summary>
    public Action? RequestExit { get; set; }

    public string VersionText => $"Trellis {_updates.Version}" + (_updates.IsInstalled ? "" : " (running from a build, not installed)");

    [ObservableProperty]
    private bool _checkOnStartup;

    partial void OnCheckOnStartupChanged(bool value) => _settings.Set(SettingsKey, new UpdateSettings(value));

    /// <summary>The banner shows while this has text.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBannerVisible))]
    private string? _bannerText;

    public bool IsBannerVisible => BannerText is not null;

    /// <summary>Last result of a check, for the Settings window.</summary>
    [ObservableProperty]
    private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckNowCommand), nameof(UpdateAndRestartCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateAndRestartCommand))]
    private bool _hasUpdate;

    /// <summary>After the main window opens. Quiet: only an available update is shown.</summary>
    public async Task CheckOnStartupAsync()
    {
        if (!_updates.IsInstalled || !CheckOnStartup) return;
        await CheckAsync(quiet: true);
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNow() => CheckAsync(quiet: false);

    private async Task CheckAsync(bool quiet)
    {
        if (!_updates.IsInstalled)
        {
            Status = "Updates work in the installed app; this copy runs from a build folder.";
            if (!quiet) BannerText = Status;
            return;
        }
        IsBusy = true;
        Status = "Checking for updates…";
        try
        {
            _available = await _updates.CheckAsync();
            HasUpdate = _available is not null;
            Status = _available is { } update
                ? $"Trellis {update.TargetFullRelease.Version} is available."
                : $"You have the latest version ({_updates.Version}).";
            if (HasUpdate || !quiet) BannerText = Status;
        }
#pragma warning disable CA1031 // Offline, GitHub down, rate-limited: say so; never crash over an update check.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Status = $"Couldn't check for updates: {ex.Message}";
            if (!quiet) BannerText = Status;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanUpdate() => HasUpdate && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateAndRestart()
    {
        if (_available is not { } update) return;
        IsBusy = true;
        try
        {
            await _updates.DownloadAsync(update, percent => Avalonia.Threading.Dispatcher.UIThread.Post(
                () => BannerText = $"Downloading Trellis {update.TargetFullRelease.Version}… {percent} %"));
            BannerText = "Restarting to finish the update…";
            _updates.ApplyAfterExit(update);
            RequestExit?.Invoke();
        }
#pragma warning disable CA1031 // A failed download leaves the current version running; say why.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            BannerText = Status = $"The update couldn't be downloaded: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Dismiss() => BannerText = null;
}
