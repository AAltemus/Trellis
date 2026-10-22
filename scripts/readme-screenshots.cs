#:property PublishAot=false
#:property ManagePackageVersionsCentrally=false
#:package Avalonia.Headless@12.1.4
#:package Avalonia.Skia@12.1.4
#:package Avalonia.Themes.Fluent@12.1.4
#:package Avalonia.Fonts.Inter@12.1.4
#:package Avalonia.HarfBuzz@12.1.4
#:project ../src/GuitarPractice.App/GuitarPractice.App.csproj
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using GuitarPractice.App;
using GuitarPractice.App.ViewModels;
using GuitarPractice.App.Views;
using GuitarPractice.Audio;
using GuitarPractice.Domain;
using GuitarPractice.Domain.EarTraining;
using GuitarPractice.Domain.Practice;
using GuitarPractice.Domain.Planning;
using GuitarPractice.Infrastructure;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// Renders the README screenshots (docs/screenshots) from demo data, headless. In-memory DB and temp folders only: never
// your practice.db, recordings or midi folders. Never the real App class (its startup opens the real DB).
// Run from the repo root: dotnet run --file scripts/readme-screenshots.cs [-- <outDir>]
AppBuilder.Configure<ShotApp>().UseSkia().UseHarfBuzz().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var outDir = args.Length > 0 ? args[0] : Path.Combine("docs", "screenshots");
Directory.CreateDirectory(outDir);
void Pump() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();
var flags = BindingFlags.NonPublic | BindingFlags.Instance;
void Call(object o, string method) => o.GetType().GetMethod(method, flags)!.Invoke(o, null);

var windows = new List<(Window Window, string Name)>();
void Add(Window w, string name) { w.Show(); Pump(); w.UpdateLayout(); Pump(); windows.Add((w, name)); }

// A fixed evening: Wednesday 14 Oct 2026, 18:30 local.
var local = new DateTime(2026, 10, 14, 18, 30, 0);
var clock = new DemoClock(new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)));
var today = DateOnly.FromDateTime(local);
var monday = WeekMath.StartOfWeek(today);

using var f = new MemFactory();
var settings = new SettingsStore(f, clock);
var library = new LibraryStore(f);
var progress = new ProgressStore(f, clock);
var logs = new PracticeLogStore(f, clock);
var weeks = new WeekStore(f, clock);
var tracking = new ProgressTracking(settings);
var links = new TakeLinkStore(settings);
var tools = new NoTools();
var temp = Path.Combine(Path.GetTempPath(), "trellis-shots-" + Guid.NewGuid().ToString("N"));
var recDir = Directory.CreateDirectory(Path.Combine(temp, "recordings")).FullName;
var midiDir = Directory.CreateDirectory(Path.Combine(temp, "midi")).FullName;

// ---- Demo routine: 60 min weekdays, 90 Saturday, Sunday off ----
var routine = library.GetOrCreateDefaultRoutine();
library.SaveSettings(routine.Id, new Dictionary<DayOfWeek, int>
{
    [DayOfWeek.Monday] = 60, [DayOfWeek.Tuesday] = 60, [DayOfWeek.Wednesday] = 60, [DayOfWeek.Thursday] = 60,
    [DayOfWeek.Friday] = 45, [DayOfWeek.Saturday] = 90, [DayOfWeek.Sunday] = 0,
}, pinnedTimeOnTop: true);
var cats = library.LoadLibrary();
Subcategory Sub(string category, int index = 0) => cats.First(c => c.Name.Contains(category, StringComparison.OrdinalIgnoreCase)).Subcategories.OrderBy(s => s.SortOrder).ElementAt(index);
var picking = Sub("Alternate");
library.SetWeight(routine.Id, picking.Id, 8);
library.SetWeight(routine.Id, Sub("Legato").Id, 6);
var song = library.AddSubcategory(cats.First(c => c.Kind == CategoryKind.Songs).Id, "Little Wing",
    new SongDetails("Jimi Hendrix", null, SongStatus.Learning, "E minor"));
library.SetWeight(routine.Id, song.Id, 5);

// ---- Practice logged Monday, Tuesday and part of today ----
var first = new MainWindowViewModel(library, logs, weeks, new NoSound(), clock, tools, progress, settings, tracking, links);
foreach (var day in first.Days.Where(d => d.Date < today))
    foreach (var e in day.Entries) logs.Save(e.Key, new PracticeState(e.PlannedMinutes * 60, true));
var todayEntries = first.Days.First(d => d.Date == today).Entries;
logs.Save(todayEntries[0].Key, new PracticeState(todayEntries[0].PlannedMinutes * 60, true));
var pickingToday = todayEntries.FirstOrDefault(e => e.Key.SubcategoryId == picking.Id) ?? todayEntries[1];
logs.Save(pickingToday.Key, new PracticeState(pickingToday.PlannedMinutes * 60 * 3 / 5, false));

// ---- Progress history for the alternate-picking block: four weeks, three exercises ----
var rng = new Random(7);
string[] notes = ["Clean at the start, tense after 10 min", "Relax the forearm, smaller motions", "Finally even across strings",
    "Sloppy on the string change", "Good day", "Used the drum pattern; felt steadier"];
for (var d = 27; d >= 0; d--)
{
    if (d > 3 && (d % 7 == 4 || rng.Next(3) == 0)) continue;
    var date = today.AddDays(-d);
    if (d > 2) logs.Save(new PracticeKey(date, picking.Id), new PracticeState(60 * (15 + 5 * rng.Next(3)), true));
    var t = (27 - d) / 27.0;
    progress.Save(date, picking.Id,
    [
        new ExerciseLog("3nps scale", (int)(100 + 32 * t + rng.Next(-2, 3)), NoteValue.Sixteenths),
        new ExerciseLog("String skipping", (int)(140 + 26 * t + rng.Next(-3, 3)), NoteValue.Eighths),
        .. d < 18 ? [new ExerciseLog("Single-string runs", (int)(120 + 24 * (t - 0.35) / 0.65 + rng.Next(-2, 3)), NoteValue.Triplets)] : Array.Empty<ExerciseLog>(),
    ], 3 + (int)Math.Round(t * 1.6 + rng.NextDouble() * 0.6), d % 3 == 0 ? notes[rng.Next(notes.Length)] : null);
}
progress.SetTarget(picking.Id, "3nps scale", NoteValue.Sixteenths, 140);
progress.SetBlockTarget(picking.Id, 170);

// ---- Recorder takes (plucked-note riffs) ----
var rate = 48_000;
float[] Riff(int[] midi, double noteSeconds, int seed)
{
    var r = new Random(seed);
    var len = (int)(midi.Length * noteSeconds * rate + rate);
    var s = new float[len];
    for (var n = 0; n < midi.Length; n++)
    {
        var hz = 440 * Math.Pow(2, (midi[n] - 69) / 12.0);
        var start = (int)(n * noteSeconds * rate);
        var amp = 0.35 + 0.15 * r.NextDouble();
        for (var i = 0; i < rate * 1.2 && start + i < len; i++)
        {
            var tt = i / (double)rate;
            var env = Math.Exp(-tt * 3.2) * Math.Min(1, i / 60.0);
            s[start + i] += (float)(amp * env * (Math.Sin(2 * Math.PI * hz * tt) + 0.4 * Math.Sin(4 * Math.PI * hz * tt) + 0.2 * Math.Sin(6 * Math.PI * hz * tt)) / 1.6);
        }
    }
    return s;
}
WavFile.Write(Path.Combine(recDir, "3nps scale at 132.wav"), new AudioTake(Riff([52, 54, 56, 57, 59, 61, 62, 64, 66, 68, 69, 71, 73, 74, 76, 74, 73, 71, 69, 68, 66, 64], 0.115, 1), rate));
WavFile.Write(Path.Combine(recDir, "Little Wing intro.wav"), new AudioTake(Riff([64, 67, 71, 76, 74, 71, 67, 69, 72, 76, 79, 76], 0.42, 2), rate));
WavFile.Write(Path.Combine(recDir, "Legato idea.wav"), new AudioTake(Riff([57, 59, 60, 62, 64, 62, 60, 59], 0.3, 3), rate));
File.SetLastWriteTime(Path.Combine(recDir, "Legato idea.wav"), local.AddDays(-3));
File.SetLastWriteTime(Path.Combine(recDir, "Little Wing intro.wav"), local.AddDays(-1));
File.SetLastWriteTime(Path.Combine(recDir, "3nps scale at 132.wav"), local.AddMinutes(-12));
links.Set("3nps scale at 132.wav", new TakeLink(picking.Id, today, "3nps scale"));
links.Set("Little Wing intro.wav", new TakeLink(song.Id, today.AddDays(-1)));

// ---- MIDI file: 16 bars, guitar + bass + drums ----
var midiPath = Path.Combine(temp, "Groove in E.mid");
File.WriteAllBytes(midiPath, DemoMidi.Build());
var midiStore = new MidiLibraryStore(settings, midiDir);
midiStore.Import(midiPath);

// ---- Main window: today's picking block selected, log strip open ----
var mainVm = new MainWindowViewModel(library, logs, weeks, new NoSound(), clock, tools, progress, settings, tracking, links);
var main = new MainWindow { DataContext = mainVm, Width = 1500, Height = 900 };
Add(main, "week");
mainVm.Days.First(d => d.Date == today).Entries.First(e => e.Key.SubcategoryId == picking.Id).SelectCommand.Execute(null);
mainVm.IsLogExpanded = true;
Pump(); main.UpdateLayout(); Pump();
// The board opens scrolled to today; from Monday reads better in a still picture (Mon, Tue done → today).
main.GetVisualDescendants().OfType<ScrollViewer>().First(s => s.Name == "WeekBoard").Offset = default;
Pump();

// ---- Progress ----
var pvm = new ProgressViewModel(library, progress, clock, tracking, links, tools);
pvm.SelectedExercise = pvm.Exercises.First(e => e.Id == picking.Id); Pump();
Add(new ProgressWindow { DataContext = pvm, Width = 1240, Height = 900 }, "progress");

// ---- Metronome: drums, a groove pattern, trainer on ----
var met = new MetronomeViewModel(new FakeDevices(), settings);
met.SelectedSound = met.SoundOptions.First(o => o.Value == MetronomeSound.Drums);
met.SelectedPattern = met.Patterns.FirstOrDefault(p => p.Pattern.Name.Contains("rock", StringComparison.OrdinalIgnoreCase)) ?? met.Patterns.First();
met.Bpm = 96;
Pump();
Add(new MetronomeWindow { DataContext = met }, "metronome");

// ---- Tuner: an A string a few cents sharp, strobe on ----
var tunerDevices = new FakeDevices(toneHz: 110 * Math.Pow(2, 6.0 / 1200));
var tuner = new TunerViewModel(tunerDevices, library, settings) { ShowStrobe = true };
for (var i = 0; i < 60; i++) { Thread.Sleep(25); Call(tuner, "Refresh"); }
Pump();
Add(new TunerWindow { DataContext = tuner }, "tuner");

// ---- Recorder: the attached take selected, a loop over its middle ----
var rec = new RecorderViewModel(new FakeDevices(), settings, links, library, progress, clock, recDir);
rec.SelectedTake = rec.Takes.First(t => t.Name == "3nps scale at 132"); Pump();
rec.LoopStart = 0.18; rec.LoopEnd = 0.62; Pump();
Add(new RecorderWindow { DataContext = rec, Width = 980, Height = 760 }, "recorder");

// ---- MIDI player: song loaded, loop bars 5–8, bar 6 current ----
var midi = new MidiPlayerViewModel(new FakeDevices(), midiStore);
midi.SelectedFile = midi.Files.First(); Pump();
midi.LoopFromBar = 5; midi.LoopToBar = 8; midi.LoopEnabled = true; midi.SpeedPercent = 80; Pump();
midi.Position = 5 * 2.4 + 0.9; // bar 6, beat 2 (100 BPM, 4/4)
Pump();
var midiWin = new MidiPlayerWindow { DataContext = midi, Width = 980, Height = 860 };
Add(midiWin, "midi-player");
// The footer shows the library folder: the temp folder here would print this PC's user name.
midiWin.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text?.StartsWith("Saved in", StringComparison.Ordinal) == true)
    .Text = @"Saved in C:\Users\you\AppData\Local\GuitarPractice\midi";

// ---- Ear trainer: chords in G major, a few answered ----
var ear = new EarTrainerViewModel(new FakeDevices(), settings, library, progress, clock);
ear.SelectedMode = ear.ModeOptions.First(o => o.Value == EarItemKind.Chord);
ear.SelectedTonic = ear.TonicOptions.First(o => o.Value == 7); Pump();
var earWin = new EarTrainerWindow { DataContext = ear, Width = 780, Height = 1180 };
Add(earWin, "ear-trainer");
var pick = new Random(3);
for (var q = 0; q < 9; q++)
{
    ear.NextCommand.Execute(null); Pump();
    var answers = ear.Answers.ToList();
    var right = answers.FirstOrDefault(a => a.Item == ((EarQuiz)typeof(EarTrainerViewModel).GetField("_quiz", flags)!.GetValue(ear)!).Current?.Item);
    var chosen = right is not null && pick.Next(5) != 0 ? right : answers[pick.Next(answers.Count)];
    chosen.ChooseCommand.Execute(null); Pump();
}

// ---- Render each window once, alternating dark and light (README order) ----
for (var i = 0; i < windows.Count; i++)
{
    var (w, name) = windows[i];
    Application.Current!.RequestedThemeVariant = i % 2 == 0 ? ThemeVariant.Dark : ThemeVariant.Light;
    Pump(); w.UpdateLayout(); Pump(); w.InvalidateVisual(); Pump();
    var b = new RenderTargetBitmap(new PixelSize((int)w.Bounds.Width, (int)w.Bounds.Height));
    b.Render(w);
    var file = Path.Combine(outDir, name + ".png");
    b.Save(file, new PngBitmapEncoderOptions { CompressionLevel = System.IO.Compression.CompressionLevel.SmallestSize });
    Console.WriteLine($"{Path.GetFileName(file)} ({(i % 2 == 0 ? "dark" : "light")}) {w.Bounds.Width}×{w.Bounds.Height} {new FileInfo(file).Length / 1024} KB");
}
foreach (var (w, _) in windows) w.Close();
Pump();
Directory.Delete(temp, true);

sealed class DemoClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}
sealed class NoSound : INotificationSound { public void PlayDing() { } }
sealed class NoTools : IToolWindows
{
    public void ShowMetronome(MetronomeLink? link = null) { }
    public void ShowTuner() { }
    public void ShowRecorder(RecorderLink? link = null, string? selectTake = null) { }
    public void ShowEarTrainer() { }
    public void ShowSettings() { }
    public void ShowMidiPlayer(MidiPlayerLink? link = null) { }
    public void ShowProgress(int? subcategoryId = null) { }
    public void FollowInProgress(int subcategoryId) { }
    public void CloseAll() { }
}
/// <summary>The app's own styles (App.axaml), without the real App class.</summary>
sealed class ShotApp : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        Styles.Add(new Style(x => x.OfType<TextBlock>().Class("caption")) { Setters = { new Setter(TextBlock.FontSizeProperty, 12.0), new Setter(Visual.OpacityProperty, 0.7) } });
        Styles.Add(new Style(x => x.OfType<TextBlock>().Class("warning")) { Setters = { new Setter(TextBlock.ForegroundProperty, Brush.Parse("#E65100")), new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap) } });
        Styles.Add(new Style(x => x.OfType<ProgressBar>().Class(":horizontal")) { Setters = { new Setter(Layoutable.MinWidthProperty, 0.0) } });
    }
}
sealed class MemFactory : IDbContextFactory<PracticeDbContext>, IDisposable
{
    private readonly SqliteConnection _c = new("DataSource=:memory:;Foreign Keys=True");
    private readonly DbContextOptions<PracticeDbContext> _o;
    public MemFactory()
    {
        _c.Open();
        _o = new DbContextOptionsBuilder<PracticeDbContext>().UseSqlite(_c).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }
    public PracticeDbContext CreateDbContext() => new(_o);
    public void Dispose() => _c.Dispose();
}
sealed class FakeDevices(double toneHz = 0) : IAudioDevices
{
    public IAudioInput CreateInput() => new FakeInput(toneHz);
    public IAudioOutput CreateOutput() => new FakeOutput();
    public IAudioDuplex CreateDuplex() => new FakeDuplex();
}
/// <summary>Plays a steady plucked-string-like tone into the capture callback (for the tuner).</summary>
sealed class FakeInput(double toneHz) : IAudioInput
{
    private Thread? _thread;
    private volatile bool _run;
    public IReadOnlyList<AudioDeviceInfo> Devices { get; } = [new("1", "Input 1 (SSL 2 USB Audio)", 1, 48_000, true)];
    public int SampleRate => 48_000;
    public bool IsRunning => _run;
    public int GetPreferredSampleRate(string? deviceId = null) => 48_000;
    public void Start(AudioStreamOptions options, AudioCaptureCallback callback)
    {
        if (toneHz <= 0) return;
        _run = true;
        _thread = new Thread(() =>
        {
            var buf = new float[512];
            long n = 0;
            while (_run)
            {
                for (var i = 0; i < buf.Length; i++, n++)
                {
                    var t = n / 48_000.0;
                    buf[i] = (float)(0.08 * (Math.Sin(2 * Math.PI * toneHz * t) + 0.5 * Math.Sin(4 * Math.PI * toneHz * t) + 0.25 * Math.Sin(6 * Math.PI * toneHz * t)));
                }
                callback(buf);
                Thread.Sleep(5);
            }
        }) { IsBackground = true };
        _thread.Start();
    }
    public void Stop() => _run = false;
    public void Dispose() => Stop();
}
sealed class FakeOutput : IAudioOutput
{
    public IReadOnlyList<AudioDeviceInfo> Devices { get; } = [new("2", "Output 1/2 (SSL 2 USB Audio)", 2, 48_000, true)];
    public int SampleRate => 48_000;
    public TimeSpan OutputLatency => TimeSpan.Zero;
    public bool IsRunning => false;
    public int GetPreferredSampleRate(string? deviceId = null) => 48_000;
    public void Start(AudioStreamOptions options, AudioRenderCallback callback) { }
    public void Stop() { }
    public void Dispose() { }
}
sealed class FakeDuplex : IAudioDuplex
{
    public int SampleRate => 48_000;
    public bool IsRunning => false;
    public void Start(AudioStreamOptions options, AudioDuplexCallback callback) { }
    public void Stop() { }
    public void Dispose() { }
}
/// <summary>A small type-1 MIDI file: tempo track + rhythm guitar, bass and drums, 16 bars of 4/4 at 100 BPM.</summary>
static class DemoMidi
{
    public static byte[] Build()
    {
        const int ppq = 480, bars = 16;
        var tracks = new List<byte[]>
        {
            Track(e => { e.Meta(0, 0x03, "Groove in E"u8.ToArray()); e.Meta(0, 0x51, [0x09, 0x27, 0xC0]); e.Meta(0, 0x58, [4, 2, 24, 8]); }),
            Track(e =>
            {
                e.Meta(0, 0x03, "Rhythm guitar"u8.ToArray()); e.Program(0, 0, 29);
                int[][] chords = [[40, 47, 52], [43, 50, 55], [45, 52, 57], [38, 45, 50]];
                for (var bar = 0; bar < bars; bar++)
                    for (var step = 0; step < 8; step++)
                    {
                        var at = (bar * 8 + step) * ppq / 2;
                        foreach (var n in chords[bar / 2 % 4]) e.Note(at, 0, n, step % 2 == 0 ? 100 : 80, ppq / 2 - 20);
                    }
            }),
            Track(e =>
            {
                e.Meta(0, 0x03, "Bass"u8.ToArray()); e.Program(0, 1, 33);
                int[] roots = [28, 31, 33, 26];
                for (var bar = 0; bar < bars; bar++)
                    for (var beat = 0; beat < 4; beat++)
                        e.Note((bar * 4 + beat) * ppq, 1, roots[bar / 2 % 4] + (beat == 3 ? 7 : 0), 100, ppq - 30);
            }),
            Track(e =>
            {
                e.Meta(0, 0x03, "Drums"u8.ToArray());
                for (var bar = 0; bar < bars; bar++)
                    for (var step = 0; step < 8; step++)
                    {
                        var at = (bar * 8 + step) * ppq / 2;
                        e.Note(at, 9, 42, 80, 60);
                        if (step is 0 or 5) e.Note(at, 9, 36, 110, 60);
                        if (step is 2 or 6) e.Note(at, 9, 38, 105, 60);
                    }
            }),
        };
        var file = new List<byte>();
        file.AddRange("MThd"u8.ToArray()); file.AddRange(BE32(6)); file.AddRange(new byte[] { 0, 1, 0, (byte)tracks.Count, ppq >> 8, ppq & 0xFF });
        foreach (var t in tracks) { file.AddRange("MTrk"u8.ToArray()); file.AddRange(BE32(t.Length)); file.AddRange(t); }
        return [.. file];
    }
    private static byte[] BE32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] Track(Action<Events> build)
    {
        var e = new Events();
        build(e);
        var bytes = new List<byte>();
        var last = 0;
        foreach (var (at, _, data) in e.All.OrderBy(x => x.At).ThenBy(x => x.Order))
        {
            var delta = at - last; last = at;
            var vlq = new List<byte> { (byte)(delta & 0x7F) };
            while ((delta >>= 7) > 0) vlq.Insert(0, (byte)(0x80 | (delta & 0x7F)));
            bytes.AddRange(vlq); bytes.AddRange(data);
        }
        bytes.AddRange(new byte[] { 0, 0xFF, 0x2F, 0 });
        return [.. bytes];
    }
    private sealed class Events
    {
        public List<(int At, int Order, byte[] Data)> All { get; } = [];
        public void Meta(int at, byte type, byte[] data) => All.Add((at, 0, [0xFF, type, (byte)data.Length, .. data]));
        public void Program(int at, int ch, int program) => All.Add((at, 1, [(byte)(0xC0 | ch), (byte)program]));
        public void Note(int at, int ch, int note, int velocity, int length)
        {
            All.Add((at, 3, [(byte)(0x90 | ch), (byte)note, (byte)velocity]));
            All.Add((at + length, 2, [(byte)(0x80 | ch), (byte)note, 0]));
        }
    }
}
