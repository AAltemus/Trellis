using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GuitarPractice.Audio;
using GuitarPractice.Audio.Midi;
using GuitarPractice.Infrastructure;

namespace GuitarPractice.App.ViewModels;

/// <summary>Opening the player for a practice item: its MIDI file (if linked) opens, and the file can be (un)linked.</summary>
public sealed record MidiPlayerLink(int SubcategoryId, string ItemName);

public sealed class MidiFileItemViewModel(MidiLibraryItem item)
{
    public MidiLibraryItem Item { get; } = item;
    public string Name => Item.DisplayName;
    public override string ToString() => Name;
}

/// <summary>A track: mute (play it yourself) or solo (hear only it).</summary>
public sealed partial class MidiTrackViewModel(MidiPlayerViewModel owner, int slot, MidiTrackInfo track) : ObservableObject
{
    public int Slot { get; } = slot;
    public int FileIndex { get; } = track.Index;
    public string Name { get; } = track.Name;
    public string Detail { get; } = $"{track.Instrument} · {track.NoteCount} notes";

    [ObservableProperty]
    private bool _isMuted;

    [ObservableProperty]
    private bool _isSoloed;

    partial void OnIsMutedChanged(bool value) => owner.OnTracksChanged();
    partial void OnIsSoloedChanged(bool value) => owner.OnTracksChanged();
}

/// <summary>
/// Tools → MIDI Player: a library of imported MIDI files (e.g. Guitar Pro exports, kept in Trellis's data folder),
/// played through the bundled SoundFont with speed (pitch unchanged), track mute/solo, a bar loop and a count-in. Each
/// file remembers its settings; a file can be linked to a practice item and opened from the timer ("♫ Play along").
/// </summary>
public sealed partial class MidiPlayerViewModel : ObservableObject, IDisposable
{
    public static string SoundFontPath => Path.Combine(AppContext.BaseDirectory, "SoundFont", "GeneralUser-GS.sf2");

    private readonly IAudioOutput _output;
    private readonly MidiLibraryStore _library;
    private readonly DispatcherTimer _poll;
    private readonly Task _soundFontLoading;
    private MidiPlayerEngine? _engine;
    private MidiSong? _song;
    private MidiPlayerLink? _link;
    private bool _applying;          // loading a file's settings: don't push or save yet
    private bool _positionFromEngine;

    public MidiPlayerViewModel(IAudioDevices devices, MidiLibraryStore library)
    {
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(library);
        _output = devices.CreateOutput(); // owned: disposed with the window
        _library = library;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Render, (_, _) => Poll());
        _poll.Stop();

        // ~1 s for 31 MB: load in the background while the window opens.
        _soundFontLoading = File.Exists(SoundFontPath)
            ? Task.Run(() => SoundFontSynth.Load(SoundFontPath))
            : Task.CompletedTask;
        if (!File.Exists(SoundFontPath)) Error = $"The instrument sounds are missing ({SoundFontPath}). Rebuild the app.";

        RefreshFiles(select: null);
    }

    // ---- Library ----

    public ObservableCollection<MidiFileItemViewModel> Files { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    // Every command whose CanExecute depends on the selected file (CanPlay / CanEditFile / CanLink) must be listed here.
    [NotifyCanExecuteChangedFor(nameof(PlayPauseCommand), nameof(StopCommand), nameof(LoopThisBarCommand), nameof(LoopAllCommand),
        nameof(RenameCommand), nameof(DeleteCommand), nameof(LinkCommand))]
    private MidiFileItemViewModel? _selectedFile;

    public bool HasFile => SelectedFile is not null && _song is not null;

    [ObservableProperty]
    private string _renameText = "";

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private string? _libraryStatus;

    public string LibraryFolder => _library.Directory;

    // ---- Song ----

    public ObservableCollection<MidiTrackViewModel> Tracks { get; } = [];

    [ObservableProperty]
    private string _songTitle = "No file selected";

    [ObservableProperty]
    private string _songInfo = "Import a .mid file (e.g. File → Export → MIDI in Guitar Pro).";

    [ObservableProperty]
    private int _barCount = 1;

    [ObservableProperty]
    private double _length = 1;

    // ---- Playback settings (saved per file) ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SpeedText))]
    private double _speedPercent = 100;

    [ObservableProperty]
    private bool _loopEnabled;

    [ObservableProperty]
    private decimal? _loopFromBar = 1;

    [ObservableProperty]
    private decimal? _loopToBar = 4;

    [ObservableProperty]
    private bool _countIn = true;

    [ObservableProperty]
    private double _volume = 100;

    public string SpeedText => $"{Math.Round(SpeedPercent)} %";

    // ---- Live ----

    [ObservableProperty]
    private double _position;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayPauseText))]
    private bool _isPlaying;

    [ObservableProperty]
    private string _positionText = "0:00";

    [ObservableProperty]
    private string _status = "Space: play/pause · Home: back to the start (or loop start)";

    [ObservableProperty]
    private string? _error;

    public string PlayPauseText => IsPlaying ? "❚❚ Pause" : "▶ Play";

    // ---- Link to a practice item ----

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinkedItem))]
    private string? _linkTitle;

    [ObservableProperty]
    private string? _linkStatus;

    public bool IsLinkedItem => LinkTitle is not null;

    /// <summary>Opened from a practice item: show it, open its linked file if there is one.</summary>
    public void OpenFor(MidiPlayerLink link)
    {
        ArgumentNullException.ThrowIfNull(link);
        _link = link;
        LinkTitle = link.ItemName;
        var linked = _library.LinkedFile(link.SubcategoryId);
        if (linked is not null) SelectedFile = Files.FirstOrDefault(f => f.Item.FileName == linked.FileName);
        RefreshLinkStatus();
        LinkCommand.NotifyCanExecuteChanged();
    }

    private bool CanLink() => _link is not null && SelectedFile is not null;

    [RelayCommand(CanExecute = nameof(CanLink))]
    private void Link()
    {
        if (_link is null || SelectedFile is null) return;
        _library.Link(_link.SubcategoryId, SelectedFile.Item.FileName);
        RefreshLinkStatus();
    }

    [RelayCommand]
    private void Unlink()
    {
        if (_link is null) return;
        _library.Unlink(_link.SubcategoryId);
        RefreshLinkStatus();
    }

    [RelayCommand]
    private void CloseLink()
    {
        _link = null;
        LinkTitle = null;
        LinkStatus = null;
        LinkCommand.NotifyCanExecuteChanged();
    }

    private void RefreshLinkStatus()
    {
        if (_link is null) return;
        var linked = _library.LinkedFile(_link.SubcategoryId);
        LinkStatus = linked is null
            ? "No file linked yet: pick one and press Link."
            : linked.FileName == SelectedFile?.Item.FileName
                ? "This file is linked: ♫ Play along opens it."
                : $"Linked to “{linked.DisplayName}”. Press Link to use this file instead.";
    }

    // ---- Library commands ----

    /// <summary>From the window's file picker. Each file is checked (must be a readable MIDI file) and copied in.</summary>
    public void ImportFiles(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        MidiLibraryItem? last = null;
        var problems = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                MidiFileReader.Read(path); // refuse what we can't play
                last = _library.Import(path);
            }
#pragma warning disable CA1031 // Report each bad file and carry on with the rest.
            catch (Exception ex)
            {
                problems.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
#pragma warning restore CA1031
        }
        RefreshFiles(last?.FileName);
        LibraryStatus = problems.Count > 0 ? "Not imported: " + string.Join("; ", problems)
            : last is not null ? $"Imported “{last.DisplayName}” (copied into the library)." : null;
    }

    private bool CanEditFile() => SelectedFile is not null;

    [RelayCommand(CanExecute = nameof(CanEditFile))]
    private void Rename()
    {
        if (SelectedFile is not { } file) return;
        try
        {
            var renamed = _library.Rename(file.Item.FileName, RenameText);
            RefreshFiles(renamed.FileName);
            LibraryStatus = $"Renamed to “{renamed.DisplayName}”.";
        }
#pragma warning disable CA1031 // Shown, not thrown.
        catch (Exception ex)
        {
            LibraryStatus = ex.Message;
        }
#pragma warning restore CA1031
    }

    /// <summary>Asks first (the button turns into Delete it / Cancel).</summary>
    [RelayCommand(CanExecute = nameof(CanEditFile))]
    private void Delete()
    {
        if (SelectedFile is not { } file) return;
        if (!IsConfirmingDelete)
        {
            IsConfirmingDelete = true;
            return;
        }
        IsConfirmingDelete = false;
        StopPlayback();
        _engine?.Load(null);
        _library.Delete(file.Item.FileName);
        RefreshFiles(select: null);
        LibraryStatus = $"Deleted “{file.Name}” from the library (the original you imported from is untouched).";
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    private void RefreshFiles(string? select)
    {
        var keep = select ?? SelectedFile?.Item.FileName;
        Files.Clear();
        foreach (var item in _library.List()) Files.Add(new MidiFileItemViewModel(item));
        SelectedFile = Files.FirstOrDefault(f => string.Equals(f.Item.FileName, keep, StringComparison.OrdinalIgnoreCase));
    }

    partial void OnSelectedFileChanging(MidiFileItemViewModel? value) => SaveFileSettings();

    partial void OnSelectedFileChanged(MidiFileItemViewModel? value)
    {
        IsConfirmingDelete = false;
        StopPlayback();
        LoadSong(value);
        RefreshLinkStatus();
    }

    private void LoadSong(MidiFileItemViewModel? file)
    {
        Tracks.Clear();
        _song = null;
        Error = File.Exists(SoundFontPath) ? null : Error;
        if (file is null)
        {
            SongTitle = "No file selected";
            SongInfo = Files.Count == 0 ? "Import a .mid file (e.g. File → Export → MIDI in Guitar Pro)." : "Pick a file on the left.";
            RenameText = "";
            _engine?.Load(null);
            OnPropertyChanged(nameof(HasFile));
            return;
        }

        try
        {
            _song = MidiFileReader.Read(file.Item.Path);
        }
#pragma warning disable CA1031 // A broken file is reported, not thrown.
        catch (Exception ex)
        {
            Error = $"Can't read “{file.Name}”: {ex.Message}";
            OnPropertyChanged(nameof(HasFile));
            return;
        }
#pragma warning restore CA1031

        var song = _song;
        RenameText = file.Name;
        SongTitle = file.Name;
        var sig = song.TimeSignatures[0];
        SongInfo = $"{Format(song.LengthSeconds)} · {song.BarCount} bars · {Math.Round(song.QuarterBpmAt(0))} BPM · {sig.Numerator}/{sig.Denominator}"
            + (song.Tempos.Count > 1 ? " (tempo changes)" : "");
        BarCount = song.BarCount;
        Length = Math.Max(0.1, song.LengthSeconds);

        var saved = _library.GetSettings(file.Item.FileName);
        _applying = true;
        try
        {
            for (var slot = 0; slot < song.Tracks.Count; slot++)
            {
                var track = new MidiTrackViewModel(this, slot, song.Tracks[slot])
                {
                    IsMuted = saved.MutedTracks?.Contains(song.Tracks[slot].Index) == true,
                    IsSoloed = saved.SoloTracks?.Contains(song.Tracks[slot].Index) == true,
                };
                Tracks.Add(track);
            }
            SpeedPercent = Math.Clamp(saved.SpeedPercent, 25, 150);
            LoopEnabled = saved.LoopEnabled;
            LoopFromBar = Math.Clamp(saved.LoopFromBar, 1, song.BarCount);
            LoopToBar = Math.Clamp(saved.LoopToBar, (int)LoopFromBar, song.BarCount);
            CountIn = saved.CountIn;
            Volume = Math.Clamp(saved.VolumePercent, 0, 200);
        }
        finally
        {
            _applying = false;
        }
        _engine?.Load(song);
        PushSettings();
        SetPosition(LoopEnabled ? song.BarStartSeconds((int)(LoopFromBar ?? 1)) : 0, seek: true);
        OnPropertyChanged(nameof(HasFile));
    }

    // ---- Settings → engine ----

    internal void OnTracksChanged() => PushSettings();
    partial void OnSpeedPercentChanged(double value) => PushSettings();
    partial void OnLoopEnabledChanged(bool value) => PushSettings();
    partial void OnLoopFromBarChanged(decimal? value) => PushSettings();
    partial void OnLoopToBarChanged(decimal? value) => PushSettings();
    partial void OnCountInChanged(bool value) => PushSettings();
    partial void OnVolumeChanged(double value) => PushSettings();

    private MidiPlayerSettings CurrentSettings()
    {
        ulong muted = 0, soloed = 0;
        foreach (var t in Tracks.Where(t => t.Slot < 64))
        {
            if (t.IsMuted) muted |= 1UL << t.Slot;
            if (t.IsSoloed) soloed |= 1UL << t.Slot;
        }
        var from = (int)Math.Clamp(LoopFromBar ?? 1, 1, Math.Max(1, BarCount));
        var to = (int)Math.Clamp(LoopToBar ?? from, from, Math.Max(1, BarCount));
        return new MidiPlayerSettings(SpeedPercent / 100.0, LoopEnabled, from, to, CountIn, (float)(Volume / 100.0), muted, soloed);
    }

    private void PushSettings()
    {
        if (_applying) return;
        if (_engine is not null) _engine.Settings = CurrentSettings();
        UpdatePositionText();
    }

    private void SaveFileSettings()
    {
        if (SelectedFile is not { } file || _song is null) return;
        var s = CurrentSettings();
        try
        {
            _library.SaveSettings(file.Item.FileName, new MidiFileSettings(
                (int)Math.Round(SpeedPercent), s.LoopEnabled, s.LoopFromBar, s.LoopToBar, s.CountIn, (int)Math.Round(Volume),
                [.. Tracks.Where(t => t.IsMuted).Select(t => t.FileIndex)],
                [.. Tracks.Where(t => t.IsSoloed).Select(t => t.FileIndex)]));
        }
#pragma warning disable CA1031 // Losing a preference must not break playback.
        catch (Exception ex)
        {
            Trace.TraceWarning($"Saving MIDI file settings failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }

    // ---- Transport ----

    private bool CanPlay() => SelectedFile is not null;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private async Task PlayPause()
    {
        if (_song is null) return;
        if (IsPlaying)
        {
            _engine?.Pause();
            IsPlaying = false;
            SaveFileSettings();
            return;
        }
        if (!await EnsureEngine()) return;
        _engine!.Play();
        IsPlaying = true;
        _poll.Start();
    }

    /// <summary>Stops and goes back to the start (or the loop start).</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Stop()
    {
        StopPlayback();
        if (_song is { } song) SetPosition(LoopEnabled ? song.BarStartSeconds((int)(LoopFromBar ?? 1)) : 0, seek: true);
        SaveFileSettings();
    }

    /// <summary>Sets the loop to the bar playing now (one bar).</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void LoopThisBar()
    {
        if (_song is null) return;
        var bar = _song.BarAt(Position);
        LoopFromBar = bar;
        LoopToBar = bar;
        LoopEnabled = true;
    }

    /// <summary>Loops the whole song: bar 1 to the last bar.</summary>
    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void LoopAll()
    {
        if (_song is null) return;
        LoopFromBar = 1;
        LoopToBar = _song.BarCount;
        LoopEnabled = true;
    }

    private void StopPlayback()
    {
        _engine?.Pause();
        IsPlaying = false;
    }

    /// <summary>The slider moved by the user: seek there.</summary>
    partial void OnPositionChanged(double value)
    {
        if (_positionFromEngine || _song is null) return;
        _engine?.Seek(value);
        UpdatePositionText();
    }

    private void SetPosition(double seconds, bool seek)
    {
        _positionFromEngine = !seek;
        Position = seconds;
        _positionFromEngine = false;
        if (seek) _engine?.Seek(seconds);
        UpdatePositionText();
    }

    private async Task<bool> EnsureEngine()
    {
        if (_engine is not null) return true;
        Error = null;
        try
        {
            if (!_soundFontLoading.IsCompleted) Status = "Loading the instrument sounds…";
            await _soundFontLoading;
            if (!File.Exists(SoundFontPath)) throw new FileNotFoundException("Instrument sounds not found.", SoundFontPath);
            var rate = _output.GetPreferredSampleRate();
            var engine = new MidiPlayerEngine(rate, new SoundFontSynth(SoundFontPath, rate)) { Settings = CurrentSettings() };
            engine.Load(_song);
            engine.Seek(Position);
            _output.Start(new AudioStreamOptions(SampleRate: rate, FramesPerBuffer: 256), engine.Render);
            _engine = engine;
            var device = (_output as PortAudioOutput)?.DeviceName;
            Status = $"{device ?? "Default output"} · {_output.SampleRate / 1000.0:0.#} kHz · Space: play/pause · Home: back to the start";
            return true;
        }
#pragma warning disable CA1031 // Any audio failure (no device, driver error, missing file) is shown, not thrown.
        catch (Exception ex)
        {
            Error = $"Couldn't start playback: {ex.Message}";
            return false;
        }
#pragma warning restore CA1031
    }

    /// <summary>UI thread, 20 Hz: follow the engine.</summary>
    private void Poll()
    {
        if (_engine is null) return;
        SetPosition(_engine.Position, seek: false);
        if (IsPlaying && !_engine.IsPlaying)
        {
            IsPlaying = false; // reached the end
            SaveFileSettings();
        }
        if (!IsPlaying) _poll.Stop();
    }

    private void UpdatePositionText()
    {
        if (_song is not { } song)
        {
            PositionText = "0:00";
            return;
        }
        var bpm = song.QuarterBpmAt(Position) * SpeedPercent / 100.0;
        var counting = _engine?.IsCountingIn == true ? "count-in · " : "";
        PositionText = $"{counting}{Format(Position)} / {Format(song.LengthSeconds)} · bar {song.BarAt(Position)} of {song.BarCount} · {Math.Round(bpm)} BPM";
    }

    internal static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    public void Dispose()
    {
        _poll.Stop();
        SaveFileSettings();
        _engine?.Pause();
        _output.Dispose();
    }
}
