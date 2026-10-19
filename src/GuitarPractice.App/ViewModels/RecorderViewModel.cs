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
    string? DeviceName = null, int Channel = 0, bool CountIn = false, double CountInBpm = 0, double Volume = 100, double Speed = 100);

/// <summary>Opening the Recorder for a practice item: new takes attach to it (block, day, exercise).</summary>
public sealed record RecorderLink(int SubcategoryId, DateOnly Date, string ItemName, string? Exercise);

/// <summary>A WAV file in the recordings folder.</summary>
public sealed partial class TakeItemViewModel(string path, TimeSpan duration) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(FileName))]
    private string _path = path;

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string FileName => System.IO.Path.GetFileName(Path);
    public TimeSpan Duration { get; } = duration;
    public string DurationText => RecorderViewModel.Format(Duration.TotalSeconds);

    /// <summary>"📎 Legato · Oct 18 · 3nps scale", or empty.</summary>
    [ObservableProperty]
    private string _attachText = "";
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

    // Slowed-down copies of _take (by speed %), and what the player plays: _take at 100 %, else a stretched copy.
    // Positions and the loop are fractions of the take, so they mean the same place in either.
    private readonly Dictionary<int, AudioTake> _stretched = [];
    private AudioTake? _playTake;
    private CancellationTokenSource? _stretching;

    public RecorderViewModel(IAudioDevices devices, SettingsStore settingsStore, TakeLinkStore links, LibraryStore library,
        ProgressStore progress, TimeProvider clock, string? recordingsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settingsStore);
        ArgumentNullException.ThrowIfNull(links);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(clock);
        _settingsStore = settingsStore;
        RecordingsDirectory = recordingsDirectory ?? DefaultRecordingsDirectory;
        _links = links;
        _progress = progress;
        _clock = clock;
        foreach (var category in library.LoadLibrary())
            foreach (var s in category.Subcategories)
                _blockNames[s.Id] = s.DisplayName;
        foreach (var category in library.LoadLibrary())
            foreach (var s in category.Subcategories)
                AttachBlocks.Add(new LabeledOption<int>(s.Id, $"{category.Name} · {s.DisplayName}"));
        _duplexDevices = devices;
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
        Speed = Math.Clamp(Math.Round(saved.Speed / 5) * 5, TimeStretch.MinSpeed * 100, TimeStretch.MaxSpeed * 100);
        _loading = false;

        RefreshTakes();
        RefreshAttachTexts();
        SelectedTake = Takes.FirstOrDefault();
        LoadLooperSettings();
    }

    // A different input (or channel): the looper's stream reopens on it next time.
    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value) => ReleaseDuplex();

    partial void OnSelectedChannelChanged(ChannelOption? value) => ReleaseDuplex();

    private void ReleaseDuplex()
    {
        if (_loading) return;
        if (IsLooperRunning) StopLooper();
        _duplex?.Stop();
    }

    /// <summary>Writes a new take into the recordings folder, lists it first and selects it; attaches it to the linked item.</summary>
    private TakeItemViewModel SaveNewTake(AudioTake take, string prefix)
    {
        Directory.CreateDirectory(RecordingsDirectory);
        var path = UniquePath($"{prefix} {DateTime.Now:yyyy-MM-dd HH-mm-ss}");
        WavFile.Write(path, take);
        var item = new TakeItemViewModel(path, take.Duration);
        if (_link is { } link) _links.Set(item.FileName, new TakeLink(link.SubcategoryId, link.Date, link.Exercise));
        Takes.Insert(0, item);
        RefreshAttachTexts();
        SelectedTake = item;
        return item;
    }

    public static string DefaultRecordingsDirectory => Path.Combine(AppPaths.DataDirectory, "recordings");

    /// <summary>Where takes live: the data folder's "recordings" (another folder only for headless checks).</summary>
    public string RecordingsDirectory { get; }

    // ---- Attach takes to a practice item (block + day + exercise) ----

    private readonly TakeLinkStore _links;
    private readonly ProgressStore _progress;
    private readonly TimeProvider _clock;
    private readonly IAudioDevices _duplexDevices;
    private readonly Dictionary<int, string> _blockNames = [];
    private RecorderLink? _link;

    /// <summary>Opened from a practice item: new takes attach to it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink))]
    private string? _linkTitle;

    public bool HasLink => LinkTitle is not null;

    public ObservableCollection<LabeledOption<int>> AttachBlocks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private LabeledOption<int>? _attachBlock;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AttachCommand))]
    private DateTime? _attachDate;

    [ObservableProperty]
    private string? _attachExercise;

    [ObservableProperty]
    private IReadOnlyList<string> _attachSuggestions = [];

    /// <summary>"Attached to Legato · Sat Oct 18 · 3nps scale" or "Not attached to a practice item".</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetachCommand))]
    private string _attachSummary = "";

    [ObservableProperty]
    private bool _isAttached;

    public void OpenFor(RecorderLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        LinkTitle = $"Recording for: {link.ItemName} · {link.Date:ddd MMM d}{(link.Exercise is { } e ? $" · {e}" : "")}";
        if (SelectedTake is null) LoadAttachEditor(null);
    }

    [RelayCommand]
    private void CloseLink()
    {
        _link = null;
        LinkTitle = null;
    }

    /// <summary>Selects a take by file name (e.g. from Progress).</summary>
    public void SelectTake(string fileName)
    {
        if (Takes.FirstOrDefault(t => string.Equals(t.FileName, fileName, StringComparison.OrdinalIgnoreCase)) is { } take)
            SelectedTake = take;
        else Status = $"“{fileName}” isn't in the recordings folder any more.";
    }

    partial void OnAttachBlockChanged(LabeledOption<int>? value) =>
        AttachSuggestions = value is null ? [] : _progress.ExerciseNames(value.Value);

    private bool CanAttach() => SelectedTake is not null && AttachBlock is not null && AttachDate is not null;

    [RelayCommand(CanExecute = nameof(CanAttach))]
    private void Attach()
    {
        if (SelectedTake is not { } take || AttachBlock is not { } block || AttachDate is not { } date) return;
        _links.Set(take.FileName, new TakeLink(block.Value, DateOnly.FromDateTime(date), AttachExercise));
        RefreshAttachTexts();
        LoadAttachEditor(take);
        Status = $"Attached “{take.Name}” to {block.Label}.";
    }

    private bool CanDetach() => IsAttached;

    [RelayCommand(CanExecute = nameof(CanDetach))]
    private void Detach()
    {
        if (SelectedTake is not { } take) return;
        _links.Remove(take.FileName);
        RefreshAttachTexts();
        LoadAttachEditor(take);
        Status = $"“{take.Name}” isn't attached any more.";
    }

    /// <summary>Fills the editor from the take's link, or (not attached) with the linked item / today as a suggestion.</summary>
    private void LoadAttachEditor(TakeItemViewModel? take)
    {
        var link = take is null ? null : _links.Get(take.FileName);
        IsAttached = link is not null;
        AttachSummary = link is null ? "Not attached to a practice item" : $"Attached to {Describe(link)}";
        var blockId = link?.SubcategoryId ?? _link?.SubcategoryId;
        AttachBlock = AttachBlocks.FirstOrDefault(b => b.Value == blockId);
        AttachDate = (link?.Date ?? _link?.Date ?? DateOnly.FromDateTime(_clock.GetLocalNow().DateTime)).ToDateTime(TimeOnly.MinValue);
        AttachExercise = link?.Exercise ?? (link is null ? _link?.Exercise : null);
        DetachCommand.NotifyCanExecuteChanged();
    }

    private string Describe(TakeLink link) =>
        $"{_blockNames.GetValueOrDefault(link.SubcategoryId, "a removed item")} · {link.Date:ddd MMM d}{(link.Exercise is { } e ? $" · {e}" : "")}";

    private void RefreshAttachTexts()
    {
        foreach (var t in Takes) t.AttachText = _links.Get(t.FileName) is { } l ? $"📎 {Describe(l)}" : "";
    }

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
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StopCommand), nameof(LooperStartStopCommand), nameof(RecordLayerCommand), nameof(CalibrateCommand))]
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

    /// <summary>Playback speed in % (50–100), pitch unchanged.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double _speed = 100;

    public string SpeedText => $"{Speed:0} %";

    [ObservableProperty]
    private string? _speedStatus;

    [ObservableProperty]
    private string _status ="Tip: turn up the interface's direct-monitor mix to hear yourself with no delay while you play along.";

    [ObservableProperty]
    private string? _error;

    public string PlayButtonText => IsPlaying ? "❚❚ Pause" : "▶ Play";

    // ---- Changes ----

    partial void OnSelectedTakeChanged(TakeItemViewModel? value)
    {
        LoadSelectedTake();
        LoadAttachEditor(value);
        AttachCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsLoopEnabledChanged(bool value) => PublishPlayer();

    partial void OnLoopStartChanged(double value) => LoopRegionChanged();

    partial void OnLoopEndChanged(double value) => LoopRegionChanged();

    partial void OnIsCountInEnabledChanged(bool value) => PublishPlayer();

    partial void OnCountInBpmChanged(decimal? value) => PublishPlayer();

    partial void OnVolumeChanged(double value) => PublishPlayer();

    partial void OnSpeedChanged(double value)
    {
        if (!_loading) ScheduleSpeed();
    }

    private int SpeedPercent => (int)Math.Round(Speed);

    /// <summary>Plays the take at the chosen speed: at once if that copy is ready, else after stretching it in the background.</summary>
    private void ApplySpeed()
    {
        _stretching?.Cancel();
        if (_take is not { } take) return;
        var percent = SpeedPercent;
        if (percent >= 100)
        {
            SpeedStatus = null;
            SwitchPlayTake(take);
            return;
        }
        if (_stretched.TryGetValue(percent, out var ready))
        {
            SpeedStatus = null;
            SwitchPlayTake(ready);
            return;
        }

        // Cancelling is routine (the speed changed again, another take): Stretch then returns null, no exception.
        var cts = _stretching = new CancellationTokenSource();
        SpeedStatus = $"Preparing {percent} %…";
        _ = Task.Run(() =>
        {
            var result = TimeStretch.Stretch(take, percent / 100.0,
                p => Dispatcher.UIThread.Post(() => { if (!cts.IsCancellationRequested) SpeedStatus = $"Preparing {percent} %… {p:P0}"; }),
                cts.Token);
            if (result is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                if (cts.IsCancellationRequested || !ReferenceEquals(take, _take)) return;
                _stretched[percent] = result;
                SpeedStatus = null;
                if (SpeedPercent == percent) SwitchPlayTake(result);
            });
        });
    }

    /// <summary>Waits for the slider to settle (250 ms) before preparing a new speed, so dragging doesn't start one per step.</summary>
    private DispatcherTimer? _speedSettle;

    private void ScheduleSpeed()
    {
        _stretching?.Cancel();
        if (_take is not null && (SpeedPercent >= 100 || _stretched.ContainsKey(SpeedPercent)))
        {
            ApplySpeed();                          // ready now: no wait
            return;
        }
        _speedSettle ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) =>
        {
            _speedSettle!.Stop();
            ApplySpeed();
        });
        _speedSettle.Stop();
        SpeedStatus = $"Preparing {SpeedPercent} %…";
        _speedSettle.Start();
    }

    /// <summary>Swaps what the player plays, keeping the place (as a fraction) and playing on if it was.</summary>
    private void SwitchPlayTake(AudioTake playTake)
    {
        if (ReferenceEquals(playTake, _playTake)) return;
        var fraction = PositionFraction;
        var wasPlaying = IsPlaying;
        _playTake = playTake;
        if (_player is null) return;
        _player.Load(playTake);
        PublishPlayer();
        _player.Seek(fraction * playTake.Duration.TotalSeconds);
        if (wasPlaying) _player.Play();
    }

    /// <summary>Seconds of the copy being played (longer than the take when slowed down).</summary>
    private double PlaySeconds => (_playTake ?? _take)?.Duration.TotalSeconds ?? 0;

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
        var seconds = PlaySeconds;
        _player.Settings = new PlayerSettings(
            (float)(Volume / 100.0), IsLoopEnabled, LoopStart * seconds, LoopEnd * seconds,
            IsCountInEnabled, (double)(CountInBpm ?? 100) * SpeedPercent / 100.0, 4); // the count-in slows down too
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
        if (IsLooperRunning) StopLooper();
        _duplex?.Stop();                          // the looper holds the input
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
            var item = SaveNewTake(take, "Take");
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
        _player?.Seek(IsLoopEnabled && _take is not null ? LoopStart * PlaySeconds : 0);
        Refresh();
    }

    [RelayCommand]
    private void Seek(double fraction)
    {
        if (_player is null || _take is null) return;
        _player.Seek(Math.Clamp(fraction, 0, 1) * PlaySeconds);
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
        var name = string.Concat(RenameText.Trim().Select(c => FileNames.IsInvalid(c) ? '_' : c)); // same rule on every OS
        try
        {
            var target = UniquePath(name);
            var oldFileName = take.FileName;
            File.Move(take.Path, target);
            take.Path = target;
            _links.Rename(oldFileName, take.FileName);
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
            _links.Remove(take.FileName);
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
        _stretching?.Cancel();
        _stretched.Clear();
        _take = null;
        _playTake = null;
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

        _playTake = _take;
        _player?.Load(_take);
        ClearLoop();
        OnPropertyChanged(nameof(HasTake));
        PlayPauseCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        Refresh();
        if (SpeedPercent < 100) ApplySpeed(); // prepare the slowed-down copy right away
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
                _player.Load(_playTake ?? _take);
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
            var playing = PlaySeconds;
            PositionFraction = playing > 0 ? Math.Clamp((_player?.PositionSeconds ?? 0) / playing, 0, 1) : 0;
            var duration = take.Duration.TotalSeconds;
            var seconds = PositionFraction * duration;                  // in the take's own time, whatever the speed
            var countIn = _player is { IsCountingIn: true } ? "  · count-in…" : "";
            var loops = _player is { LoopCount: > 0 } p && IsLoopEnabled ? $"  · loop {p.LoopCount + 1}" : "";
            var speed = ReferenceEquals(_playTake, take) || _playTake is null ? "" : $"  · {SpeedPercent} %";
            PositionText = $"{Format(seconds)} / {Format(duration)}{speed}{countIn}{loops}";
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

    private string UniquePath(string name)
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

    private bool _disposed;

    public void Dispose()
    {
        // Disposed by the window on close, then again by the DI container when the app exits (after the database
        // services are gone): only the first call may do anything.
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _stretching?.Cancel();
        _speedSettle?.Stop();
        DisposeLooper();
        if (IsRecording) StopRecording(); // closing mid-take keeps what was recorded
        _player?.Pause();
        _output.Stop();
        _recorder.Dispose();
        _output.Dispose();
        _input.Dispose();
        try
        {
            _settingsStore.Set(SettingsKey, new RecorderSettings(
                SelectedDevice?.Name, SelectedChannel?.Channel ?? 0, IsCountInEnabled, (double)(CountInBpm ?? 100), Volume, Speed));
        }
#pragma warning disable CA1031 // Losing a preference must never break closing the window.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving recorder settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }
}
