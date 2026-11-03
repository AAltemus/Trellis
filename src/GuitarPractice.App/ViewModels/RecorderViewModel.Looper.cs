using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;

namespace GuitarPractice.App.ViewModels;

/// <summary>Remembered between sessions (AppSettings key "looper"). Fields are only appended.</summary>
public sealed record LooperSettings(
    double Bpm = 0, int BeatsPerBar = 4, int Bars = 4, bool Click = true, double ClickVolume = 80, double LoopVolume = 100,
    double LatencyMs = 0, string? MeasuredOn = null, bool CountInBeforeRecording = true, int LayerPasses = 1);

/// <summary>One recorded layer: its own volume and mute (not saved: layers live until Clear or the window closes).</summary>
public sealed partial class LooperLayerViewModel(int index, int passes, Action<LooperLayerViewModel> changed) : ObservableObject
{
    public int Index { get; } = index;
    public int Passes { get; } = passes;
    public string Title => Passes == 1 ? $"Layer {Index + 1}" : $"Layer {Index + 1} · {Passes} passes";

    [ObservableProperty]
    private double _volume = 100;   // percent, 0–150

    [ObservableProperty]
    private bool _isMuted;

    public float Gain => IsMuted ? 0f : (float)(Volume / 100.0);

    partial void OnVolumeChanged(double value) => changed(this);
    partial void OnIsMutedChanged(bool value) => changed(this);
}

/// <summary>
/// Recorder → Looper: an overdub looper synced to its own click (tempo, beats per bar, bars), recording on the chosen
/// input through one full-duplex stream (<see cref="Looper"/>) that plays through the Metronome's output (Tools →
/// Metronome → Output) when it can, with the round trip measured once by Calibrate (and fine-tuned by hand). Layers
/// can span 1, 2 or 4 passes and each has its own volume and mute. "Save as take" mixes the layers into a Recorder
/// take (attached like any new take).
/// </summary>
public sealed partial class RecorderViewModel
{
    public const string LooperSettingsKey = "looper";

    private IAudioDuplex? _duplex;
    private Looper? _looper;
    private LatencyProbe? _probe;
    private DispatcherTimer? _looperPoll;
    private bool _looperLoaded;
    private int _looperPollGrace;     // ticks to keep polling a stopped looper (an undo or clear is applied by the stream)

    public IReadOnlyList<int> LooperBeatOptions { get; } = [.. Enumerable.Range(2, 6)];      // 2–7
    public IReadOnlyList<int> LooperBarOptions { get; } = [1, 2, 4, 8, 12, 16];

    public IReadOnlyList<LabeledOption<int>> LooperPassOptions { get; } =
        [new(1, "1 pass"), new(2, "2 passes"), new(4, "4 passes")];

    public ObservableCollection<LooperLayerViewModel> LooperLayers { get; } = [];

    /// <summary>How many passes the next recorded layer spans.</summary>
    [ObservableProperty]
    private LabeledOption<int>? _selectedLooperPasses;

    /// <summary>The bar before a recording clicks (count-in volume) even with the click off.</summary>
    [ObservableProperty]
    private bool _looperCountInBeforeRecording = true;

    /// <summary>"Input 1 (SSL 2) → Output 1/2 (SSL 2)" once the stream has opened.</summary>
    [ObservableProperty]
    private string? _looperRoute;

    [ObservableProperty]
    private decimal? _looperBpm = 100;

    [ObservableProperty]
    private int _looperBeats = 4;

    [ObservableProperty]
    private int _looperBars = 4;

    [ObservableProperty]
    private bool _looperClick = true;

    [ObservableProperty]
    private double _looperClickVolume = 80;

    [ObservableProperty]
    private double _looperVolume = 100;

    /// <summary>Round-trip compensation (0–300 ms).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    private double _latencyMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyText))]
    private string? _latencyMeasuredOn;

    public string LatencyText => $"{LatencyMs:0.0} ms{(LatencyMeasuredOn is { } on ? $" (measured on {on})" : " (not measured yet: Calibrate)")}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LooperStartText), nameof(CanChangeLoop))]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand))]
    private bool _isLooperRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeLoop))]
    [NotifyCanExecuteChangedFor(nameof(UndoLayerCommand), nameof(ClearLayersCommand), nameof(SaveLoopCommand))]
    private int _layerCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CalibrateCommand), nameof(LooperStartStopCommand), nameof(RecordLayerCommand))]
    private bool _isCalibrating;

    [ObservableProperty]
    private string _looperPositionText = "";

    [ObservableProperty]
    private double _looperPosition;

    [ObservableProperty]
    private string _looperState = "Set the tempo and length, then ▶ Start (or ● Record to start and record the first pass).";

    [ObservableProperty]
    private bool _looperRecordingNow;

    [ObservableProperty]
    private string? _calibrationStatus;

    public string LooperStartText => IsLooperRunning ? "■ Stop" : "▶ Start";

    /// <summary>Tempo, beats and bars are fixed while there are layers (they set the loop's length).</summary>
    public bool CanChangeLoop => LayerCount == 0 && !IsLooperRunning;

    private void LoadLooperSettings()
    {
        if (_looperLoaded) return;
        _looperLoaded = true;
        var s = _settingsStore.Get<LooperSettings>(LooperSettingsKey) ?? new LooperSettings();
        var bpm = s.Bpm > 0 ? s.Bpm : (double)(CountInBpm ?? 100);
        LooperBpm = (decimal)Math.Clamp(Math.Round(bpm), Looper.MinBpm, Looper.MaxBpm);
        LooperBeats = LooperBeatOptions.Contains(s.BeatsPerBar) ? s.BeatsPerBar : 4;
        LooperBars = LooperBarOptions.Contains(s.Bars) ? s.Bars : 4;
        LooperClick = s.Click;
        LooperClickVolume = Math.Clamp(s.ClickVolume, 0, 100);
        LooperVolume = Math.Clamp(s.LoopVolume, 0, 150);
        LatencyMs = Math.Clamp(s.LatencyMs, 0, 300);
        LatencyMeasuredOn = s.MeasuredOn;
        LooperCountInBeforeRecording = s.CountInBeforeRecording;
        SelectedLooperPasses = LooperPassOptions.FirstOrDefault(o => o.Value == s.LayerPasses) ?? LooperPassOptions[0];
    }

    private void SaveLooperSettings()
    {
        if (!_looperLoaded) return;
        try
        {
            _settingsStore.Set(LooperSettingsKey, new LooperSettings((double)(LooperBpm ?? 100), LooperBeats, LooperBars, LooperClick,
                LooperClickVolume, LooperVolume, LatencyMs, LatencyMeasuredOn, LooperCountInBeforeRecording,
                SelectedLooperPasses?.Value ?? 1));
        }
#pragma warning disable CA1031 // Losing a preference must not break the looper.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving looper settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    partial void OnLooperClickChanged(bool value) => PushLooperLive();
    partial void OnLooperClickVolumeChanged(double value) => PushLooperLive();
    partial void OnLooperVolumeChanged(double value) => PushLooperLive();
    partial void OnLatencyMsChanged(double value) => PushLooperLive();
    partial void OnLooperCountInBeforeRecordingChanged(bool value) => PushLooperLive();

    private void PushLooperLive()
    {
        if (_looper is not { } looper) return;
        looper.ClickEnabled = LooperClick;
        looper.ClickVolume = (float)(LooperClickVolume / 100.0);
        looper.LoopVolume = (float)(LooperVolume / 100.0);
        looper.LatencySamples = (int)Math.Round(LatencyMs / 1000.0 * looper.SampleRate);
        looper.CountInBeforeRecording = LooperCountInBeforeRecording;
    }

    private void OnLayerChanged(LooperLayerViewModel layer) => _looper?.SetLayerGain(layer.Index, layer.Gain);

    /// <summary>Keeps one row per finished layer: new layers start at 100 %, undone ones go.</summary>
    private void SyncLayers(Looper looper)
    {
        var count = looper.LayerCount;
        while (LooperLayers.Count > count) LooperLayers.RemoveAt(LooperLayers.Count - 1);
        while (LooperLayers.Count < count)
            LooperLayers.Add(new LooperLayerViewModel(LooperLayers.Count, looper.LayerPasses(LooperLayers.Count), OnLayerChanged));
    }

    /// <summary>The Metronome's chosen output (Tools → Metronome → Output), null for the system default or if it's gone.</summary>
    private string? MetronomeOutputId()
    {
        try
        {
            return MetronomeViewModel.SavedOutput(_settingsStore, _output.Devices)?.Id;
        }
#pragma warning disable CA1031 // No device list: play through the default output.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Listing outputs for the looper failed: {ex.Message}");
            return null;
        }
#pragma warning restore CA1031
    }

    private AudioStreamOptions DuplexOptions(int rate, string? outputId) =>
        new(DeviceId: SelectedDevice?.Id, SampleRate: rate, FramesPerBuffer: 256,
            Channel: IsStereoDevice ? SelectedChannel?.Channel ?? 0 : 0, OutputDeviceId: outputId);

    // ---- Transport ----

    private bool CanUseLooper() => !IsCalibrating && !IsRecording;

    [RelayCommand(CanExecute = nameof(CanUseLooper))]
    private void LooperStartStop()
    {
        if (IsLooperRunning)
        {
            StopLooper();
            return;
        }
        StartLooper();
    }

    /// <summary>Arms a new layer (records the next whole pass, or 2 or 4); starts the loop first if it's stopped.</summary>
    [RelayCommand(CanExecute = nameof(CanUseLooper))]
    private void RecordLayer()
    {
        if (!IsLooperRunning && !StartLooper()) return;
        var passes = SelectedLooperPasses?.Value ?? 1;
        if (_looper is { } looper && !looper.ArmRecord(passes))
            LooperState = looper.LayerCount >= Looper.MaxLayers ? $"All {Looper.MaxLayers} layers are used: Undo one first."
                : passes > looper.MaxPassesPerLayer ? $"A layer can be at most {Looper.MaxLayerSeconds / 60:0} minutes: pick fewer passes."
                : "Already armed.";
    }

    private bool CanUndo() => LayerCount > 0 || _looper is { IsArmed: true } or { IsRecording: true };

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private void UndoLayer()
    {
        _looper?.Undo();
        KeepPolling();
    }

    private bool HasLayers() => LayerCount > 0;

    [RelayCommand(CanExecute = nameof(HasLayers))]
    private void ClearLayers()
    {
        _looper?.Clear();
        KeepPolling();
    }

    /// <summary>Mixes the layers into a new take (in the Takes list, attached like any new take).</summary>
    [RelayCommand(CanExecute = nameof(HasLayers))]
    private void SaveLoop()
    {
        if (_looper?.MixDown() is not { } mix) return;
        try
        {
            var item = SaveNewTake(new AudioTake(mix, _looper.SampleRate), "Loop");
            LooperState = $"Saved the loop as “{item.Name}” ({item.DurationText}) in Takes.";
        }
#pragma warning disable CA1031 // Report a failed save.
        catch (Exception ex)
        {
            Error = $"Couldn't save the loop: {ex.Message}";
        }
#pragma warning restore CA1031
    }

    private bool StartLooper()
    {
        Error = null;
        Pause();                                  // the take player
        try
        {
            _duplex ??= _duplexDevices.CreateDuplex();
            var outputId = MetronomeOutputId();
            var rate = (_duplex as PortAudioDuplex)?.GetPreferredSampleRate(SelectedDevice?.Id, outputId) ?? 48_000;
            var bpm = (double)(LooperBpm ?? 100);
            if (_looper is null || _looper.SampleRate != rate || _looper.Bpm != Math.Clamp(bpm, Looper.MinBpm, Looper.MaxBpm)
                || _looper.BeatsPerBar != LooperBeats || _looper.Bars != LooperBars)
            {
                _looper = new Looper(rate, bpm, LooperBeats, LooperBars);
                LooperLayers.Clear();
            }
            PushLooperLive();
            var looper = _looper;
            if (!_duplex.IsRunning)
                _duplex.Start(DuplexOptions(rate, outputId), (input, output) => looper.Process(input, output));
            LooperRoute = (_duplex as PortAudioDuplex)?.DeviceNames;
            looper.Start();
        }
#pragma warning disable CA1031 // Any audio failure is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't start the looper: {ex.Message}";
            return false;
        }
#pragma warning restore CA1031
        IsLooperRunning = true;
        SaveLooperSettings();
        StartLooperPoll();
        return true;
    }

    private void StopLooper()
    {
        _looper?.Stop();
        IsLooperRunning = false;
        // Keep the stream until the window closes or the input changes; it plays silence while stopped.
        RefreshLooper();
    }

    // ---- Calibration ----

    private bool CanCalibrate() => !IsLooperRunning && !IsCalibrating && !IsRecording;

    /// <summary>Plays 8 clicks and listens for them on the input: the round trip becomes the compensation.</summary>
    [RelayCommand(CanExecute = nameof(CanCalibrate))]
    private void Calibrate()
    {
        Error = null;
        Pause();
        try
        {
            _duplex ??= _duplexDevices.CreateDuplex();
            _duplex.Stop();                       // restart with the probe as the callback
            var outputId = MetronomeOutputId();
            var rate = (_duplex as PortAudioDuplex)?.GetPreferredSampleRate(SelectedDevice?.Id, outputId) ?? 48_000;
            var probe = _probe = new LatencyProbe(rate);
            _duplex.Start(DuplexOptions(rate, outputId), (input, output) => probe.Process(input, output));
            LooperRoute = (_duplex as PortAudioDuplex)?.DeviceNames;
        }
#pragma warning disable CA1031 // Any audio failure is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't start calibration: {ex.Message}";
            return;
        }
#pragma warning restore CA1031
        IsCalibrating = true;
        CalibrationStatus = "Listening for the clicks…";
        StartLooperPoll();
    }

    private void FinishCalibration()
    {
        var probe = _probe;
        _probe = null;
        _duplex?.Stop();                          // the looper restarts it with its own callback
        IsCalibrating = false;
        if (probe is null) return;
        var result = probe.Result();
        if (result.Milliseconds is double ms)
        {
            LatencyMs = Math.Round(ms, 1);
            LatencyMeasuredOn = LooperRoute ?? SelectedDevice?.Name ?? "the default input"; // the output is part of the round trip
            SaveLooperSettings();
        }
        CalibrationStatus = result.Message;
    }

    // ---- Polling ----

    private void StartLooperPoll()
    {
        _looperPoll ??= new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => RefreshLooper());
        _looperPoll.Start();
    }

    /// <summary>Polls ~½ s more even when stopped, so the layer list catches an undo or clear the stream applies.</summary>
    private void KeepPolling()
    {
        _looperPollGrace = 15;
        StartLooperPoll();
    }

    private void RefreshLooper()
    {
        if (IsCalibrating && _probe is { } probe)
        {
            CalibrationStatus = $"Listening for the clicks… {probe.Progress:P0}";
            if (probe.IsDone) FinishCalibration();
            return;
        }
        if (_looper is not { } looper)
        {
            _looperPoll?.Stop();
            return;
        }

        LayerCount = looper.LayerCount;
        SyncLayers(looper);
        LooperRecordingNow = looper.IsRecording;
        LooperPosition = looper.Position;
        var (bar, beat) = looper.BarBeat;
        LooperPositionText = !looper.IsRunning ? ""
            : looper.IsCountingIn ? $"Count-in {beat + 1}"
            : $"Bar {bar + 1} of {looper.Bars} · beat {beat + 1}";
        var recordingPasses = looper.RecordingPasses;
        LooperState = !looper.IsRunning
            ? LayerCount > 0 ? $"Stopped · {LayerCount} layer{(LayerCount == 1 ? "" : "s")} (▶ Start to hear them, Save as take to keep them)" : "Stopped"
            : looper.IsRecording
                ? $"● Recording layer {LayerCount + 1}{(looper.RecordingPass > 0 && recordingPasses > 1 ? $" · pass {looper.RecordingPass} of {recordingPasses}" : "")}… (it plays back when it's done)"
            : looper.IsCountingInToRecord ? $"Recording in {looper.BeatsPerBar - beat}…"
            : looper.IsArmed ? "Armed: recording starts at the next bar 1"
            : looper.IsCountingIn ? "Count-in…"
            : $"Looping · {LayerCount} layer{(LayerCount == 1 ? "" : "s")}";
        UndoLayerCommand.NotifyCanExecuteChanged();
        if (!looper.IsRunning && !IsCalibrating && --_looperPollGrace <= 0) _looperPoll?.Stop();
    }

    private void DisposeLooper()
    {
        _looperPoll?.Stop();
        _looper?.Stop();
        _duplex?.Dispose();
        SaveLooperSettings();
    }
}
