using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using GuitarPractice.Audio;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>Remembered between sessions (AppSettings key "tuner"). The device is stored by name: PortAudio indexes shift.</summary>
public sealed record TunerSettings(
    int? TuningId = 1, double A4 = 440, string? DeviceName = null, int Channel = 0, bool BendMode = false, int BendSemitones = 2);

public sealed record BendIntervalOption(int Semitones, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A tuning in the picker; <see cref="Strings"/> null = chromatic.</summary>
public sealed record TuningOption(int? Id, string Name, IReadOnlyList<int>? Strings)
{
    public override string ToString() => Name;
}

public sealed record ChannelOption(int Channel, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Tools → Tuner. Capture + YIN + smoothing run in <see cref="TunerEngine"/> (audio + analysis threads); this view
/// model polls <see cref="TunerEngine.State"/> ~30×/s and formats it. Starts listening as soon as the window opens.
/// Bend mode feeds the same readings to a <see cref="BendTracker"/> (chromatic, no string snapping).
/// </summary>
public sealed partial class TunerViewModel : ObservableObject, IDisposable
{
    public const string SettingsKey = "tuner";
    public const double NeedleHalfWidth = 170; // px from centre to ±50 cents
    private const double InTuneCents = 3;
    private const double BendOnTargetCents = 8;
    private const int BendHistoryLength = 8;

    private readonly IAudioInput _input;
    private readonly SettingsStore _settingsStore;
    private readonly TunerEngine _engine;
    private readonly DispatcherTimer _poll;
    private readonly bool _loading;
    private readonly BendTracker _bend = new();
    private readonly List<double> _bendErrors = [];

    public TunerViewModel(IAudioInput input, LibraryStore library, SettingsStore settingsStore)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(settingsStore);
        _input = input;
        _settingsStore = settingsStore;
        _engine = new TunerEngine(input);
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => Refresh());
        _poll.Stop();

        Tunings = [new TuningOption(null, "Chromatic", null), .. library.LoadTunings().Select(ToOption)];

        _loading = true;
        var saved = settingsStore.Get<TunerSettings>(SettingsKey) ?? new TunerSettings();
        try
        {
            foreach (var d in input.Devices) Devices.Add(d);
        }
#pragma warning disable CA1031 // No audio subsystem: show the error, keep the window usable.
        catch (Exception ex)
        {
            Error = $"Couldn't list audio inputs: {ex.Message}";
        }
#pragma warning restore CA1031
        SelectedDevice = Devices.FirstOrDefault(d => d.Name == saved.DeviceName) ?? Devices.FirstOrDefault(d => d.IsDefault) ?? Devices.FirstOrDefault();
        SelectedChannel = ChannelOptions.FirstOrDefault(c => c.Channel == saved.Channel) ?? ChannelOptions[0];
        SelectedTuning = Tunings.FirstOrDefault(t => t.Id == saved.TuningId) ?? Tunings[Math.Min(1, Tunings.Count - 1)];
        A4 = (decimal)Math.Clamp(saved.A4, 400, 480);
        SelectedBendInterval = BendIntervals.FirstOrDefault(b => b.Semitones == saved.BendSemitones) ?? BendIntervals[1];
        IsBendMode = saved.BendMode;
        _loading = false;

        ApplyTuning();
        _engine.Smoother.A4 = _bend.A4 = (double)(A4 ?? 440);
        _bend.IntervalSemitones = SelectedBendInterval.Semitones;
        StartListening();
    }

    // ---- Pickers ----

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];

    public IReadOnlyList<ChannelOption> ChannelOptions { get; } =
        [new(0, "Both channels (mix)"), new(1, "Channel 1 only"), new(2, "Channel 2 only")];

    public IReadOnlyList<TuningOption> Tunings { get; }

    public ObservableCollection<StringChipViewModel> StringChips { get; } = [];

    public IReadOnlyList<BendIntervalOption> BendIntervals { get; } =
        [new(1, "½ step"), new(2, "Whole step"), new(3, "1½ steps"), new(4, "2 steps")];

    /// <summary>Scored bends, newest first.</summary>
    public ObservableCollection<string> BendHistory { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTunerMode))]
    private bool _isBendMode;

    /// <summary>For the mode radio buttons.</summary>
    public bool IsTunerMode
    {
        get => !IsBendMode;
        set => IsBendMode = !value;
    }

    [ObservableProperty]
    private BendIntervalOption? _selectedBendInterval;

    [ObservableProperty]
    private string _bendTitle = "";

    [ObservableProperty]
    private double _bendProgress;

    [ObservableProperty]
    private string _bendSummary = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStereoDevice))]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ChannelOption? _selectedChannel;

    [ObservableProperty]
    private TuningOption? _selectedTuning;

    [ObservableProperty]
    private decimal? _a4 = 440;

    public bool IsStereoDevice => SelectedDevice is { MaxChannels: >= 2 };

    // ---- Display ----

    [ObservableProperty]
    private string _noteText = "–";

    [ObservableProperty]
    private string _octaveText = "";

    [ObservableProperty]
    private string _centsText = "";

    [ObservableProperty]
    private string _hzText = "Play a single string";

    [ObservableProperty]
    private string _hintText = "";

    [ObservableProperty]
    private double _needleLeft = NeedleHalfWidth;

    [ObservableProperty]
    private bool _hasPitch;

    [ObservableProperty]
    private bool _isInTune;

    [ObservableProperty]
    private double _level;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string? _error;

    // ---- Changes ----

    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value)
    {
        if (!_loading) StartListening();
    }

    partial void OnSelectedChannelChanged(ChannelOption? value)
    {
        if (!_loading && IsStereoDevice) StartListening();
    }

    partial void OnSelectedTuningChanged(TuningOption? value)
    {
        if (!_loading) ApplyTuning();
    }

    partial void OnA4Changed(decimal? value)
    {
        if (!_loading) _engine.Smoother.A4 = _bend.A4 = (double)(value ?? 440);
    }

    partial void OnIsBendModeChanged(bool value)
    {
        if (_loading) return;
        _bend.Reset();
        ApplyTuning();
    }

    partial void OnSelectedBendIntervalChanged(BendIntervalOption? value)
    {
        if (!_loading && value is not null) _bend.IntervalSemitones = value.Semitones;
    }

    private void ApplyTuning()
    {
        var strings = IsBendMode ? null : SelectedTuning?.Strings; // bends are measured chromatically
        _engine.Smoother.Strings = strings;
        StringChips.Clear();
        if (strings is null) return;
        // Guitarists number strings from the high E (1) down; show them low → high like the headstock reads.
        for (var i = 0; i < strings.Count; i++)
            StringChips.Add(new StringChipViewModel(strings.Count - i, Pitch.NoteName(strings[i])));
    }

    private void StartListening()
    {
        Error = null;
        _poll.Stop();
        try
        {
            _engine.Start(SelectedDevice?.Id, SelectedChannel?.Channel ?? 0);
        }
#pragma warning disable CA1031 // Any audio failure (no device, driver error, missing native lib) is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't open the input: {ex.Message}";
            Status = "";
            return;
        }
#pragma warning restore CA1031

        var device = (_input as PortAudioInput)?.DeviceName ?? SelectedDevice?.Name ?? "Default input";
        Status = $"Listening on {device} · {_engine.SampleRate / 1000.0:0.#} kHz";
        _poll.Start();
    }

    /// <summary>UI thread, ~30×/s.</summary>
    private void Refresh()
    {
        var state = _engine.State;
        Level = double.IsFinite(state.LevelDb) ? Math.Clamp((state.LevelDb + 80) / 80, 0, 1) : 0;
        if (IsBendMode)
        {
            RefreshBend(state);
            return;
        }

        foreach (var chip in StringChips) chip.IsActive = state.Reading is { StringIndex: int s } && StringChips.Count - s == chip.Number;

        if (state.Reading is not { } r)
        {
            HasPitch = false;
            IsInTune = false;
            NoteText = "–";
            OctaveText = "";
            CentsText = "";
            HzText = state.LevelDb > _engine.GateDb ? "Listening…" : "Play a single string";
            HintText = "";
            NeedleLeft = NeedleHalfWidth;
            return;
        }

        HasPitch = true;
        NoteText = Pitch.PitchClassName(r.TargetMidi);
        OctaveText = ((int)Math.Floor(r.TargetMidi / 12.0) - 1).ToString(CultureInfo.InvariantCulture);
        CentsText = $"{r.Cents:+0.0;−0.0;0.0} ¢";
        HzText = $"{r.Hz:0.00} Hz";
        IsInTune = Math.Abs(r.Cents) < InTuneCents;
        HintText = IsInTune ? "In tune ✓"
            : r.Cents < -50 ? $"{-r.Cents / 100:0.#} semitones flat: tune up"
            : r.Cents > 50 ? $"{r.Cents / 100:0.#} semitones sharp: tune down"
            : r.Cents < 0 ? "Flat: tune up ▲" : "Sharp: tune down ▼";
        NeedleLeft = NeedleHalfWidth + Math.Clamp(r.Cents, -50, 50) / 50 * NeedleHalfWidth;
    }

    private void RefreshBend(TunerState state)
    {
        var bend = _bend.Update(state.Reading?.Hz, Stopwatch.GetElapsedTime(0));
        if (bend.Completed is { } done) RecordBend(done);

        if (bend.Phase == BendPhase.Idle || bend.ReferenceMidi is not int reference || bend.TargetMidi is not int target)
        {
            HasPitch = false;
            IsInTune = false;
            NoteText = "–";
            OctaveText = "";
            CentsText = "";
            HzText = "";
            BendTitle = $"Play a fretted note, then bend it up a {SelectedBendInterval?.Label.ToLowerInvariant() ?? "whole step"}";
            HintText = "";
            BendProgress = 0;
            NeedleLeft = NeedleHalfWidth;
            return;
        }

        HasPitch = true;
        NoteText = Pitch.PitchClassName(target);
        OctaveText = ((int)Math.Floor(target / 12.0) - 1).ToString(CultureInfo.InvariantCulture);
        BendTitle = $"{Pitch.NoteName(reference)} → {Pitch.NoteName(target)}";
        CentsText = $"{bend.CentsFromTarget:+0;−0;0} ¢";
        HzText = state.Reading is { } r ? $"{r.Hz:0.00} Hz" : "";
        BendProgress = Math.Clamp(bend.Progress, 0, 1.25);
        IsInTune = bend.Phase == BendPhase.Holding && Math.Abs(bend.CentsFromTarget) <= BendOnTargetCents;
        HintText = bend.Phase switch
        {
            BendPhase.Unbent => $"Bend up to {Pitch.NoteName(target)}",
            BendPhase.Bending => "Keep going ▲",
            BendPhase.Holding when IsInTune => "On target, hold it ✓",
            BendPhase.Holding => bend.CentsFromTarget < 0 ? "Hold: a bit more ▲" : "Hold: ease off ▼",
            BendPhase.Overbent => "Too far ▼",
            _ => "",
        };
        NeedleLeft = NeedleHalfWidth + Math.Clamp(bend.CentsFromTarget, -50, 50) / 50 * NeedleHalfWidth;
    }

    private void RecordBend(BendAttempt attempt)
    {
        BendHistory.Insert(0,
            $"{Pitch.NoteName(attempt.ReferenceMidi)} → {Pitch.NoteName(attempt.TargetMidi)}   " +
            $"{attempt.AverageCents:+0;−0;0} ¢   held {attempt.Held.TotalSeconds:0.0} s");
        while (BendHistory.Count > BendHistoryLength) BendHistory.RemoveAt(BendHistory.Count - 1);

        _bendErrors.Insert(0, Math.Abs(attempt.AverageCents));
        if (_bendErrors.Count > BendHistoryLength) _bendErrors.RemoveAt(_bendErrors.Count - 1);
        BendSummary = $"Average error over the last {_bendErrors.Count}: {_bendErrors.Average():0} ¢";
    }

    private static TuningOption ToOption(Domain.Tuning t)
    {
        var strings = new List<int>();
        foreach (var name in t.NoteNames)
            if (Pitch.TryParseNoteName(name, out var midi)) strings.Add(midi);
        return new TuningOption(t.Id, $"{t.Name} ({t.Notes})", strings.Count > 0 ? strings : null);
    }

    public void Dispose()
    {
        _poll.Stop();
        _engine.Dispose();
        try
        {
            _settingsStore.Set(SettingsKey, new TunerSettings(
                SelectedTuning?.Id, (double)(A4 ?? 440), SelectedDevice?.Name, SelectedChannel?.Channel ?? 0,
                IsBendMode, SelectedBendInterval?.Semitones ?? 2));
        }
#pragma warning disable CA1031 // Losing a preference must never break closing the window.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving tuner settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }
}

public sealed partial class StringChipViewModel(int number, string note) : ObservableObject
{
    public int Number { get; } = number;
    public string Note { get; } = note;
    public string Label => $"{Number} · {Note}";

    [ObservableProperty]
    private bool _isActive;
}
