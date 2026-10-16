using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace GuitarPractice.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Categories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    ColorHex = table.Column<string>(type: "TEXT", maxLength: 9, nullable: true),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Routines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    QuantumMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxTasksPerDay = table.Column<int>(type: "INTEGER", nullable: false),
                    PinnedTimeOnTopOfBudget = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Routines", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tunings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tunings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RoutineDayBudgets",
                columns: table => new
                {
                    RoutineId = table.Column<int>(type: "INTEGER", nullable: false),
                    Day = table.Column<int>(type: "INTEGER", nullable: false),
                    Minutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutineDayBudgets", x => new { x.RoutineId, x.Day });
                    table.CheckConstraint("CK_RoutineDayBudget_Minutes", "Minutes >= 0");
                    table.ForeignKey(
                        name: "FK_RoutineDayBudgets_Routines_RoutineId",
                        column: x => x.RoutineId,
                        principalTable: "Routines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WeeklyPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeekStart = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    Seed = table.Column<int>(type: "INTEGER", nullable: false),
                    RoutineId = table.Column<int>(type: "INTEGER", nullable: true),
                    RoutineSnapshotJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WeeklyPlans", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WeeklyPlans_Routines_RoutineId",
                        column: x => x.RoutineId,
                        principalTable: "Routines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "Subcategories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    MinSessionMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxSessionMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    StartBpm = table.Column<int>(type: "INTEGER", nullable: true),
                    TargetBpm = table.Column<int>(type: "INTEGER", nullable: true),
                    Pin = table.Column<int>(type: "INTEGER", nullable: false),
                    PinnedMinutes = table.Column<int>(type: "INTEGER", nullable: true),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsArchived = table.Column<bool>(type: "INTEGER", nullable: false),
                    Artist = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    TuningId = table.Column<int>(type: "INTEGER", nullable: true),
                    SongKey = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    ReferenceUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Subcategories", x => x.Id);
                    table.CheckConstraint("CK_Subcategory_SessionRange", "MinSessionMinutes > 0 AND MaxSessionMinutes >= MinSessionMinutes");
                    table.ForeignKey(
                        name: "FK_Subcategories_Categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "Categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Subcategories_Tunings_TuningId",
                        column: x => x.TuningId,
                        principalTable: "Tunings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "PlanDays",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WeeklyPlanId = table.Column<int>(type: "INTEGER", nullable: false),
                    Date = table.Column<DateOnly>(type: "TEXT", nullable: false),
                    BudgetMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    PinnedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    FlexMinutes = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanDays", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanDays_WeeklyPlans_WeeklyPlanId",
                        column: x => x.WeeklyPlanId,
                        principalTable: "WeeklyPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RoutineItems",
                columns: table => new
                {
                    RoutineId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubcategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Weight = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RoutineItems", x => new { x.RoutineId, x.SubcategoryId });
                    table.CheckConstraint("CK_RoutineItem_Weight", "Weight BETWEEN 0 AND 10");
                    table.ForeignKey(
                        name: "FK_RoutineItems_Routines_RoutineId",
                        column: x => x.RoutineId,
                        principalTable: "Routines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RoutineItems_Subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "Subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PlanDayId = table.Column<int>(type: "INTEGER", nullable: false),
                    SubcategoryId = table.Column<int>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false),
                    PlannedMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    IsPinned = table.Column<bool>(type: "INTEGER", nullable: false),
                    ActualSeconds = table.Column<int>(type: "INTEGER", nullable: true),
                    BpmAchieved = table.Column<int>(type: "INTEGER", nullable: true),
                    Completed = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanEntries_PlanDays_PlanDayId",
                        column: x => x.PlanDayId,
                        principalTable: "PlanDays",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PlanEntries_Subcategories_SubcategoryId",
                        column: x => x.SubcategoryId,
                        principalTable: "Subcategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "Categories",
                columns: new[] { "Id", "ColorHex", "IsArchived", "IsBuiltIn", "Kind", "Name", "SortOrder" },
                values: new object[,]
                {
                    { 1, "#9E9E9E", false, true, 0, "Warmup", 0 },
                    { 2, "#E53935", false, true, 0, "Alternate Picking", 10 },
                    { 3, "#FB8C00", false, true, 0, "Economy Picking", 20 },
                    { 4, "#FDD835", false, true, 0, "Sweep Picking", 30 },
                    { 5, "#43A047", false, true, 0, "Legato", 40 },
                    { 6, "#00ACC1", false, true, 0, "Bending & Vibrato", 50 },
                    { 7, "#1E88E5", false, true, 0, "Rhythm", 60 },
                    { 8, "#5E35B1", false, true, 0, "Theory", 70 },
                    { 9, "#8E24AA", false, true, 0, "Ear Training", 80 },
                    { 10, "#D81B60", false, true, 0, "Improvisation", 90 },
                    { 11, "#6D4C41", false, true, 0, "Composition", 100 },
                    { 12, "#F06292", false, true, 1, "Songs", 85 }
                });

            migrationBuilder.InsertData(
                table: "Tunings",
                columns: new[] { "Id", "IsBuiltIn", "Name", "Notes", "SortOrder" },
                values: new object[,]
                {
                    { 1, true, "Standard", "E2 A2 D3 G3 B3 E4", 1 },
                    { 2, true, "Drop D", "D2 A2 D3 G3 B3 E4", 2 },
                    { 3, true, "D Standard", "D2 G2 C3 F3 A3 D4", 3 },
                    { 4, true, "Drop C", "C2 G2 C3 F3 A3 D4", 4 },
                    { 5, true, "B Standard", "B1 E2 A2 D3 F#3 B3", 5 },
                    { 6, true, "7-String Standard", "B1 E2 A2 D3 G3 B3 E4", 6 }
                });

            migrationBuilder.InsertData(
                table: "Subcategories",
                columns: new[] { "Id", "Artist", "CategoryId", "IsArchived", "IsBuiltIn", "MaxSessionMinutes", "MinSessionMinutes", "Name", "Notes", "Pin", "PinnedMinutes", "ReferenceUrl", "SongKey", "SortOrder", "StartBpm", "Status", "TargetBpm", "TuningId" },
                values: new object[,]
                {
                    { 101, null, 1, false, true, 10, 5, "Chromatic 1-2-3-4", "One finger per fret across all six strings, up and back. Strict alternate picking, even volume.", 1, 5, null, null, 1, 60, null, 120, null },
                    { 102, null, 1, false, true, 10, 5, "Finger permutations", "Cycle the 24 orderings of fingers 1-2-3-4 (1-3-2-4, 1-4-2-3…), two or three per day.", 1, 5, null, null, 2, 60, null, 110, null },
                    { 103, null, 1, false, true, 10, 5, "Spider walk", "Fingers move in pairs across adjacent strings. Keep unused fingers hovering close to the frets.", 1, 5, null, null, 3, 50, null, 100, null },
                    { 104, null, 1, false, true, 10, 5, "Open-string picking", "Picking hand only: 8ths, triplets and 16ths on open strings to sync wrist and pick depth.", 1, 5, null, null, 4, 80, null, 160, null },
                    { 105, null, 1, false, true, 10, 5, "Slow stretches", "Wide spans (1-3-5, 1-4-7) high on the neck, moving lower as the hand loosens. Stop if anything hurts.", 1, 5, null, null, 5, null, null, null, null },
                    { 106, null, 1, false, true, 10, 5, "Slow scale run-through", "One scale through every position at a relaxed tempo, focusing on clean fretting.", 1, 5, null, null, 6, 60, null, 100, null },
                    { 201, null, 2, false, true, 20, 10, "Single-string 16ths & tremolo", "Constant down-up on one string, then tremolo bursts. Small motions, consistent pick depth.", 0, null, null, null, 1, 90, null, 200, null },
                    { 202, null, 2, false, true, 20, 10, "Inside/outside string crossing", "Two-string patterns that isolate picking between the strings (inside) and around them (outside).", 0, null, null, null, 2, 70, null, 160, null },
                    { 203, null, 2, false, true, 20, 10, "Scale sequences (3s, 4s, 6ths)", "Major and minor scales in sequences; accent the start of each group.", 0, null, null, null, 3, 70, null, 160, null },
                    { 204, null, 2, false, true, 20, 10, "Pentatonic runs (2 notes per string)", "Even-numbered string changes make every crossing the same; master the outside cross.", 0, null, null, null, 4, 80, null, 180, null },
                    { 205, null, 2, false, true, 20, 10, "3nps scale runs", "Three notes per string through all seven positions with strict alternate picking.", 0, null, null, null, 5, 70, null, 160, null },
                    { 206, null, 2, false, true, 20, 10, "Odd groupings (5s, 6s, 7s)", "Note groups that cross the beat. Accent the first note of each group against the click.", 0, null, null, null, 6, 60, null, 140, null },
                    { 207, null, 2, false, true, 20, 10, "Pedal-tone licks", "Alternate a moving line against a fixed open or fretted note.", 0, null, null, null, 7, 80, null, 170, null },
                    { 208, null, 2, false, true, 15, 5, "Speed bursts", "Short 2–4 note bursts above your clean tempo, then rest. Builds top speed without tension.", 0, null, null, null, 8, 100, null, 220, null },
                    { 301, null, 3, false, true, 20, 10, "3nps economy scales", "Sweep through every string change in the direction of travel; alternate pick within a string.", 0, null, null, null, 1, 70, null, 160, null },
                    { 302, null, 3, false, true, 20, 10, "Two-string economy patterns", "Odd note counts on two strings so every crossing is a sweep.", 0, null, null, null, 2, 70, null, 160, null },
                    { 303, null, 3, false, true, 20, 10, "Five-note pentatonic sequences", "Pentatonic fragments that force directional pick changes.", 0, null, null, null, 3, 70, null, 150, null },
                    { 304, null, 3, false, true, 20, 10, "Economy arpeggio runs", "Mixed one- and two-note-per-string arpeggios using directional picking.", 0, null, null, null, 4, 60, null, 140, null },
                    { 305, null, 3, false, true, 20, 10, "Sweep-to-pick transitions", "Move between swept arpeggio fragments and picked scale runs without changing feel.", 0, null, null, null, 5, 60, null, 140, null },
                    { 401, null, 4, false, true, 20, 10, "3-string triads", "Major, minor and diminished shapes on the top three strings. Mute everything you aren't playing.", 0, null, null, null, 1, 50, null, 130, null },
                    { 402, null, 4, false, true, 20, 10, "4-string 7th-chord arpeggios", "maj7, m7, dom7 and m7b5 shapes. Hear each chord quality as you sweep it.", 0, null, null, null, 2, 50, null, 120, null },
                    { 403, null, 4, false, true, 20, 10, "5-string major/minor arpeggios", "A-string root shapes with a pulled-off or tapped top note.", 0, null, null, null, 3, 40, null, 120, null },
                    { 404, null, 4, false, true, 20, 10, "6-string arpeggios", "E-string root shapes. Notes stay separated, never strummed.", 0, null, null, null, 4, 40, null, 110, null },
                    { 405, null, 4, false, true, 15, 5, "Rolled sweeps", "Same-fret string rolls with no two notes ringing together.", 0, null, null, null, 5, 50, null, 110, null },
                    { 406, null, 4, false, true, 20, 10, "Sweeps with tapping", "Tap the top note of the arpeggio to extend its range.", 0, null, null, null, 6, 40, null, 110, null },
                    { 407, null, 4, false, true, 20, 10, "Arpeggio progressions", "One shape per chord through a progression (e.g. i–VI–III–VII), changing in time.", 0, null, null, null, 7, 40, null, 110, null },
                    { 501, null, 5, false, true, 20, 10, "3nps hammer/pull runs", "Pick only the first note on each string; match the volume of picked and legato notes.", 0, null, null, null, 1, 60, null, 150, null },
                    { 502, null, 5, false, true, 15, 5, "Trill endurance", "One-minute trills on each finger pair (1-2, 1-3, 1-4, 2-3, 2-4, 3-4).", 0, null, null, null, 2, 80, null, 160, null },
                    { 503, null, 5, false, true, 20, 10, "Hammer-ons from nowhere", "Start each string with a fretting-hand hammer, no pick. Builds strength and string control.", 0, null, null, null, 3, 60, null, 140, null },
                    { 504, null, 5, false, true, 20, 10, "Legato scale sequences", "Sequenced 3nps lines with slides for position shifts.", 0, null, null, null, 4, 60, null, 140, null },
                    { 505, null, 5, false, true, 15, 5, "Open-string pull-offs", "Pull-off licks to open strings. Pull slightly sideways for volume.", 0, null, null, null, 5, 80, null, 160, null },
                    { 506, null, 5, false, true, 20, 10, "Slides & position shifts", "Connect positions with one-finger slides while keeping time.", 0, null, null, null, 6, 60, null, 130, null },
                    { 507, null, 5, false, true, 20, 10, "Two-hand tapping", "Tap–pull–hammer triplet cells, then moving tapped arpeggios.", 0, null, null, null, 7, 60, null, 140, null },
                    { 601, null, 6, false, true, 15, 10, "Pitch-accurate bends", "Half, whole and 1½-step bends checked against the tuner's bend mode.", 0, null, null, null, 1, null, null, null, null },
                    { 602, null, 6, false, true, 15, 10, "Pre-bends & releases", "Bend silently to pitch, pick, then release smoothly back to the fretted note.", 0, null, null, null, 2, null, null, null, null },
                    { 603, null, 6, false, true, 15, 10, "Unison & double-stop bends", "Bend the lower string up to the held note. Listen for the beating to stop.", 0, null, null, null, 3, null, null, null, null },
                    { 604, null, 6, false, true, 15, 10, "Vibrato width & speed", "Narrow/wide and slow/fast vibrato in time with the click (8ths, triplets).", 0, null, null, null, 4, 60, null, 100, null },
                    { 605, null, 6, false, true, 15, 10, "Vibrato on bent notes", "Hold a bend at pitch and add vibrato without dropping flat.", 0, null, null, null, 5, null, null, null, null },
                    { 606, null, 6, false, true, 15, 10, "Bending licks & phrasing", "Classic blues/rock bend licks; focus on intonation at the top of each bend.", 0, null, null, null, 6, null, null, null, null },
                    { 701, null, 7, false, true, 20, 10, "Downpicking endurance", "Continuous 8th-note downpicking. Lengthen each run before tension sets in.", 0, null, null, null, 1, 120, null, 220, null },
                    { 702, null, 7, false, true, 20, 10, "Palm-muted 16ths & gallops", "Alternate-picked 16ths and gallops with a consistent mute and accents.", 0, null, null, null, 2, 90, null, 200, null },
                    { 703, null, 7, false, true, 20, 10, "Strumming patterns", "16th-note strum grid on muted strings. Keep the hand moving; choose which strokes hit.", 0, null, null, null, 3, 60, null, 140, null },
                    { 704, null, 7, false, true, 20, 10, "Syncopation (click on 2 & 4)", "Metronome on 2 and 4 only; place offbeat accents and pushes.", 0, null, null, null, 4, 60, null, 140, null },
                    { 705, null, 7, false, true, 20, 10, "Subdivision changes", "Switch between quarters, 8ths, triplets and 16ths every bar without drifting.", 0, null, null, null, 5, 60, null, 120, null },
                    { 706, null, 7, false, true, 20, 10, "Odd meters (5/4, 7/8)", "Riffs and strumming in odd meters. Count out loud first.", 0, null, null, null, 6, 60, null, 140, null },
                    { 707, null, 7, false, true, 15, 5, "Chord changes", "One-minute changes: count clean switches between two chords and beat your best.", 0, null, null, null, 7, null, null, null, null },
                    { 708, null, 7, false, true, 20, 10, "Muting & tight stops", "Stop chords exactly on the beat and kill string noise with both hands.", 0, null, null, null, 8, 80, null, 180, null },
                    { 709, null, 7, false, true, 20, 10, "Locking in with drums", "Play riffs over drum loops. Record and check your attacks line up with kick and snare.", 0, null, null, null, 9, null, null, null, null },
                    { 801, null, 8, false, true, 15, 5, "Fretboard note names", "Name every note on one string per day, then find one note everywhere on the neck.", 0, null, null, null, 1, null, null, null, null },
                    { 802, null, 8, false, true, 20, 10, "Intervals on the fretboard", "Play and name intervals from any root, on one string and across strings.", 0, null, null, null, 2, null, null, null, null },
                    { 803, null, 8, false, true, 20, 10, "Major scale & modes", "Play each mode from one root and name its characteristic note.", 0, null, null, null, 3, null, null, null, null },
                    { 804, null, 8, false, true, 20, 10, "CAGED system", "Connect the five shapes for one chord, then change keys.", 0, null, null, null, 4, null, null, null, null },
                    { 805, null, 8, false, true, 20, 10, "Diatonic harmony", "Triads and 7th chords on each scale degree; play a key's chords in several positions.", 0, null, null, null, 5, null, null, null, null },
                    { 806, null, 8, false, true, 20, 10, "Chord construction & extensions", "Build sus, add9, 9th, 11th and 13th chords from formulas.", 0, null, null, null, 6, null, null, null, null },
                    { 807, null, 8, false, true, 15, 5, "Circle of fifths & keys", "Key signatures, relative minors and common modulations.", 0, null, null, null, 7, null, null, null, null },
                    { 808, null, 8, false, true, 20, 10, "Rhythm reading", "Read and play rhythms from notation: rests, ties, dotted notes, triplets.", 0, null, null, null, 8, null, null, null, null },
                    { 901, null, 9, false, true, 15, 5, "Interval recognition", "Identify melodic and harmonic intervals; sing them before checking.", 0, null, null, null, 1, null, null, null, null },
                    { 902, null, 9, false, true, 15, 5, "Chord quality ID", "Major, minor, diminished, augmented and 7th qualities by ear.", 0, null, null, null, 2, null, null, null, null },
                    { 903, null, 9, false, true, 15, 5, "Progression recognition", "Recognise common progressions (I–IV–V, ii–V–I, i–VI–III–VII) by their bass motion.", 0, null, null, null, 3, null, null, null, null },
                    { 904, null, 9, false, true, 15, 5, "Sing what you play", "Sing a note or short phrase, then find it on the guitar. Then reverse it.", 0, null, null, null, 4, null, null, null, null },
                    { 905, null, 9, false, true, 15, 5, "Call & response", "Play back short phrases from a recording or app after one listen.", 0, null, null, null, 5, null, null, null, null },
                    { 906, null, 9, false, true, 15, 5, "Find the key", "Put on a song and find its tonic and key within the first chorus.", 0, null, null, null, 6, null, null, null, null },
                    { 907, null, 9, false, true, 20, 10, "Transcription", "Transcribe a short riff or solo by ear, then check it against a tab.", 0, null, null, null, 7, null, null, null, null },
                    { 1001, null, 10, false, true, 20, 10, "Backing-track soloing", "Full solo over a track. Record it and listen back.", 0, null, null, null, 1, null, null, null, null },
                    { 1002, null, 10, false, true, 20, 10, "Target chord tones", "Land a chord tone on every chord change.", 0, null, null, null, 2, null, null, null, null },
                    { 1003, null, 10, false, true, 20, 10, "Motif development", "One short idea varied in rhythm, register and intervals.", 0, null, null, null, 3, null, null, null, null },
                    { 1004, null, 10, false, true, 20, 10, "Phrasing & space", "Call-and-response phrases; leave as much space as you play.", 0, null, null, null, 4, null, null, null, null },
                    { 1005, null, 10, false, true, 20, 10, "Limited-note soloing", "Restrict yourself to three notes or one string to force rhythmic ideas.", 0, null, null, null, 5, null, null, null, null },
                    { 1006, null, 10, false, true, 20, 10, "Modal improvisation", "Solo over a drone to hear each mode's character.", 0, null, null, null, 6, null, null, null, null },
                    { 1007, null, 10, false, true, 20, 10, "Lick vocabulary", "Learn a lick, move it through keys and vary its rhythm until it's yours.", 0, null, null, null, 7, null, null, null, null },
                    { 1008, null, 10, false, true, 20, 10, "Dynamics & articulation", "Repeat one phrase with different attack, volume, vibrato and slides.", 0, null, null, null, 8, null, null, null, null },
                    { 1101, null, 11, false, true, 30, 15, "Riff writing", "Write and record three riffs. Pick one to keep.", 0, null, null, null, 1, null, null, null, null },
                    { 1102, null, 11, false, true, 30, 15, "Chord progressions", "Write progressions in a key, then borrow chords from the parallel mode.", 0, null, null, null, 2, null, null, null, null },
                    { 1103, null, 11, false, true, 30, 15, "Melody writing", "Write a vocal-style melody over a progression and play it on guitar.", 0, null, null, null, 3, null, null, null, null },
                    { 1104, null, 11, false, true, 30, 15, "Song arrangement", "Arrange riffs into verse/chorus/bridge and demo the structure.", 0, null, null, null, 4, null, null, null, null },
                    { 1105, null, 11, false, true, 30, 15, "Solo composition", "Compose a solo with a beginning, a build and a climax; record it.", 0, null, null, null, 5, null, null, null, null },
                    { 1106, null, 11, false, true, 30, 15, "Writing to a constraint", "Give yourself a brief: odd meter, one scale, a set tempo or a mood.", 0, null, null, null, 6, null, null, null, null },
                    { 1107, null, 11, false, true, 30, 15, "Demo recording", "Record and rough-mix a demo; listen back the next day with fresh ears.", 0, null, null, null, 7, null, null, null, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Categories_SortOrder",
                table: "Categories",
                column: "SortOrder");

            migrationBuilder.CreateIndex(
                name: "IX_PlanDays_WeeklyPlanId_Date",
                table: "PlanDays",
                columns: new[] { "WeeklyPlanId", "Date" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlanEntries_PlanDayId_Order",
                table: "PlanEntries",
                columns: new[] { "PlanDayId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanEntries_SubcategoryId",
                table: "PlanEntries",
                column: "SubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_RoutineItems_SubcategoryId",
                table: "RoutineItems",
                column: "SubcategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Subcategories_CategoryId_SortOrder",
                table: "Subcategories",
                columns: new[] { "CategoryId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Subcategories_TuningId",
                table: "Subcategories",
                column: "TuningId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyPlans_RoutineId",
                table: "WeeklyPlans",
                column: "RoutineId");

            migrationBuilder.CreateIndex(
                name: "IX_WeeklyPlans_WeekStart",
                table: "WeeklyPlans",
                column: "WeekStart");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanEntries");

            migrationBuilder.DropTable(
                name: "RoutineDayBudgets");

            migrationBuilder.DropTable(
                name: "RoutineItems");

            migrationBuilder.DropTable(
                name: "PlanDays");

            migrationBuilder.DropTable(
                name: "Subcategories");

            migrationBuilder.DropTable(
                name: "WeeklyPlans");

            migrationBuilder.DropTable(
                name: "Categories");

            migrationBuilder.DropTable(
                name: "Tunings");

            migrationBuilder.DropTable(
                name: "Routines");
        }
    }
}
