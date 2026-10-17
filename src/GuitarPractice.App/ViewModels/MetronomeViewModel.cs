using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

public sealed record SubdivisionOption(Subdivision Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// "Practice with metronome": the practice item the metronome is playing for. <see cref="Reached"/> gets the tempo heard
/// last when playback stops and returns a line for the metronome window ("152 BPM → in the log").
/// </summary>
public sealed record MetronomeLink(string Title, int? StartBpm, int? TargetBpm, Subdivision? Subdivision, Func<int, string> Reached);

/// <summary>
/// Tools → Metronome. All timing (including gap training and the tempo trainer) lives in <see cref="MetronomeEngine"/>
/// on the audio thread; this view model publishes settings, remembers them between sessions, and turns the engine's
/// tick reports into beat lights, offset by the output latency so a light flashes when its click is heard.
/// </summary>
public sealed partial class MetronomeViewModel : ObservableObject, IDisposable
{
    public const string SettingsKey = "metronome";

    private static readonly TimeSpan FlashLength = TimeSpan.FromMilliseconds(110);
    private static readonly TimeSpan TapReset = TimeSpan.FromSeconds(2);

    private readonly IAudioOutput _output;
    private readonly SettingsStore _settingsStore;
    private readonly DispatcherTimer _uiTimer;
    private readonly Queue<MetronomeTick> _pendingTicks = new();
    private readonly List<long> _taps = [];
    private readonly bool _loading;
    private MetronomeEngine? _engine;
    private long _flashOffAt;

    public MetronomeViewModel(IAudioDevices devices, SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settingsStore);
        _output = devices.CreateOutput(); // owned: disposed with the window
        _settingsStore = settingsStore;
        _uiTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Render, (_, _) => PumpTicks());
        _uiTimer.Stop(); // that constructor starts the timer; it only runs while playing

        _loading = true;
        Apply((settingsStore.Get<MetronomeSettings>(SettingsKey) ?? new MetronomeSettings()).Normalized());
        _loading = false;
        RebuildBeatLights();
    }

    // ---- Options ----

    public IReadOnlyList<int> BeatsPerBarOptions { get; } = Enumerable.Range(1, 12).ToList();

    public IReadOnlyList<SubdivisionOption> SubdivisionOptions { get; } =
    [
        new(Subdivision.Quarters, "♩ Quarter notes"),
        new(Subdivision.Eighths, "♪♪ Eighth notes"),
        new(Subdivision.Triplets, "3 Triplets"),
        new(Subdivision.Sixteenths, "♬♬ Sixteenth notes"),
    ];

    public IReadOnlyList<int> GapBarOptions { get; } = Enumerable.Range(1, 8).ToList();

    public IReadOnlyList<double> TrainerStepOptions { get; } = [1, 2, 3, 4, 5, 10];

    public IReadOnlyList<int> TrainerEveryOptions { get; } = [1, 2, 4, 8, 16];

    public ObservableCollection<BeatLightViewModel> BeatLights { get; } = [];

    // ---- Settings ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BpmText))]
    private double _bpm = 120;

    [ObservableProperty]
    private int _beatsPerBar = 4;

    [ObservableProperty]
    private SubdivisionOption? _selectedSubdivision;

    [ObservableProperty]
    private bool _accentFirstBeat = true;

    [ObservableProperty]
    private double _volume = 100; // percent, 0–200

    [ObservableProperty]
    private bool _gapEnabled;

    [ObservableProperty]
    private int _gapPlayBars = 3;

    [ObservableProperty]
    private int _gapMuteBars = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveText))]
    private bool _trainerEnabled;

    [ObservableProperty]
    private double _trainerStepBpm = 2;

    [ObservableProperty]
    private int _trainerEveryBars = 4;

    [ObservableProperty]
    private decimal? _trainerTargetBpm = 160;

    // ---- Live state ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartStopText), nameof(LiveText))]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveText))]
    private double _currentBpm;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LiveText))]
    private bool _isMutedBar;

    [ObservableProperty]
    private string _status = "Space: start/stop · ↑/↓: ±1 BPM · PgUp/PgDn: ±5 · T: tap";

    [ObservableProperty]
    private string? _error;

    public string BpmText => Math.Round(Bpm).ToString(CultureInfo.InvariantCulture);

    public string StartStopText => IsPlaying ? "■ Stop" : "▶ Start";

    /// <summary>"Now 128 BPM · muted bar" while playing with the trainer or gap training on.</summary>
    public string LiveText
    {
        get
        {
            if (!IsPlaying || CurrentBpm <= 0) return "";
            var parts = new List<string>();
            if (TrainerEnabled) parts.Add($"Now {CurrentBpm:0.#} BPM");
            if (IsMutedBar) parts.Add("silent bar: keep time!");
            return string.Join(" · ", parts);
        }
    }

    private MetronomeSettings Settings => new MetronomeSettings(
        Math.Round(Bpm),
        BeatsPerBar,
        SelectedSubdivision?.Value ?? Subdivision.Quarters,
        AccentFirstBeat,
        (float)(Volume / 100.0),
        GapEnabled,
        GapPlayBars,
        GapMuteBars,
        TrainerEnabled,
        TrainerStepBpm,
        TrainerEveryBars,
        (double)(TrainerTargetBpm ?? 160)).Normalized();

    private void Apply(MetronomeSettings s)
    {
        Bpm = s.Bpm;
        BeatsPerBar = s.BeatsPerBar;
        SelectedSubdivision = SubdivisionOptions.FirstOrDefault(o => o.Value == s.Subdivision) ?? SubdivisionOptions[0];
        AccentFirstBeat = s.AccentFirstBeat;
        Volume = Math.Round(s.Volume * 100);
        GapEnabled = s.GapEnabled;
        GapPlayBars = s.GapPlayBars;
        GapMuteBars = s.GapMuteBars;
        TrainerEnabled = s.TrainerEnabled;
        TrainerStepBpm = s.TrainerStepBpm;
        TrainerEveryBars = s.TrainerEveryBars;
        TrainerTargetBpm = (decimal)s.TrainerTargetBpm;
    }

    partial void OnBpmChanged(double value)
    {
        var clamped = Math.Clamp(Math.Round(value), MetronomeSettings.MinBpm, 300);
        if (clamped != value)
        {
            Bpm = clamped; // re-enters with the rounded value
            return;
        }
        Publish();
    }

    partial void OnBeatsPerBarChanged(int value)
    {
        RebuildBeatLights();
        Publish();
    }

    partial void OnAccentFirstBeatChanged(bool value)
    {
        RebuildBeatLights();
        Publish();
    }

    partial void OnSelectedSubdivisionChanged(SubdivisionOption? value) => Publish();
    partial void OnVolumeChanged(double value) => Publish();
    partial void OnGapEnabledChanged(bool value) => Publish();
    partial void OnGapPlayBarsChanged(int value) => Publish();
    partial void OnGapMuteBarsChanged(int value) => Publish();
    partial void OnTrainerEnabledChanged(bool value) => Publish();
    partial void OnTrainerStepBpmChanged(double value) => Publish();
    partial void OnTrainerEveryBarsChanged(int value) => Publish();
    partial void OnTrainerTargetBpmChanged(decimal? value) => Publish();

    private void Publish()
    {
        if (_loading) return;
        if (_engine is not null) _engine.Settings = Settings;
    }

    // ---- Link to a practice item ----

    private MetronomeLink? _link;

    /// <summary>"Playing for: Alternate picking · Trills · 16ths", or null when not linked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked))]
    private string? _linkTitle;

    /// <summary>What happened to the last tempo reached ("152 BPM → in the log").</summary>
    [ObservableProperty]
    private string? _linkStatus;

    public bool IsLinked => LinkTitle is not null;

    /// <summary>
    /// Plays for a practice item: its start tempo, note value and target (as the trainer's target; whether the trainer
    /// ramps stays the user's choice), then starts. A link that was playing is stopped first, so it gets its tempo.
    /// </summary>
    public void Link(MetronomeLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (IsPlaying) StopPlayback();
        _link = link;
        LinkTitle = link.Title;
        LinkStatus = link.TargetBpm is int t ? $"Target {t} BPM (the trainer ramps toward it when on)" : null;
        if (link.StartBpm is int start) Bpm = start;
        if (link.Subdivision is { } sub) SelectedSubdivision = SubdivisionOptions.First(o => o.Value == sub);
        if (link.TargetBpm is int target) TrainerTargetBpm = target;
        StartPlayback();
    }

    [RelayCommand]
    private void Unlink()
    {
        _link = null;
        LinkTitle = null;
        LinkStatus = null;
    }

    private void ReportReached()
    {
        if (_link is null || CurrentBpm <= 0) return; // nothing heard yet
        var bpm = (int)Math.Round(CurrentBpm);
        try
        {
            LinkStatus = _link.Reached(bpm);
        }
#pragma warning disable CA1031 // Logging the tempo must never break the metronome.
        catch (Exception ex)
        {
            LinkStatus = $"Reached {bpm} BPM (couldn't add it to the log: {ex.Message})";
        }
#pragma warning restore CA1031
    }

    // ---- Commands ----

    [RelayCommand]
    private void StartStop()
    {
        if (IsPlaying) StopPlayback();
        else StartPlayback();
    }

    /// <summary>"+1", "-5"...</summary>
    [RelayCommand]
    private void AdjustBpm(string? delta)
    {
        if (int.TryParse(delta, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d)) Bpm += d;
    }

    /// <summary>Tap tempo: average of the last four intervals; a 2 s pause starts over.</summary>
    [RelayCommand]
    private void Tap()
    {
        var now = Stopwatch.GetTimestamp();
        if (_taps.Count > 0 && Stopwatch.GetElapsedTime(_taps[^1], now) > TapReset) _taps.Clear();
        _taps.Add(now);
        if (_taps.Count > 5) _taps.RemoveAt(0);
        if (_taps.Count < 2) return;

        var seconds = Stopwatch.GetElapsedTime(_taps[0], now).TotalSeconds / (_taps.Count - 1);
        if (seconds > 0) Bpm = 60.0 / seconds;
    }

    // ---- Playback ----

    private void StartPlayback()
    {
        Error = null;
        SaveSettings();
        try
        {
            var rate = _output.GetPreferredSampleRate();
            if (_engine?.SampleRate != rate) _engine = new MetronomeEngine(rate);
            _engine.Settings = Settings;
            while (_engine.TryDequeueTick(out _)) { }
            _pendingTicks.Clear();
            _engine.Start();
            _output.Start(new AudioStreamOptions(SampleRate: rate, FramesPerBuffer: 256), _engine.Render);
        }
#pragma warning disable CA1031 // Any audio failure (no device, driver error, missing native lib) is shown, not thrown.
        catch (Exception ex)
        {
            _engine?.Stop();
            Error = $"Couldn't start audio: {ex.Message}";
            return;
        }
#pragma warning restore CA1031

        CurrentBpm = 0;
        IsMutedBar = false;
        IsPlaying = true;
        var device = (_output as PortAudioOutput)?.DeviceName;
        Status = $"{device ?? "Default output"} · {_output.SampleRate / 1000.0:0.#} kHz · latency {_output.OutputLatency.TotalMilliseconds:0} ms";
        _uiTimer.Start();
    }

    private void StopPlayback()
    {
        _engine?.Stop();
        _output.Stop();
        _uiTimer.Stop();
        IsPlaying = false;
        ReportReached();
        IsMutedBar = false;
        foreach (var light in BeatLights) light.IsLit = false;
    }

    /// <summary>UI thread, ~100 Hz: light the beat whose click is being heard now.</summary>
    private void PumpTicks()
    {
        if (_engine is null) return;
        while (_engine.TryDequeueTick(out var tick))
            if (tick.Kind != ClickKind.Subdivision) _pendingTicks.Enqueue(tick);

        var now = Stopwatch.GetTimestamp();
        var latency = (long)(_output.OutputLatency.TotalSeconds * Stopwatch.Frequency);
        MetronomeTick? due = null;
        while (_pendingTicks.TryPeek(out var next) && next.Timestamp + latency <= now)
            due = _pendingTicks.Dequeue();

        if (due is { } beat)
        {
            CurrentBpm = beat.Bpm;
            IsMutedBar = beat.Muted;
            for (var i = 0; i < BeatLights.Count; i++)
            {
                BeatLights[i].IsLit = i == beat.Beat;
                BeatLights[i].IsMuted = beat.Muted;
            }
            _flashOffAt = now + (long)(FlashLength.TotalSeconds * Stopwatch.Frequency);
        }
        else if (_flashOffAt != 0 && now >= _flashOffAt)
        {
            foreach (var light in BeatLights) light.IsLit = false;
            _flashOffAt = 0;
        }
    }

    private void RebuildBeatLights()
    {
        BeatLights.Clear();
        for (var i = 0; i < BeatsPerBar; i++)
            BeatLights.Add(new BeatLightViewModel(i + 1, isAccent: i == 0 && AccentFirstBeat));
    }

    private void SaveSettings()
    {
        try
        {
            _settingsStore.Set(SettingsKey, Settings);
        }
#pragma warning disable CA1031 // Losing a preference must never break the metronome.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving metronome settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    public void Dispose()
    {
        if (IsPlaying) StopPlayback();
        _uiTimer.Stop();
        SaveSettings();
        _output.Dispose();
    }
}

public sealed partial class BeatLightViewModel(int number, bool isAccent) : ObservableObject
{
    public int Number { get; } = number;
    public bool IsAccent { get; } = isAccent;

    [ObservableProperty]
    private bool _isLit;

    /// <summary>Gap training: this bar is silent; the light still moves (hollow) so you can check your time.</summary>
    [ObservableProperty]
    private bool _isMuted;
}
