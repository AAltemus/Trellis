using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;
using GuitarPractice.Infrastructure;
using GuitarPractice.Infrastructure.Persistence;

namespace GuitarPractice.App.ViewModels;

/// <summary>Remembered between sessions (AppSettings key "recorder").</summary>
public sealed record RecorderSettings(
    string? DeviceName = null, int Channel = 0, bool CountIn = false, double CountInBpm = 0, double Volume = 100);

/// <summary>A WAV file in the recordings folder.</summary>
public sealed partial class TakeItemViewModel(string path, TimeSpan duration) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    private string _path = path;

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public TimeSpan Duration { get; } = duration;
    public string DurationText => RecorderViewModel.Format(Duration.TotalSeconds);
}

/// <summary>
/// Tools → Recorder: record a take from the chosen input, play it back, loop an A–B region to play along (with an
/// optional count-in), keep takes as WAV files. Recording runs in <see cref="TakeRecorder"/>, playback in
/// <see cref="LoopPlayer"/>; this view model polls them ~30×/s for the meters and the playhead.
/// Hearing yourself while playing along is the interface's job (direct monitoring), not the app's.
/// </summary>
public sealed partial class RecorderViewModel : ObservableObject, IDisposable
{
    public const string SettingsKey = "recorder";
    private const int WaveformColumns = 1_200;

    private readonly IAudioInput _input;
    private readonly IAudioOutput _output;
    private readonly SettingsStore _settingsStore;
    private readonly TakeRecorder _recorder;
    private readonly DispatcherTimer _poll;
    private readonly bool _loading;
    private LoopPlayer? _player;
    private AudioTake? _take;
    private bool _outputRunning;

    public RecorderViewModel(IAudioDevices devices, SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settingsStore);
        _settingsStore = settingsStore;
        _input = devices.CreateInput();
        _output = devices.CreateOutput();
        _recorder = new TakeRecorder(_input);
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Refresh());
        _poll.Stop(); // that constructor starts the timer; it runs only while recording or playing

        _loading = true;
        var saved = settingsStore.Get<RecorderSettings>(SettingsKey) ?? new RecorderSettings();
        try
        {
            foreach (var d in _input.Devices) Devices.Add(d);
        }
#pragma warning disable CA1031 // No audio subsystem: show the error, keep the window usable.
        catch (Exception ex)
        {
            Error = $"Couldn't list audio inputs: {ex.Message}";
        }
#pragma warning restore CA1031
        SelectedDevice = Devices.FirstOrDefault(d => d.Name == saved.DeviceName) ?? Devices.FirstOrDefault(d => d.IsDefault) ?? Devices.FirstOrDefault();
        SelectedChannel = ChannelOptions.FirstOrDefault(c => c.Channel == saved.Channel) ?? ChannelOptions[0];
        IsCountInEnabled = saved.CountIn;
        var metronomeBpm = settingsStore.Get<MetronomeSettings>(MetronomeViewModel.SettingsKey)?.Bpm ?? 100;
        CountInBpm = (decimal)Math.Round(saved.CountInBpm > 0 ? saved.CountInBpm : metronomeBpm);
        Volume = Math.Clamp(saved.Volume, 0, 200);
        _loading = false;

        RefreshTakes();
        SelectedTake = Takes.FirstOrDefault();
    }

    public static string RecordingsDirectory => Path.Combine(AppPaths.DataDirectory, "recordings");

    // ---- Input ----

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];

    public IReadOnlyList<ChannelOption> ChannelOptions { get; } =
        [new(0, "Both channels (mix)"), new(1, "Channel 1 only"), new(2, "Channel 2 only")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStereoDevice))]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ChannelOption? _selectedChannel;

    public bool IsStereoDevice => SelectedDevice is { MaxChannels: >= 2 };

    // ---- Recording ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RecordButtonText))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StopCommand))]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordTimeText = "";

    [ObservableProperty]
    private double _inputLevel;

    public string RecordButtonText => IsRecording ? "■ Stop recording" : "● Record";

    // ---- Takes ----

    public ObservableCollection<TakeItemViewModel> Takes { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTake))]
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StopCommand), nameof(RenameCommand), nameof(AskDeleteCommand))]
    private TakeItemViewModel? _selectedTake;

    public bool HasTake => SelectedTake is not null && _take is not null;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RenameCommand))]
    private string _renameText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteConfirmOpen), nameof(DeleteConfirmText))]
    private TakeItemViewModel? _pendingDelete;

    public bool IsDeleteConfirmOpen => PendingDelete is not null;

    public string DeleteConfirmText => PendingDelete is { } take ? $"Delete the take “{take.Name}”? The WAV file is removed." : "";

    // ---- Playback ----

    [ObservableProperty]
    private WaveformPeaks? _peaks;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayButtonText))]
    private bool _isPlaying;

    [ObservableProperty]
    private double _positionFraction;

    [ObservableProperty]
    private string _positionText = "";

    [ObservableProperty]
    private bool _isLoopEnabled;

    [ObservableProperty]
    private double _loopStart;

    [ObservableProperty]
    private double _loopEnd = 1;

    [ObservableProperty]
    private string _loopText = "Drag across the waveform to choose a loop";

    [ObservableProperty]
    private bool _isCountInEnabled;

    [ObservableProperty]
    private decimal? _countInBpm = 100;

    [ObservableProperty]
    private double _volume = 100;

    [ObservableProperty]
    private string _status = "Tip: turn up the interface's direct-monitor mix to hear yourself with no delay while you play along.";

    [ObservableProperty]
    private string? _error;

    public string PlayButtonText => IsPlaying ? "❚❚ Pause" : "▶ Play";

    // ---- Changes ----

    partial void OnSelectedTakeChanged(TakeItemViewModel? value) => LoadSelectedTake();

    partial void OnIsLoopEnabledChanged(bool value) => PublishPlayer();

    partial void OnLoopStartChanged(double value) => LoopRegionChanged();

    partial void OnLoopEndChanged(double value) => LoopRegionChanged();

    partial void OnIsCountInEnabledChanged(bool value) => PublishPlayer();

    partial void OnCountInBpmChanged(decimal? value) => PublishPlayer();

    partial void OnVolumeChanged(double value) => PublishPlayer();

    private void LoopRegionChanged()
    {
        if (_loading || _take is null) return;
        var seconds = _take.Duration.TotalSeconds;
        var length = (LoopEnd - LoopStart) * seconds;
        if (length >= PlayerSettings.MinimumLoop.TotalSeconds && !(LoopStart <= 0 && LoopEnd >= 1)) IsLoopEnabled = true;
        LoopText = $"Loop {Format(LoopStart * seconds)} – {Format(LoopEnd * seconds)} ({length:0.00} s)";
        PublishPlayer();
    }

    private void PublishPlayer()
    {
        if (_loading || _player is null || _take is null) return;
        var seconds = _take.Duration.TotalSeconds;
        _player.Settings = new PlayerSettings(
            (float)(Volume / 100.0), IsLoopEnabled, LoopStart * seconds, LoopEnd * seconds,
            IsCountInEnabled, (double)(CountInBpm ?? 100), 4);
    }

    // ---- Commands ----

    [RelayCommand]
    private void ToggleRecord()
    {
        if (IsRecording) StopRecording();
        else StartRecording();
    }

    private void StartRecording()
    {
        Error = null;
        Pause();
        try
        {
            _recorder.Start(SelectedDevice?.Id, IsStereoDevice ? SelectedChannel?.Channel ?? 0 : 0);
        }
#pragma warning disable CA1031 // Any audio failure is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't open the input: {ex.Message}";
            return;
        }
#pragma warning restore CA1031
        IsRecording = true;
        Status = $"Recording from {(_input as PortAudioInput)?.DeviceName ?? SelectedDevice?.Name} · {_recorder.SampleRate / 1000.0:0.#} kHz";
        _poll.Start();
    }

    private void StopRecording()
    {
        var take = _recorder.Stop();
        IsRecording = false;
        InputLevel = 0;
        RecordTimeText = "";
        if (take.Samples.Length == 0)
        {
            Status = "Nothing was recorded.";
            return;
        }

        try
        {
            Directory.CreateDirectory(RecordingsDirectory);
            var path = UniquePath($"Take {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
            WavFile.Write(path, take);
            var item = new TakeItemViewModel(path, take.Duration);
            Takes.Insert(0, item);
            SelectedTake = item;
            Status = $"Saved “{item.Name}” ({item.DurationText}){(_recorder.DroppedSamples > 0 ? $" · warning: {_recorder.DroppedSamples} samples dropped" : "")}";
        }
#pragma warning disable CA1031 // Report a failed save; don't lose the window.
        catch (Exception ex)
        {
            Error = $"Couldn't save the take: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    private bool CanPlay() => !IsRecording && HasTake;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void PlayPause()
    {
        if (IsPlaying)
        {
            Pause();
            return;
        }
        if (!EnsureOutput() || _player is null) return; // EnsureOutput creates the player on first play
        PublishPlayer();
        _player.Play();
        IsPlaying = true;
        _poll.Start();
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Stop()
    {
        Pause();
        _player?.Seek(IsLoopEnabled && _take is not null ? LoopStart * _take.Duration.TotalSeconds : 0);
        Refresh();
    }

    [RelayCommand]
    private void Seek(double fraction)
    {
        if (_player is null || _take is null) return;
        _player.Seek(Math.Clamp(fraction, 0, 1) * _take.Duration.TotalSeconds);
        Refresh();
    }

    [RelayCommand]
    private void ClearLoop()
    {
        IsLoopEnabled = false;
        LoopStart = 0;
        LoopEnd = 1;
        LoopText = "Drag across the waveform to choose a loop";
    }

    private bool CanRename() => SelectedTake is not null && !string.IsNullOrWhiteSpace(RenameText) && RenameText.Trim() != SelectedTake.Name;

    [RelayCommand(CanExecute = nameof(CanRename))]
    private void Rename()
    {
        if (SelectedTake is not { } take) return;
        var name = string.Concat(RenameText.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        try
        {
            var target = UniquePath(name);
            File.Move(take.Path, target);
            take.Path = target;
            RenameText = take.Name;
            Status = $"Renamed to “{take.Name}”.";
        }
#pragma warning disable CA1031 // Report, don't crash.
        catch (Exception ex)
        {
            Error = $"Couldn't rename: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    private bool CanAskDelete() => SelectedTake is not null;

    [RelayCommand(CanExecute = nameof(CanAskDelete))]
    private void AskDelete() => PendingDelete = SelectedTake;

    [RelayCommand]
    private void CancelDelete() => PendingDelete = null;

    [RelayCommand]
    private void ConfirmDelete()
    {
        if (PendingDelete is not { } take) return;
        PendingDelete = null;
        if (ReferenceEquals(take, SelectedTake))
        {
            Pause();
            SelectedTake = null;
        }
        try
        {
            File.Delete(take.Path);
            Takes.Remove(take);
            SelectedTake ??= Takes.FirstOrDefault();
            Status = $"Deleted “{take.Name}”.";
        }
#pragma warning disable CA1031 // Report, don't crash.
        catch (Exception ex)
        {
            Error = $"Couldn't delete: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    // ---- Internals ----

    private void LoadSelectedTake()
    {
        Pause();
        _take = null;
        Peaks = null;
        if (SelectedTake is { } item)
        {
            try
            {
                _take = WavFile.Read(item.Path);
                Peaks = WaveformPeaks.Compute(_take.Samples, WaveformColumns);
                RenameText = item.Name;
            }
#pragma warning disable CA1031 // A broken file is reported, not fatal.
            catch (Exception ex)
            {
                Error = $"Couldn't open “{item.Name}”: {ex.Message}";
            }
#pragma warning restore CA1031
        }

        _player?.Load(_take);
        ClearLoop();
        OnPropertyChanged(nameof(HasTake));
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        Refresh();
    }

    /// <summary>Opens the output stream on first play and keeps it open (silent while paused) until the window closes.</summary>
    private bool EnsureOutput()
    {
        if (_outputRunning) return true;
        try
        {
            var rate = _output.GetPreferredSampleRate();
            if (_player?.OutputRate != rate)
            {
                _player = new LoopPlayer(rate);
                _player.Load(_take);
            }
            _output.Start(new AudioStreamOptions(SampleRate: rate, FramesPerBuffer: 256), _player.Render);
            _outputRunning = true;
            return true;
        }
#pragma warning disable CA1031 // Any audio failure is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't start playback: {ex.Message}";
            return false;
        }
#pragma warning restore CA1031
    }

    private void Pause()
    {
        _player?.Pause();
        IsPlaying = false;
    }

    /// <summary>UI thread, ~30×/s while recording or playing.</summary>
    private void Refresh()
    {
        if (IsRecording)
        {
            var peak = _recorder.PeakDb;
            InputLevel = double.IsFinite(peak) ? Math.Clamp((peak + 60) / 60, 0, 1) : 0;
            RecordTimeText = $"{Format(_recorder.Elapsed.TotalSeconds)}{(_recorder.LimitReached ? " (10 min limit reached)" : "")}";
        }

        if (_take is { } take)
        {
            var seconds = _player?.PositionSeconds ?? 0;
            var duration = take.Duration.TotalSeconds;
            PositionFraction = duration > 0 ? Math.Clamp(seconds / duration, 0, 1) : 0;
            var countIn = _player is { IsCountingIn: true } ? "  · count-in…" : "";
            var loops = _player is { LoopCount: > 0 } p && IsLoopEnabled ? $"  · loop {p.LoopCount + 1}" : "";
            PositionText = $"{Format(seconds)} / {Format(duration)}{countIn}{loops}";
            if (IsPlaying && _player is { IsPlaying: false }) IsPlaying = false; // reached the end
        }
        else
        {
            PositionFraction = 0;
            PositionText = "";
        }

        if (!IsRecording && !IsPlaying) _poll.Stop();
    }

    private void RefreshTakes()
    {
        Takes.Clear();
        if (!Directory.Exists(RecordingsDirectory)) return;
        foreach (var file in new DirectoryInfo(RecordingsDirectory).GetFiles("*.wav").OrderByDescending(f => f.LastWriteTimeUtc))
        {
            try
            {
                Takes.Add(new TakeItemViewModel(file.FullName, WavFile.ReadInfo(file.FullName).Duration));
            }
            catch (InvalidDataException)
            {
                // Not a WAV we can read; leave it out of the list.
            }
            catch (IOException)
            {
            }
        }
    }

    private static string UniquePath(string name)
    {
        var path = Path.Combine(RecordingsDirectory, name + ".wav");
        for (var n = 2; File.Exists(path); n++) path = Path.Combine(RecordingsDirectory, $"{name} ({n}).wav");
        return path;
    }

    internal static string Format(double seconds)
    {
        seconds = Math.Max(0, seconds);
        var m = (int)(seconds / 60);
        return $"{m}:{(seconds - m * 60).ToString("00.0", CultureInfo.InvariantCulture)}";
    }

    public void Dispose()
    {
        _poll.Stop();
        if (IsRecording) StopRecording(); // closing mid-take keeps what was recorded
        _player?.Pause();
        _output.Stop();
        _recorder.Dispose();
        _output.Dispose();
        _input.Dispose();
        try
        {
            _settingsStore.Set(SettingsKey, new RecorderSettings(
                SelectedDevice?.Name, SelectedChannel?.Channel ?? 0, IsCountInEnabled, (double)(CountInBpm ?? 100), Volume));
        }
#pragma warning disable CA1031 // Losing a preference must never break closing the window.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving recorder settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }
}
