using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.App.Controls;
using GuitarPractice.Audio;
using GuitarPractice.Domain.Practice;
using GuitarPractice.Domain.Rhythm;
using GuitarPractice.Infrastructure;
using GuitarPractice.Infrastructure.Persistence;

namespace GuitarPractice.App.ViewModels;

/// <summary>Remembered between sessions (AppSettings key "rhythmTrainer"). Fields are only appended.</summary>
/// <param name="Own">Your own rhythms (all meters).</param>
/// <param name="OutputName">With <paramref name="OutputFollowsMetronome"/> off: the output by name + driver; null = the
/// system default.</param>
public sealed record RhythmTrainerSettings(
    string Meter = "4/4", string? Pattern = null, double Tempo = 80, int Bars = 2, bool ListenFirst = true,
    bool Click = true, double ClickVolume = 80, bool Drums = true, double DrumVolume = 70,
    bool Score = true, string? DeviceName = null, int Channel = 0, bool Backbeat = false,
    List<OwnRhythm>? Own = null, int RandomLevel = 2, string? OutputName = null, int OutputDriver = -1,
    bool OutputFollowsMetronome = true);

/// <summary>A rhythm you typed in the text syntax (<see cref="RhythmPattern.Parse"/>), for one meter.</summary>
public sealed record OwnRhythm(string Name, string Meter, string Text);

public enum RhythmSource
{
    Library,
    Own,
    Random,
}

/// <summary>A rhythm in the picker: "3 · Gallop", "★ · My riff", "Random · level 3".</summary>
public sealed record RhythmChoice(RhythmPattern Pattern, RhythmSource Source = RhythmSource.Library)
{
    public string Label => Source switch
    {
        RhythmSource.Own => $"★ · {Pattern.Name}{(Pattern.Bars > 1 ? $" ({Pattern.Bars} bars)" : "")}",
        RhythmSource.Random => Pattern.Name,
        _ => $"{Pattern.Level} · {Pattern.Name}",
    };

    public override string ToString() => Label;
}

/// <summary>Opening the Rhythm Trainer for a practice item: Log score goes to that block and day.</summary>
public sealed record RhythmTrainerLink(int SubcategoryId, DateOnly Date, string ItemName);

/// <summary>Where the trainer plays: the Metronome's output, the system default, or a device.</summary>
public sealed record RhythmOutputOption(AudioDeviceInfo? Device, string Label, bool FollowsMetronome = false)
{
    public override string ToString() => Label;
}

/// <summary>
/// Tools → Rhythm Trainer: pick a meter and one of its rhythms (built in, your own typed in the text syntax, or a random
/// one at a level; one bar or a few), a tempo and how many bars to play. A take is a count-in bar, the rhythm played
/// once for you (optional), then your bars over the click and drums (<see cref="RhythmTrainerEngine"/>), on the chosen
/// output (the Metronome's by default).
/// With "Score my playing" the input is recorded on the same full-duplex stream (the Looper's measured latency is
/// compensated), the attacks are found (<see cref="OnsetDetector"/>) and matched to the rhythm (<see cref="RhythmScore"/>):
/// each note is coloured and labelled with how early/late it was on average, and the take gets a score that
/// "Log score" adds to today's notes of a Rhythm block.
/// </summary>
public sealed partial class RhythmTrainerViewModel : ObservableObject, IDisposable
{
    public const string SettingsKey = "rhythmTrainer";
    public const int RhythmCategoryId = 7;
    public const int RhythmReadingId = 808;   // Theory → Rhythm reading
    public const int OddMetersId = 706;       // Rhythm → Odd meters (5/4, 7/8)

    private readonly IAudioDevices _devices;
    private readonly SettingsStore _settings;
    private readonly ProgressStore _progress;
    private readonly TimeProvider _clock;
    private readonly IAudioInput _input;
    private readonly DispatcherTimer _poll;
    private readonly bool _loading;
    private readonly List<OwnRhythm> _own;
    private readonly List<AudioDeviceInfo> _outputDevices = [];
    private IAudioDuplex? _duplex;
    private IAudioOutput? _output;
    private RhythmTrainerEngine? _engine;
    private RhythmChoice? _random;           // the last random rhythm (listed under its meter)
    private RhythmTrainerLink? _link;
    private bool _disposed;
    private int _loggedTakes;

    public RhythmTrainerViewModel(IAudioDevices devices, SettingsStore settings, ProgressStore progress, LibraryStore library, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(clock);
        _devices = devices;
        _settings = settings;
        _progress = progress;
        _clock = clock;
        _input = devices.CreateInput();
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(30), DispatcherPriority.Render, (_, _) => Refresh());
        _poll.Stop();

        _loading = true;
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
        LoadLogBlocks(library);

        var s = settings.Get<RhythmTrainerSettings>(SettingsKey) ?? new RhythmTrainerSettings();
        _own = [.. s.Own ?? []];
        RandomLevel = Math.Clamp(s.RandomLevel, RhythmGenerator.MinLevel, RhythmGenerator.MaxLevel);
        LoadOutputs(devices, s);
        SelectedMeter = Meter.Find(s.Meter) ?? Meter.FourFour;
        FillRhythms(s.Pattern); // also when the meter didn't change (4/4 is the default)
        Tempo = (decimal)Math.Clamp(Math.Round(s.Tempo), MinTempo, MaxTempo);
        SelectedBars = BarOptions.Contains(s.Bars) ? s.Bars : 2;
        ListenFirst = s.ListenFirst;
        Click = s.Click;
        Backbeat = s.Backbeat;
        ClickVolume = Math.Clamp(s.ClickVolume, 0, 100);
        Drums = s.Drums;
        DrumVolume = Math.Clamp(s.DrumVolume, 0, 100);
        ScorePlaying = s.Score;
        SelectedDevice = Devices.FirstOrDefault(d => d.Name == s.DeviceName) ?? Devices.FirstOrDefault(d => d.IsDefault) ?? Devices.FirstOrDefault();
        SelectedChannel = ChannelOptions.FirstOrDefault(c => c.Channel == s.Channel) ?? ChannelOptions[0];
        var looper = settings.Get<LooperSettings>(RecorderViewModel.LooperSettingsKey) ?? new LooperSettings();
        LatencyMs = Math.Clamp(looper.LatencyMs, 0, 300);
        LatencyText = looper.MeasuredOn is { } on
            ? $"Latency {LatencyMs:0.0} ms (measured on {on}; Recorder → Looper → Calibrate to measure again)"
            : "Latency not measured: run Recorder → Looper → Calibrate once, or timings will read late.";
        _loading = false;
        SelectDefaultLogBlock();
        CheckOwn();
    }

    public const double MinTempo = 30;
    public const double MaxTempo = 300;

    // ---- Choices ----

    public IReadOnlyList<Meter> Meters => Meter.All;

    public ObservableCollection<RhythmChoice> Rhythms { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TempoLabel), nameof(CanBackbeat))]
    private Meter _selectedMeter = Meter.FourFour;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Pattern))]
    [NotifyCanExecuteChangedFor(nameof(StartStopCommand))]
    private RhythmChoice? _selectedRhythm;

    public RhythmPattern? Pattern => SelectedRhythm?.Pattern;

    /// <summary>Beats per minute of the meter's note value (♩ in x/4, ♪ in x/8).</summary>
    [ObservableProperty]
    private decimal? _tempo = 80;

    public string TempoLabel => SelectedMeter.Denominator == 8 ? "♪ =" : "♩ =";

    public IReadOnlyList<int> BarOptions { get; } = [1, 2, 4, 8];

    [ObservableProperty]
    private int _selectedBars = 2;

    [ObservableProperty]
    private bool _listenFirst = true;

    [ObservableProperty]
    private bool _click = true;

    [ObservableProperty]
    private double _clickVolume = 80;

    /// <summary>Click only on 2 and 4 after the count-in (meters with an even number of beats; see <see cref="CanBackbeat"/>).</summary>
    [ObservableProperty]
    private bool _backbeat;

    public bool CanBackbeat => RhythmTrainerEngine.CanBackbeat(SelectedMeter.Groups.Count);

    [ObservableProperty]
    private bool _drums = true;

    [ObservableProperty]
    private double _drumVolume = 70;

    // ---- Input ----

    [ObservableProperty]
    private bool _scorePlaying = true;

    public ObservableCollection<AudioDeviceInfo> Devices { get; } = [];

    public IReadOnlyList<ChannelOption> ChannelOptions { get; } =
        [new(0, "Both channels (mix)"), new(1, "Channel 1 only"), new(2, "Channel 2 only")];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStereoDevice))]
    private AudioDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private ChannelOption? _selectedChannel;

    public bool IsStereoDevice => SelectedDevice is { MaxChannels: >= 2 };

    public double LatencyMs { get; }

    public string LatencyText { get; }

    // ---- Take ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartText), nameof(CanChange))]
    [NotifyCanExecuteChangedFor(nameof(NewRandomCommand), nameof(AddOwnCommand), nameof(DeleteOwnCommand))]
    private bool _isRunning;

    public string StartText => IsRunning ? "■ Stop" : "▶ Start";

    public bool CanChange => !IsRunning;

    [ObservableProperty]
    private string _status = "Pick a rhythm and a tempo, then ▶ Start (Space).";

    [ObservableProperty]
    private double _playheadTick = -1;

    [ObservableProperty]
    private IReadOnlyList<NoteMark>? _marks;

    [ObservableProperty]
    private string? _resultText;

    [ObservableProperty]
    private string? _error;

    public ObservableCollection<string> Takes { get; } = [];

    public bool HasTakes => Takes.Count > 0;

    private RhythmResult? _lastResult;
    private int _lastPlayBars;
    private AudioTake? _lastRecording;

    [ObservableProperty]
    private string? _saveStatus;

    private bool HasRecording() => _lastRecording is not null;

    /// <summary>
    /// Saves the last take's input (as heard, latency removed, from 0.1 s before your first bar) as a WAV in the
    /// diagnostics folder, so a take that scored oddly can be replayed through the detector offline.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasRecording))]
    private void SaveRecording()
    {
        if (_lastRecording is not { } take) return;
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            var path = Path.Combine(AppPaths.DiagnosticsDirectory, $"rhythm-{_clock.GetLocalNow():yyyyMMdd-HHmmss}.wav");
            WavFile.Write(path, take);
            SaveStatus = $"Saved to {path}";
        }
#pragma warning disable CA1031 // A failed diagnostic save is reported, never fatal.
        catch (Exception ex)
        {
            SaveStatus = $"Couldn't save it: {ex.Message}";
        }
#pragma warning restore CA1031
    }
    private string? _lastSummary;

    partial void OnSelectedMeterChanged(Meter value)
    {
        FillRhythms(SelectedRhythm?.Pattern.Name);
        SelectDefaultLogBlock();
        CheckOwn();
    }

    /// <summary>The meter's rhythms (built in, easiest first; then yours; then the last random one); keeps
    /// <paramref name="name"/> selected if the meter has one by that name.</summary>
    private void FillRhythms(string? name)
    {
        Rhythms.Clear();
        foreach (var p in RhythmLibrary.For(SelectedMeter).OrderBy(p => p.Level)) Rhythms.Add(new RhythmChoice(p));
        foreach (var own in _own.Where(o => o.Meter == SelectedMeter.Signature))
            if (RhythmPattern.TryParse(own.Name, 0, SelectedMeter, own.Text, out var p, out _)) Rhythms.Add(new RhythmChoice(p!, RhythmSource.Own));
        if (_random is { } random && random.Pattern.Meter.Equals(SelectedMeter)) Rhythms.Add(random);
        SelectedRhythm = Rhythms.FirstOrDefault(r => r.Pattern.Name == name) ?? Rhythms.FirstOrDefault();
    }

    partial void OnSelectedRhythmChanged(RhythmChoice? value)
    {
        Marks = null;
        OnPropertyChanged(nameof(IsOwnSelected));
        DeleteOwnCommand.NotifyCanExecuteChanged();
        IsDeleteConfirmOpen = false;
        // Its text goes into the editor: tweak it and add it under a new name (keep a random one you like).
        if (value is null || _loading) return;
        OwnText = value.Pattern.Text;
        OwnName = value.Source == RhythmSource.Own ? value.Pattern.Name : "";
    }

    // ---- Random rhythms ----

    public IReadOnlyList<int> RandomLevels { get; } =
        [.. Enumerable.Range(RhythmGenerator.MinLevel, RhythmGenerator.MaxLevel - RhythmGenerator.MinLevel + 1)];

    [ObservableProperty]
    private int _randomLevel = 2;

    private bool CanEdit() => !IsRunning;

    /// <summary>A new random rhythm at <see cref="RandomLevel"/> in this meter (replaces the last one).</summary>
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void NewRandom()
    {
        var pattern = RhythmGenerator.Generate(SelectedMeter, RandomLevel, new Random());
        _random = new RhythmChoice(pattern, RhythmSource.Random);
        FillRhythms(pattern.Name);
        SaveSettings();
    }

    // ---- Your own rhythms ----

    public const int MaxOwnNameLength = 40;

    [ObservableProperty]
    private string _ownName = "";

    [ObservableProperty]
    private string _ownText = "";

    /// <summary>"✓ 2 bars, 7 notes to play", or what's wrong with the text.</summary>
    [ObservableProperty]
    private string? _ownStatus;

    [ObservableProperty]
    private bool _ownStatusIsError;

    private RhythmPattern? _ownDraft;

    public bool IsOwnSelected => SelectedRhythm?.Source == RhythmSource.Own;

    partial void OnOwnTextChanged(string value) => CheckOwn();
    partial void OnOwnNameChanged(string value) => CheckOwn();

    /// <summary>Checks the typed rhythm against the meter as you type (no exceptions: <see cref="RhythmPattern.TryParse"/>).</summary>
    private void CheckOwn()
    {
        if (_loading) return;
        _ownDraft = null;
        OwnStatusIsError = false;
        var name = OwnName.Trim();
        if (string.IsNullOrWhiteSpace(OwnText))
            OwnStatus = $"Type a rhythm for {SelectedMeter.Signature}, e.g. q e e [3 e e e] q | h h";
        else if (!RhythmPattern.TryParse(name.Length > 0 ? name : "Your rhythm", 0, SelectedMeter, OwnText, out var pattern, out var error))
        {
            OwnStatus = char.ToUpperInvariant(error![0]) + error[1..];
            OwnStatusIsError = true;
        }
        else
        {
            _ownDraft = pattern;
            var shape = $"✓ {pattern!.Bars} bar{(pattern.Bars == 1 ? "" : "s")}, {pattern.Onsets.Count} note{(pattern.Onsets.Count == 1 ? "" : "s")} to play";
            OwnStatus = name.Length == 0 ? $"{shape}: name it to add it"
                : name.Length > MaxOwnNameLength ? $"{shape}; the name is too long (at most {MaxOwnNameLength} characters)"
                : NameTaken(name) ? $"{shape}; {SelectedMeter.Signature} already has “{name}” (another name adds a new one)"
                : shape;
        }
        AddOwnCommand.NotifyCanExecuteChanged();
    }

    private bool NameTaken(string name) => Rhythms.Any(r => string.Equals(r.Pattern.Name, name, StringComparison.OrdinalIgnoreCase));

    private bool CanAddOwn()
    {
        var name = OwnName.Trim();
        return !IsRunning && _ownDraft is not null && name.Length is > 0 and <= MaxOwnNameLength && !NameTaken(name);
    }

    /// <summary>Adds the typed rhythm to this meter's list (saved) and selects it.</summary>
    [RelayCommand(CanExecute = nameof(CanAddOwn))]
    private void AddOwn()
    {
        if (_ownDraft is not { } draft) return;
        var name = OwnName.Trim();
        _own.Add(new OwnRhythm(name, SelectedMeter.Signature, draft.Text));
        FillRhythms(name);
        SaveSettings();
        OwnStatus = $"Added “{name}” to the {SelectedMeter.Signature} rhythms.";
    }

    /// <summary>Asks first (inline): Delete / Cancel.</summary>
    [ObservableProperty]
    private bool _isDeleteConfirmOpen;

    public string DeleteConfirmText => $"Delete your rhythm “{SelectedRhythm?.Pattern.Name}”?";

    private bool CanDeleteOwn() => IsOwnSelected && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanDeleteOwn))]
    private void DeleteOwn()
    {
        OnPropertyChanged(nameof(DeleteConfirmText));
        IsDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private void ConfirmDeleteOwn()
    {
        IsDeleteConfirmOpen = false;
        if (SelectedRhythm is not { Source: RhythmSource.Own } own) return;
        var name = own.Pattern.Name;
        _own.RemoveAll(o => o.Meter == SelectedMeter.Signature && o.Name == name);
        FillRhythms(null);
        SaveSettings();
        OwnStatus = $"Deleted “{name}”.";
    }

    [RelayCommand]
    private void CancelDeleteOwn() => IsDeleteConfirmOpen = false;

    // ---- Output ----

    public ObservableCollection<RhythmOutputOption> Outputs { get; } = [];

    [ObservableProperty]
    private RhythmOutputOption? _selectedOutput;

    /// <summary>"Same as the Metronome", "System default output", then every output under every driver (fastest first).</summary>
    private void LoadOutputs(IAudioDevices devices, RhythmTrainerSettings s)
    {
        Outputs.Add(new RhythmOutputOption(null, "Same as the Metronome (Tools → Metronome → Output)", FollowsMetronome: true));
        Outputs.Add(new RhythmOutputOption(null, "System default output"));
        try
        {
            using var lister = devices.CreateOutput();
            _outputDevices.AddRange(OutputDevices.Sorted(lister.Devices));
        }
#pragma warning disable CA1031 // No device list: the picker offers the Metronome's and the default.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Listing outputs for the rhythm trainer failed: {ex.Message}");
        }
#pragma warning restore CA1031
        foreach (var d in _outputDevices) Outputs.Add(new RhythmOutputOption(d, OutputDevices.Label(d)));
        var saved = OutputDevices.Find(_outputDevices, s.OutputName, s.OutputDriver);
        SelectedOutput = s.OutputFollowsMetronome ? Outputs[0]
            : saved is not null ? Outputs.First(o => o.Device == saved)
            : s.OutputName is null ? Outputs[1]
            : Outputs[0];                    // that device isn't connected: back to the Metronome's
    }

    partial void OnSelectedOutputChanged(RhythmOutputOption? value)
    {
        if (_loading) return;
        ReleaseStreams();
        SaveSettings();
    }

    /// <summary>The output to open (null = the system default).</summary>
    private string? OutputId() => SelectedOutput is { FollowsMetronome: true }
        ? MetronomeViewModel.SavedOutput(_settings, _outputDevices)?.Id
        : SelectedOutput?.Device?.Id;

    // ---- Link to a practice item ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink))]
    private string? _linkTitle;

    public bool HasLink => LinkTitle is not null;

    /// <summary>Practising for a board item: Log score goes to its block and day (an odd-meters block starts in 7/8).</summary>
    public void OpenFor(RhythmTrainerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        LinkTitle = $"Practising for: {link.ItemName} · {link.Date:ddd MMM d}";
        if (LogBlocks.All(b => b.Value != link.SubcategoryId))
        {
            LogBlocks.Insert(0, new LabeledOption<int>(link.SubcategoryId, link.ItemName));
            OnPropertyChanged(nameof(HasLogBlocks));
        }
        if (link.SubcategoryId == OddMetersId && !SelectedMeter.IsOdd && !IsRunning) SelectedMeter = Meter.SevenEight;
        SelectDefaultLogBlock();
    }

    [RelayCommand]
    private void Unlink()
    {
        _link = null;
        LinkTitle = null;
        SelectDefaultLogBlock();
    }

    partial void OnClickChanged(bool value) => PushLive();
    partial void OnClickVolumeChanged(double value) => PushLive();

    partial void OnBackbeatChanged(bool value)
    {
        // The drums' snare is already on 2 and 4: turning the backbeat click on leaves you the click alone (Drums can go
        // back on). Not when loading saved settings.
        if (value && !_loading) Drums = false;
        PushLive();
    }
    partial void OnDrumsChanged(bool value) => PushLive();
    partial void OnDrumVolumeChanged(double value) => PushLive();

    // A different input: the stream reopens on it next time.
    partial void OnSelectedDeviceChanged(AudioDeviceInfo? value) => ReleaseStreams();
    partial void OnSelectedChannelChanged(ChannelOption? value) => ReleaseStreams();
    partial void OnScorePlayingChanged(bool value) => ReleaseStreams();

    private void PushLive()
    {
        if (_engine is not { } e) return;
        e.ClickEnabled = Click;
        e.Backbeat = Backbeat;
        e.ClickVolume = (float)(ClickVolume / 100.0);
        e.DrumsEnabled = Drums;
        e.DrumVolume = (float)(DrumVolume / 100.0);
    }

    private bool CanStartStop() => IsRunning || Pattern is not null;

    [RelayCommand(CanExecute = nameof(CanStartStop))]
    private void StartStop()
    {
        if (IsRunning)
        {
            StopTake("Stopped.");
            return;
        }
        StartTake();
    }

    private void StartTake()
    {
        if (Pattern is not { } pattern) return;
        Error = null;
        Marks = null;
        try
        {
            var score = ScorePlaying;
            var outputId = OutputId();
            int rate;
            if (score)
            {
                _duplex ??= _devices.CreateDuplex();
                rate = (_duplex as PortAudioDuplex)?.GetPreferredSampleRate(SelectedDevice?.Id, outputId) ?? 48_000;
            }
            else
            {
                _output ??= _devices.CreateOutput();
                rate = _output.GetPreferredSampleRate(outputId);
            }

            var engine = new RhythmTrainerEngine(rate, Plan(pattern));
            engine.LatencySamples = (int)Math.Round(LatencyMs / 1000.0 * rate);
            _engine = engine;
            PushLive();
            if (score)
            {
                _duplex!.Stop();
                _duplex.Start(new AudioStreamOptions(DeviceId: SelectedDevice?.Id, SampleRate: rate, FramesPerBuffer: 256,
                    Channel: IsStereoDevice ? SelectedChannel?.Channel ?? 0 : 0, OutputDeviceId: outputId), engine.Process);
            }
            else
            {
                _output!.Stop();
                _output.Start(new AudioStreamOptions(DeviceId: outputId, SampleRate: rate, FramesPerBuffer: 256),
                    output => engine.Process(ReadOnlySpan<float>.Empty, output));
            }
            engine.Start();
        }
#pragma warning disable CA1031 // Any audio failure is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't start: {ex.Message}";
            _engine = null;
            return;
        }
#pragma warning restore CA1031
        IsRunning = true;
        SaveSettings();
        _poll.Start();
    }

    /// <summary>The take's timeline for <paramref name="pattern"/> at the chosen tempo.</summary>
    private RhythmTrainerPlan Plan(RhythmPattern pattern)
    {
        var meter = pattern.Meter;
        var secondsPerTick = 60.0 / (double)Math.Clamp(Tempo ?? 80, (decimal)MinTempo, (decimal)MaxTempo) / meter.UnitTicks;
        var lengths = new List<int>();
        foreach (var onset in pattern.Onsets)
        {
            // A struck note lasts through any notes tied to it.
            var index = pattern.Notes.ToList().FindIndex(n => n.Start == onset && !n.IsRest);
            var ticks = 0;
            for (var i = index; i < pattern.Notes.Count; i++)
            {
                ticks += pattern.Notes[i].Ticks;
                if (!pattern.Notes[i].TiedToNext) break;
            }
            lengths.Add(ticks);
        }
        return new RhythmTrainerPlan(secondsPerTick, meter.BarTicks, meter.BeatTicks, pattern.Onsets, lengths, ListenFirst, PlayBarsFor(pattern),
            HatTicks: Meter.TicksPerQuarter / 2, PatternBars: pattern.Bars);
    }

    /// <summary>Bars to play: at least the chosen number, and a whole number of times through a multi-bar rhythm.</summary>
    private int PlayBarsFor(RhythmPattern pattern) =>
        Math.Min(RhythmTrainerEngine.MaxPlayBars, (SelectedBars + pattern.Bars - 1) / pattern.Bars * pattern.Bars);

    private void StopTake(string status)
    {
        _poll.Stop();
        _engine?.Stop();
        IsRunning = false;
        PlayheadTick = -1;
        Status = status;
    }

    private void Refresh()
    {
        if (_engine is not { } engine)
        {
            _poll.Stop();
            return;
        }
        var state = engine.State;
        var (bar, tick) = engine.Position;
        var countIn = RhythmTrainerEngine.CountInBars;
        var patternBars = Math.Max(1, engine.Plan.PatternBars);
        var barInPattern = state == RhythmTrainerState.Listen ? bar - countIn : (bar - countIn - engine.ListenBars) % patternBars;
        PlayheadTick = state is RhythmTrainerState.Listen or RhythmTrainerState.Play ? barInPattern * engine.Plan.BarTicks + tick : -1;
        var beat = (Pattern?.Meter.BeatAt((int)tick) ?? 0) + 1;
        switch (state)
        {
            case RhythmTrainerState.CountIn:
                Status = $"Count-in… {beat}";
                break;
            case RhythmTrainerState.Listen:
                Status = "Listen…";
                break;
            case RhythmTrainerState.Play:
                var yours = bar - countIn - engine.ListenBars + 1;
                Status = yours <= engine.PlayBars ? $"Your turn: bar {yours} of {engine.PlayBars}" : "Finishing…";
                break;
            case RhythmTrainerState.Done:
                StopTake(ScorePlaying ? "Done." : "Done. (Turn on “Score my playing” to get a score.)");
                if (ScorePlaying) ScoreTake(engine);
                break;
        }
    }

    private void ScoreTake(RhythmTrainerEngine engine)
    {
        if (Pattern is not { } pattern) return;
        var rate = engine.SampleRate;
        var preRoll = engine.PreRollSamples / (double)rate;
        var secondsPerTick = engine.Plan.SecondsPerTick;
        var barSeconds = pattern.Meter.BarTicks * secondsPerTick;
        var times = engine.PlayBars / pattern.Bars;              // times through the rhythm
        var expected = new List<double>();
        for (var time = 0; time < times; time++)
            foreach (var onset in pattern.Onsets) expected.Add(time * pattern.Length * secondsPerTick + onset * secondsPerTick);

        // Clear attacks anywhere, plus faint ones where a note was due (a quick re-strike on a ringing string).
        var played = OnsetDetector.DetectNear(engine.Recording, rate, [.. expected.Select(e => e + preRoll)], RhythmScore.Window(expected) / 1000)
            .Select(t => t - preRoll)
            .Where(t => t >= -RhythmScore.MaxWindowMs / 1000 && t <= engine.PlayBars * barSeconds)
            .ToList();
        var result = RhythmScore.Score(expected, played);
        _lastResult = result;
        _lastPlayBars = engine.PlayBars;
        _lastRecording = new AudioTake((float[])engine.Recording.Clone(), rate);
        SaveStatus = null;
        SaveRecordingCommand.NotifyCanExecuteChanged();

        // Per written note: the average over the times through.
        var marks = new List<NoteMark>();
        for (var o = 0; o < pattern.Onsets.Count; o++)
        {
            var timings = Enumerable.Range(0, times).Select(time => result.Notes[time * pattern.Onsets.Count + o]).ToList();
            var hits = timings.Where(t => t.IsHit).Select(t => t.OffsetMs!.Value).ToList();
            var missed = timings.Count - hits.Count;
            var index = pattern.NoteIndexOfOnset(o);
            if (hits.Count == 0)
            {
                marks.Add(new NoteMark(index, NoteMarkKind.Missed, "✕"));
                continue;
            }
            var mean = hits.Average();
            var label = $"{(mean >= 0.5 ? "+" : mean <= -0.5 ? "−" : "")}{Math.Abs(Math.Round(mean)):0}" + (missed > 0 ? $" ({missed}✕)" : "");
            marks.Add(new NoteMark(index, missed == 0 && Math.Abs(mean) <= RhythmScore.TightMs ? NoteMarkKind.OnTime : NoteMarkKind.Close, label));
        }
        Marks = marks;

        var tendency = result.MeanOffsetMs is { } m
            ? Math.Abs(m) < 3 ? "right on average" : $"avg {Math.Abs(m):0} ms {(m > 0 ? "late" : "early")}"
            : "no notes heard";
        var spread = result.MeanAbsOffsetMs is { } a ? $", ±{a:0} ms" : "";
        _lastSummary = $"{result.Percent} % ({result.Hits}/{result.Notes.Count} hit{(result.Extra > 0 ? $", {result.Extra} extra" : "")}, {tendency}{spread})";
        ResultText = played.Count == 0
            ? $"{result.Percent} %: nothing heard on {SelectedDevice?.Name ?? "the input"}. Check the input and its level (the Tuner shows it)."
            : _lastSummary;
        Takes.Insert(0, $"{_clock.GetLocalNow():HH:mm} · {pattern.Name} ({pattern.Meter.Signature}, {TempoLabel} {Tempo:0}): {_lastSummary}");
        OnPropertyChanged(nameof(HasTakes));
        LogCommand.NotifyCanExecuteChanged();
    }

    // ---- Logging ----

    public ObservableCollection<LabeledOption<int>> LogBlocks { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LogCommand))]
    private LabeledOption<int>? _selectedLogBlock;

    [ObservableProperty]
    private string? _logStatus;

    public bool HasLogBlocks => LogBlocks.Count > 0;

    private void LoadLogBlocks(LibraryStore library)
    {
        try
        {
            foreach (var category in library.LoadLibrary())
                foreach (var s in category.Subcategories)
                    if (category.Id == RhythmCategoryId || s.Id == RhythmReadingId)
                        LogBlocks.Add(new LabeledOption<int>(s.Id, $"{category.Name} · {s.DisplayName}"));
        }
#pragma warning disable CA1031 // Logging is optional: the window works without it.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Loading the Rhythm blocks failed: {ex.Message}");
        }
#pragma warning restore CA1031
        OnPropertyChanged(nameof(HasLogBlocks));
    }

    /// <summary>The linked item's block; else odd meters → Rhythm · Odd meters; otherwise Theory · Rhythm reading (else
    /// the first Rhythm block).</summary>
    private void SelectDefaultLogBlock()
    {
        if (_loading) return;
        var preferred = _link?.SubcategoryId ?? (SelectedMeter.IsOdd ? OddMetersId : RhythmReadingId);
        SelectedLogBlock = LogBlocks.FirstOrDefault(b => b.Value == preferred) ?? LogBlocks.FirstOrDefault();
    }

    private bool CanLog() => SelectedLogBlock is not null && _lastResult is not null && Takes.Count > _loggedTakes;

    /// <summary>Appends the last take's score to the chosen block's notes (keeps its exercises and rating): for the
    /// linked item's block on its day, else today.</summary>
    [RelayCommand(CanExecute = nameof(CanLog))]
    private void Log()
    {
        if (SelectedLogBlock is not { } block || _lastSummary is null || Pattern is not { } pattern) return;
        var now = _clock.GetLocalNow();
        var today = DateOnly.FromDateTime(now.DateTime);
        var day = _link is { } link && link.SubcategoryId == block.Value ? link.Date : today;
        var line = $"Rhythm trainer {now:HH:mm}: {pattern.Name} ({pattern.Meter.Signature}, {TempoLabel} {Tempo:0}, {_lastPlayBars} bar{(_lastPlayBars == 1 ? "" : "s")}): {_lastSummary}";
        try
        {
            var entry = _progress.Get(day, block.Value);
            var notes = string.IsNullOrWhiteSpace(entry?.Notes) ? line : entry.Notes.TrimEnd() + Environment.NewLine + line;
            _progress.Save(day, block.Value,
                entry?.Exercises.Select(x => new ExerciseLog(x.Name, x.Bpm, x.NoteValue, x.RemovedRun)) ?? [],
                entry?.Rating, notes);
            _loggedTakes = Takes.Count;
            LogStatus = day == today ? $"Added to today's “{block.Label}” notes." : $"Added to the “{block.Label}” notes for {day:ddd MMM d}.";
        }
#pragma warning disable CA1031 // Shown, not thrown.
        catch (Exception ex)
        {
            LogStatus = $"Couldn't log it: {ex.Message}";
        }
#pragma warning restore CA1031
        LogCommand.NotifyCanExecuteChanged();
    }

    // ---- Lifetime ----

    private void ReleaseStreams()
    {
        if (_loading) return;
        if (IsRunning) StopTake("Stopped (input changed).");
        _duplex?.Dispose();
        _duplex = null;
        _output?.Dispose();
        _output = null;
    }

    private void SaveSettings()
    {
        try
        {
            _settings.Set(SettingsKey, new RhythmTrainerSettings(SelectedMeter.Signature, Pattern?.Name, (double)(Tempo ?? 80), SelectedBars,
                ListenFirst, Click, ClickVolume, Drums, DrumVolume, ScorePlaying, SelectedDevice?.Name, SelectedChannel?.Channel ?? 0, Backbeat,
                [.. _own], RandomLevel, SelectedOutput?.Device?.Name, SelectedOutput?.Device?.Driver ?? -1,
                SelectedOutput?.FollowsMetronome ?? true));
        }
#pragma warning disable CA1031 // Losing a preference must not break the trainer.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving rhythm trainer settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _poll.Stop();
        _engine?.Stop();
        SaveSettings();
        _duplex?.Dispose();
        _output?.Dispose();
        _input.Dispose();
    }
}
