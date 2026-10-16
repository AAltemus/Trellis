using GuitarPractice.Domain;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure.Persistence.Seed;

/// <summary>
/// Built-in library, seeded through migrations with fixed IDs so migrations stay deterministic.
/// ID scheme: categories 1–99; subcategories are categoryId * 100 + n; tunings 1–99.
/// User-created rows start at 10,001 (DatabaseInitializer bumps sqlite_sequence), so built-ins added
/// in later seed migrations never collide with user data.
/// Changing anything here requires a new migration: dotnet ef migrations add SeedUpdate_xxx.
/// </summary>
internal static class BuiltInData
{
    public const int WarmupCategoryId = 1;
    public const int SongsCategoryId = 12;

    // Sort order doubles as in-day ordering: warmup → picking → left hand → rhythm → knowledge → songs → creative.
    private static readonly (int Id, string Name, int Sort, string Color, CategoryKind Kind)[] Categories =
    [
        (1,  "Warmup",            0,   "#9E9E9E", CategoryKind.Exercise),
        (2,  "Alternate Picking", 10,  "#E53935", CategoryKind.Exercise),
        (3,  "Economy Picking",   20,  "#FB8C00", CategoryKind.Exercise),
        (4,  "Sweep Picking",     30,  "#FDD835", CategoryKind.Exercise),
        (5,  "Legato",            40,  "#43A047", CategoryKind.Exercise),
        (6,  "Bending & Vibrato", 50,  "#00ACC1", CategoryKind.Exercise),
        (7,  "Rhythm",            60,  "#1E88E5", CategoryKind.Exercise),
        (8,  "Theory",            70,  "#5E35B1", CategoryKind.Exercise),
        (9,  "Ear Training",      80,  "#8E24AA", CategoryKind.Exercise),
        (12, "Songs",             85,  "#F06292", CategoryKind.Songs),
        (10, "Improvisation",     90,  "#D81B60", CategoryKind.Exercise),
        (11, "Composition",       100, "#6D4C41", CategoryKind.Exercise),
    ];

    private sealed record S(string Name, string Notes, int Min = 10, int Max = 20,
        int? StartBpm = null, int? TargetBpm = null, PinMode Pin = PinMode.None, int? PinnedMinutes = null);

    private static S Warmup(string name, string notes, int? start = null, int? target = null) =>
        new(name, notes, 5, 10, start, target, PinMode.DailyFirst, 5);

    private static readonly Dictionary<int, S[]> Subcategories = new()
    {
        [1] =
        [
            Warmup("Chromatic 1-2-3-4", "One finger per fret across all six strings, up and back. Strict alternate picking, even volume.", 60, 120),
            Warmup("Finger permutations", "Cycle the 24 orderings of fingers 1-2-3-4 (1-3-2-4, 1-4-2-3…), two or three per day.", 60, 110),
            Warmup("Spider walk", "Fingers move in pairs across adjacent strings. Keep unused fingers hovering close to the frets.", 50, 100),
            Warmup("Open-string picking", "Picking hand only: 8ths, triplets and 16ths on open strings to sync wrist and pick depth.", 80, 160),
            Warmup("Slow stretches", "Wide spans (1-3-5, 1-4-7) high on the neck, moving lower as the hand loosens. Stop if anything hurts."),
            Warmup("Slow scale run-through", "One scale through every position at a relaxed tempo, focusing on clean fretting.", 60, 100),
        ],
        [2] =
        [
            new("Single-string 16ths & tremolo", "Constant down-up on one string, then tremolo bursts. Small motions, consistent pick depth.", 10, 20, 90, 200),
            new("Inside/outside string crossing", "Two-string patterns that isolate picking between the strings (inside) and around them (outside).", 10, 20, 70, 160),
            new("Scale sequences (3s, 4s, 6ths)", "Major and minor scales in sequences; accent the start of each group.", 10, 20, 70, 160),
            new("Pentatonic runs (2 notes per string)", "Even-numbered string changes make every crossing the same; master the outside cross.", 10, 20, 80, 180),
            new("3nps scale runs", "Three notes per string through all seven positions with strict alternate picking.", 10, 20, 70, 160),
            new("Odd groupings (5s, 6s, 7s)", "Note groups that cross the beat. Accent the first note of each group against the click.", 10, 20, 60, 140),
            new("Pedal-tone licks", "Alternate a moving line against a fixed open or fretted note.", 10, 20, 80, 170),
            new("Speed bursts", "Short 2–4 note bursts above your clean tempo, then rest. Builds top speed without tension.", 5, 15, 100, 220),
        ],
        [3] =
        [
            new("3nps economy scales", "Sweep through every string change in the direction of travel; alternate pick within a string.", 10, 20, 70, 160),
            new("Two-string economy patterns", "Odd note counts on two strings so every crossing is a sweep.", 10, 20, 70, 160),
            new("Five-note pentatonic sequences", "Pentatonic fragments that force directional pick changes.", 10, 20, 70, 150),
            new("Economy arpeggio runs", "Mixed one- and two-note-per-string arpeggios using directional picking.", 10, 20, 60, 140),
            new("Sweep-to-pick transitions", "Move between swept arpeggio fragments and picked scale runs without changing feel.", 10, 20, 60, 140),
        ],
        [4] =
        [
            new("3-string triads", "Major, minor and diminished shapes on the top three strings. Mute everything you aren't playing.", 10, 20, 50, 130),
            new("4-string 7th-chord arpeggios", "maj7, m7, dom7 and m7b5 shapes. Hear each chord quality as you sweep it.", 10, 20, 50, 120),
            new("5-string major/minor arpeggios", "A-string root shapes with a pulled-off or tapped top note.", 10, 20, 40, 120),
            new("6-string arpeggios", "E-string root shapes. Notes stay separated, never strummed.", 10, 20, 40, 110),
            new("Rolled sweeps", "Same-fret string rolls with no two notes ringing together.", 5, 15, 50, 110),
            new("Sweeps with tapping", "Tap the top note of the arpeggio to extend its range.", 10, 20, 40, 110),
            new("Arpeggio progressions", "One shape per chord through a progression (e.g. i–VI–III–VII), changing in time.", 10, 20, 40, 110),
        ],
        [5] =
        [
            new("3nps hammer/pull runs", "Pick only the first note on each string; match the volume of picked and legato notes.", 10, 20, 60, 150),
            new("Trill endurance", "One-minute trills on each finger pair (1-2, 1-3, 1-4, 2-3, 2-4, 3-4).", 5, 15, 80, 160),
            new("Hammer-ons from nowhere", "Start each string with a fretting-hand hammer, no pick. Builds strength and string control.", 10, 20, 60, 140),
            new("Legato scale sequences", "Sequenced 3nps lines with slides for position shifts.", 10, 20, 60, 140),
            new("Open-string pull-offs", "Pull-off licks to open strings. Pull slightly sideways for volume.", 5, 15, 80, 160),
            new("Slides & position shifts", "Connect positions with one-finger slides while keeping time.", 10, 20, 60, 130),
            new("Two-hand tapping", "Tap–pull–hammer triplet cells, then moving tapped arpeggios.", 10, 20, 60, 140),
        ],
        [6] =
        [
            new("Pitch-accurate bends", "Half, whole and 1½-step bends checked against the tuner's bend mode.", 10, 15),
            new("Pre-bends & releases", "Bend silently to pitch, pick, then release smoothly back to the fretted note.", 10, 15),
            new("Unison & double-stop bends", "Bend the lower string up to the held note. Listen for the beating to stop.", 10, 15),
            new("Vibrato width & speed", "Narrow/wide and slow/fast vibrato in time with the click (8ths, triplets).", 10, 15, 60, 100),
            new("Vibrato on bent notes", "Hold a bend at pitch and add vibrato without dropping flat.", 10, 15),
            new("Bending licks & phrasing", "Classic blues/rock bend licks; focus on intonation at the top of each bend.", 10, 15),
        ],
        [7] =
        [
            new("Downpicking endurance", "Continuous 8th-note downpicking. Lengthen each run before tension sets in.", 10, 20, 120, 220),
            new("Palm-muted 16ths & gallops", "Alternate-picked 16ths and gallops with a consistent mute and accents.", 10, 20, 90, 200),
            new("Strumming patterns", "16th-note strum grid on muted strings. Keep the hand moving; choose which strokes hit.", 10, 20, 60, 140),
            new("Syncopation (click on 2 & 4)", "Metronome on 2 and 4 only; place offbeat accents and pushes.", 10, 20, 60, 140),
            new("Subdivision changes", "Switch between quarters, 8ths, triplets and 16ths every bar without drifting.", 10, 20, 60, 120),
            new("Odd meters (5/4, 7/8)", "Riffs and strumming in odd meters. Count out loud first.", 10, 20, 60, 140),
            new("Chord changes", "One-minute changes: count clean switches between two chords and beat your best.", 5, 15),
            new("Muting & tight stops", "Stop chords exactly on the beat and kill string noise with both hands.", 10, 20, 80, 180),
            new("Locking in with drums", "Play riffs over drum loops. Record and check your attacks line up with kick and snare.", 10, 20),
        ],
        [8] =
        [
            new("Fretboard note names", "Name every note on one string per day, then find one note everywhere on the neck.", 5, 15),
            new("Intervals on the fretboard", "Play and name intervals from any root, on one string and across strings.", 10, 20),
            new("Major scale & modes", "Play each mode from one root and name its characteristic note.", 10, 20),
            new("CAGED system", "Connect the five shapes for one chord, then change keys.", 10, 20),
            new("Diatonic harmony", "Triads and 7th chords on each scale degree; play a key's chords in several positions.", 10, 20),
            new("Chord construction & extensions", "Build sus, add9, 9th, 11th and 13th chords from formulas.", 10, 20),
            new("Circle of fifths & keys", "Key signatures, relative minors and common modulations.", 5, 15),
            new("Rhythm reading", "Read and play rhythms from notation: rests, ties, dotted notes, triplets.", 10, 20),
        ],
        [9] =
        [
            new("Interval recognition", "Identify melodic and harmonic intervals; sing them before checking.", 5, 15),
            new("Chord quality ID", "Major, minor, diminished, augmented and 7th qualities by ear.", 5, 15),
            new("Progression recognition", "Recognise common progressions (I–IV–V, ii–V–I, i–VI–III–VII) by their bass motion.", 5, 15),
            new("Sing what you play", "Sing a note or short phrase, then find it on the guitar. Then reverse it.", 5, 15),
            new("Call & response", "Play back short phrases from a recording or app after one listen.", 5, 15),
            new("Find the key", "Put on a song and find its tonic and key within the first chorus.", 5, 15),
            new("Transcription", "Transcribe a short riff or solo by ear, then check it against a tab.", 10, 20),
        ],
        [10] =
        [
            new("Backing-track soloing", "Full solo over a track. Record it and listen back.", 10, 20),
            new("Target chord tones", "Land a chord tone on every chord change.", 10, 20),
            new("Motif development", "One short idea varied in rhythm, register and intervals.", 10, 20),
            new("Phrasing & space", "Call-and-response phrases; leave as much space as you play.", 10, 20),
            new("Limited-note soloing", "Restrict yourself to three notes or one string to force rhythmic ideas.", 10, 20),
            new("Modal improvisation", "Solo over a drone to hear each mode's character.", 10, 20),
            new("Lick vocabulary", "Learn a lick, move it through keys and vary its rhythm until it's yours.", 10, 20),
            new("Dynamics & articulation", "Repeat one phrase with different attack, volume, vibrato and slides.", 10, 20),
        ],
        [11] =
        [
            new("Riff writing", "Write and record three riffs. Pick one to keep.", 15, 30),
            new("Chord progressions", "Write progressions in a key, then borrow chords from the parallel mode.", 15, 30),
            new("Melody writing", "Write a vocal-style melody over a progression and play it on guitar.", 15, 30),
            new("Song arrangement", "Arrange riffs into verse/chorus/bridge and demo the structure.", 15, 30),
            new("Solo composition", "Compose a solo with a beginning, a build and a climax; record it.", 15, 30),
            new("Writing to a constraint", "Give yourself a brief: odd meter, one scale, a set tempo or a mood.", 15, 30),
            new("Demo recording", "Record and rough-mix a demo; listen back the next day with fresh ears.", 15, 30),
        ],
        // [12] Songs: no built-ins; songs are added by the user.
    };

    private static readonly (int Id, string Name, string Notes)[] Tunings =
    [
        (1, "Standard",          "E2 A2 D3 G3 B3 E4"),
        (2, "Drop D",            "D2 A2 D3 G3 B3 E4"),
        (3, "D Standard",        "D2 G2 C3 F3 A3 D4"),
        (4, "Drop C",            "C2 G2 C3 F3 A3 D4"),
        (5, "B Standard",        "B1 E2 A2 D3 F#3 B3"),
        (6, "7-String Standard", "B1 E2 A2 D3 G3 B3 E4"),
    ];

    public static void Seed(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>().HasData(Categories.Select(c => new Category
        {
            Id = c.Id,
            Name = c.Name,
            SortOrder = c.Sort,
            ColorHex = c.Color,
            Kind = c.Kind,
            IsBuiltIn = true,
        }));

        modelBuilder.Entity<Subcategory>().HasData(Subcategories.SelectMany(kv => kv.Value.Select((s, i) => new Subcategory
        {
            Id = kv.Key * 100 + i + 1,
            CategoryId = kv.Key,
            Name = s.Name,
            Notes = s.Notes,
            SortOrder = i + 1,
            MinSessionMinutes = s.Min,
            MaxSessionMinutes = s.Max,
            StartBpm = s.StartBpm,
            TargetBpm = s.TargetBpm,
            Pin = s.Pin,
            PinnedMinutes = s.PinnedMinutes,
            IsBuiltIn = true,
        })));

        modelBuilder.Entity<Tuning>().HasData(Tunings.Select(t => new Tuning
        {
            Id = t.Id,
            Name = t.Name,
            Notes = t.Notes,
            SortOrder = t.Id,
            IsBuiltIn = true,
        }));
    }
}
