<img src="src/GuitarPractice.App/Assets/Icon/trellis-256.png" alt="" width="96" align="right">

# Trellis: guitar practice

A routine to grow your playing along. Trellis turns a weighted selection of practice tasks into a weekly plan and
times each session, with the tools you'd otherwise switch apps for in one place: metronome (clicks or drums), tuner and
bend trainer, recorder with looping, ear trainer, progress tracking, and a MIDI player for playing along with backing
tracks: import songs exported from Guitar Pro (or any MIDI file), slow them down without changing pitch, mute your
part, loop the bars you're working on, and link a song to a practice item so it's one click away. Cross-platform
desktop app (Windows / macOS / Linux); C# / .NET 10, Avalonia 11, EF Core + SQLite.

The icon is a sprout growing up a rosewood fretboard (simplified to a "T" made of a nut and a string at 16–32 px).
Sources: `src/GuitarPractice.App/Assets/Icon/trellis.svg` and `trellis-small.svg`; rebuild the `.ico` with
`dotnet run scripts/make-icon.cs -- src/GuitarPractice.App/Assets/Icon`. The code keeps its `GuitarPractice.*`
project names and the data folder stays `GuitarPractice`.

## Library model

`Category` (Legato, Rhythm, Songs…) → `Subcategory` (the weighted, schedulable unit).

- A routine stores one weight (1–10) per selected subcategory; the planner turns weights into minutes.
- Practice minutes are set per weekday (0 = rest day), with an "All" slider to set every day at once. The library
  tree is collapsible.
- Warmup subcategories are pinned to the start of each day: they're on/off, not weighted.
- **Songs** is a category of kind `Songs`. Its subcategories are songs with artist, tuning and status
  (Learning / Polishing / Maintaining) and are weighted and scheduled like any drill.
- 78 built-in subcategories across 11 exercise categories, each with practice notes and BPM ranges where relevant.
  Hover a name in the app to see the notes. Add your own categories and subcategories from the library panel.
- Any subcategory, built-in or your own, can be removed with its ✕ (asks first). Removing hides it from the library
  and the routine but deletes nothing: its practice time and progress stay, and the Progress window still lists it
  ("removed from library") while it has history. **Removed items** at the bottom of the library lists them with
  **Restore**, which puts one back in its category at weight 0.
- A whole category can be removed too (**Remove category…** at the bottom of it, asks first) and restored from
  **Removed items** with the items it held.

## Practising

Click an item in the week (or a day's name to jump to its first unfinished item), then **Start**. The week opens
scrolled so today is in view.

- **Pause / Resume** keeps the session; switching items pauses and saves the running one.
- **Stop** finishes the item early: the time you played is kept and the rest comes off the day.
- **Reset** clears the item's logged time.
- The item and the day count down live; a ding (played through the same audio output as the metronome, so it works the
  same on every OS) plays when an item's time is up and the next unfinished item is lined up.
- Practised time is stored per (date, subcategory) in `PracticeLogs`, saved every 15 s while running, so it survives
  reshuffles, weight changes, locking and restarts (loading a saved week into a week clears it, see below). The seed
  is saved too, so the plan doesn't move between launches.
- **Log for the day** (collapsible section under the timer): the exercises you played in the block, each with BPM and
  note value (quarters, 8ths, triplets, 16ths, quintuplets, sextuplets; names you used before are suggested), plus a 1–5 rating and notes, with a one-line summary.
- **♩ Metronome** (Tools → Practice with metronome, Ctrl+Shift+M): opens the metronome for the selected item and starts
  it. It picks the exercise (the first named line in the log, else the one you logged last), starts at the tempo typed
  on that line or your latest logged tempo for it (else the block's start BPM), uses its note value as the subdivision
  and its target (own, else the block's) as the tempo trainer's target. On Stop the tempo reached is written into the
  log strip (same exercise line, else a new one); press Save log to keep it.
- **🎙 Record** (Ctrl+Shift+R): opens the Recorder for the selected item; new takes are attached to it, that day and
  the first exercise in the log, and show up in Progress for that day. The button counts the day's takes.
- Items left out of progress tracking (Progress → What's tracked) show a one-line note instead of the log.

## Weeks (Week menu)

- **◀ / ▶** (Alt+Left / Alt+Right) move between weeks; **Back to this week** (Alt+Home) returns. Unlocked weeks are
  generated from the routine with its saved seed, so previewing next week shows exactly what it will get. The routine
  is shared: weight changes apply to every unlocked week.
- **Lock week** (Ctrl+L) freezes the displayed plan. Weight/minute changes are still saved to the routine but a locked
  week doesn't regenerate until you unlock it.
- **Save week as…** (Ctrl+S) stores the displayed week under a name (same name replaces). Only the plan is saved,
  never practised time.
- **Load saved week** copies a saved week onto the displayed week, weekday for weekday, and locks it.
  **Load saved week into next week** does the same for the coming week; a notice shows what's queued and
  **Clear next week** removes it.
- Overwrite protection: you can't load into a locked week (the menu says "unlock … first"). Unlock, then load.
- **Delete saved week…** asks for confirmation first.
- Loading starts the week fresh: practised time logged on that week's dates is cleared. Locking, unlocking, saving
  and deleting never touch practised time.

## Tools menu

- **Metronome…** (Ctrl+M): 20–300 BPM, tap tempo, 1–12 beats per bar, quarter/eighth/triplet/sixteenth/quintuplet/sextuplet subdivisions,
  accent on beat 1, volume. Clicks are scheduled by counting audio samples (no timer jitter, no drift).
  - *Gap training*: play N bars, mute M bars; the beat lights keep going (hollow) so you can check your time.
  - *Tempo trainer*: +X BPM every N bars up to a target, stepping exactly on the downbeat.
  - *Sound*: the beep click or **drums** (kick, snare, closed hi-hat, synthesised). In drum mode a step grid shows one
    column per step of the bar (beats per bar × subdivision) with a row per drum: click cells to put each drum where you
    want it; the step being played is outlined. Built-in patterns (Basic rock, Four on the floor, 16th hats, Shuffle,
    Waltz, Just hats) are a starting point; name your grid and **Save** it (same name updates it; built-in names are
    reserved), **Clear** starts from empty, **Delete** removes a saved pattern (asks first). Changing beats or
    subdivision resizes the grid, keeping the hits that still line up; picking a pattern sets the meter. Gap training
    and the tempo trainer work the same with drums; the accent option applies to the click only.
  - Keys: Space start/stop, ↑/↓ ±1, PgUp/PgDn ±5, T tap (not while typing in a box). Settings, the sound and the
    current grid are remembered; saved patterns live in the database.
  - Opened with ♩ Metronome from the timer, a "Playing for: …" banner shows the linked item; ✕ unlinks it.
  - *Output*: pick the device, listed once per audio driver with that driver's latency; the lowest number starts the
    click soonest (e.g. ~11 ms instead of ~110 ms on Windows' default driver). Remembered by name.
- **Tuner…** (Ctrl+T): YIN pitch detection (±1 cent in tests), needle ±50 ¢, tunings from the library (Standard,
  Drop D, D Standard, Drop C, B Standard, 7-string) or chromatic, adjustable A4, input device/channel picker
  (pick your interface's guitar input; the system default may be a webcam mic). Settings are remembered.
  - *Bend trainer*: play a fretted note, bend it ½ / 1 / 1½ / 2 steps; the needle shows cents from the target and each
    held bend is scored (average offset while held, glide in/out excluded).
  - Level readout in dBFS and **Save last 20 s** (raw input as WAV, for troubleshooting).
  - *Sensitivity* (−40 … −80 dBFS): for low-gain inputs, slide right until picked notes register but the idle input
    doesn't. *Strobe display*: striped bands under the needle drift right when sharp, left when flat, stand still in tune.
- **Recorder…** (Ctrl+R), two tabs:
  - *Takes*: record from your input, play back on a waveform (click to jump, drag to select a loop), loop an A–B region
    seamlessly with an optional 4-click count-in, **Speed** 50–100 % without changing pitch (also while playing; the
    loop and count-in follow), rename/delete takes. Attach a take to a practice item, day and (optionally) exercise;
    Progress lists each day's takes with an **Open** button. Takes are WAV files in
    `%LOCALAPPDATA%\GuitarPractice\recordings`.
  - *Looper*: a loop of 1–16 bars at a tempo, with its own click and a one-bar count-in. **● Record layer** records the
    next whole pass as a layer (up to 8; press again while recording to record the pass after it too), **Undo layer**,
    **Clear**, **Save as take** mixes the layers into a take. **Calibrate** measures your setup's round-trip latency
    once (8 clicks out, heard back on the input: pickups or a mic near the speakers, a cable, or an interface loopback
    input) so layers land on the beat; a slider fine-tunes it.
  - To hear yourself while playing along, use your interface's direct monitoring (the app doesn't route the live input
    to the output).
- **Ear Trainer…** (Ctrl+E): hear an interval or a chord, pick what it was, get scored. Choose the key (any of 12
  tonics, major or natural minor) and tick exactly what to practise: the 12 intervals (m2 … P8; ascending, descending,
  together or mixed; from the key's root or from any note of the key) or the key's diatonic chords (I … vii° triads
  and Imaj7 … viiø7 sevenths). A chord question is just that chord: name it by degree and quality, e.g. in C
  major "ii · Dm (minor)", the 2 chord, a minor chord (K plays the key chord if you want a reference). Intervals can
  play the key chord first. Only the first answer to each question scores; after answering, click any answer to hear
  it for comparison. Score, streak and
  per-item results for the round, plus all-time accuracy per item (kept between sessions; chords per mode, so V7 in
  any major key counts together). Keys: Enter/N next, Space/R replay, K key chord, 1–9/0 answer. Notes are a
  synthesised plucked string, in tune to ±2 cents.
  - More chord types built from the key's notes, offered only on the degrees where they're real chords: sus2, sus4,
    add9, 6th and 9th chords (e.g. in C major Vsus4 but no IVsus4).
  - *Melodies* (dictation): hear a 3–8 note melody in the key (after the key chord) and write it down with the degree
    buttons 1–8 (or keys 1–8, Backspace, M to hear yours); scored per note and per melody.
  - **Log score** adds the round's result as a line in today's notes for an Ear Training block (chosen in a list).
- **MIDI Player…** (Ctrl+I): play along with MIDI files, e.g. Guitar Pro → File → Export → MIDI. **Import MIDI…** (or drop
  .mid files on the window) copies them into Trellis's library (`%LOCALAPPDATA%\GuitarPractice\midi`) so they're there
  every time; rename or delete them there (your original is never touched). Sounds come from the bundled GeneralUser GS
  SoundFont (see `src/GuitarPractice.App/Assets/SoundFont/README.md`). Practice controls: **Speed** 25–150 % (pitch
  unchanged), **Mute** a track to play that part yourself or **Solo** one to learn it, **Loop bars** (or *Loop this
  bar*, or **All** for the whole song), a one-bar **Count-in** at the song's tempo (optionally before every loop
  pass), volume (0–200 %; a soft limiter keeps it clean) and a volume per track. A bar panel shows the bar and beat
  playing now and a map of the bars (loop tinted, click a bar to jump there). Each file remembers its speed, loop,
  count-in, volumes and muted/soloed tracks. Keys: Space play/pause, Home back to the start (or loop start).
  - **♫ Play along** (timer row; Tools → Play along with MIDI, Ctrl+Shift+I) opens the player for the selected practice
    item; press **Link** to attach the current file to it, so next time it opens straight away.
- **Progress…** (Ctrl+P): per block: streak, recent rating, minutes practised; per exercise (name + note value): best
  and latest BPM and progress toward its target. Set your own targets per exercise, or a block target as the fallback.
  A BPM chart with a line per exercise (up to four) and dashed target lines, a minutes-per-day chart, and a day table
  you can edit for any date. While it's open it follows the block you select in the week.
  **Remove** (asks first) stops tracking an exercise: it leaves the list and the chart, but nothing is deleted; its
  lines stay in the day table marked "(removed)" and it's listed under "Removed exercises (history kept)". Logging the
  same name again starts it over from scratch (its own target, if set, stays).
  **Rename** fixes an exercise's name; typing another exercise's name (same note value) merges the two.
  **What's tracked** (bottom left) leaves categories or single blocks out of tracking, e.g. Warmup or Theory; their
  history is kept. The day table shows Recorder takes attached to each day (🎙 n).

The metronome, tuner and recorder each open their own audio stream, so they can run at the same time.

**File → Settings…** (Ctrl+,): Appearance → Theme: Light (default) or Dark. It never follows the OS setting, so the app
looks the same on every platform.
It applies to every open window right away and is remembered.

Every window reopens where you left it: the main window, Progress, Recorder and Ear Trainer at their last size and
position (and maximized if they were); the Metronome and Tuner, which size themselves, at their last position. If a saved
spot is no longer on any screen (e.g. a monitor was unplugged), the window opens centred, shrunk to fit if needed.

## First run

Requires the .NET 10 SDK.

```powershell
powershell -ExecutionPolicy Bypass -File scripts\bootstrap.ps1   # Windows (-Reset to rebuild migrations + DB)
./scripts/bootstrap.sh                                            # macOS / Linux
dotnet run --project src/GuitarPractice.App
```

The bootstrap restores `dotnet-ef`, creates the `InitialCreate` migration (built-in library + tunings
are seeded through it), builds, and runs the tests. Commit the generated `Persistence/Migrations` folder.

The database lives at `%LOCALAPPDATA%\GuitarPractice\practice.db` (Windows) or
`~/Library/Application Support/GuitarPractice/practice.db` (macOS) or `~/.local/share/GuitarPractice/practice.db` (Linux). It is backed up to `practice.db.bak` before any
migration runs. Delete it to start fresh.

## Layout

| Project | Role |
|---|---|
| `GuitarPractice.Domain` | Entities, the planner, the practice timer state machine. No package dependencies; warnings are errors. |
| `GuitarPractice.Infrastructure` | `PracticeDbContext`, entity configs, seed data, migrations, stores (incl. the MIDI library), startup migration/backup. |
| `GuitarPractice.Audio` | PortAudio input/output/full-duplex, metronome engine, YIN tuner pipeline, bend tracker, recorder, time-stretch, overdub looper + latency calibration, MIDI reader + player (MeltySynth), notification ding. |
| `GuitarPractice.App` | Avalonia UI: library/routine panel, week view, session timer, Week and Tools menus, tool windows (metronome, tuner, recorder, ear trainer, MIDI player, progress). |
| `GuitarPractice.Domain.Tests` | xUnit + FsCheck: planner properties and examples, progress math, week cloning, timer. |
| `GuitarPractice.Infrastructure.Tests` | xUnit on in-memory SQLite: stores, week rules, settings, MIDI library, migrations up/down. |
| `GuitarPractice.Audio.Tests` | xUnit: metronome timing (sample positions, no drift, no allocation), YIN accuracy, tuner, bends, drums, MIDI reading and playback (incl. the real SoundFont). |
## Planner

`WeeklyPlanner.Generate(PlanRequest) → PlanResult` is a pure, deterministic function.

1. **Day split.** Pinned tasks (warmup) are carved out of each day's budget, or added on top when
   `PinnedTimeOnTopOfBudget` is set. Rest days (0 min) get nothing, not even warmup.
2. **Weekly allocation.** Largest-remainder (Hamilton) apportionment of flexible quanta by weight, in exact
   integer arithmetic, then rebalanced so every task reaches its `MinSessionMinutes` for the week.
3. **Sessions and spacing.** Session count comes from `MaxSessionMinutes`. Preferred days are chosen on an
   evenly spaced lattice with a seeded phase, so 3 sessions land Mon/Wed/Fri-ish.
4. **Exact fill.** A convex min-cost transportation problem (successive shortest paths) fills every day to
   the minute and balances session sizes.
5. **Repair.** Cross swaps between task/day pairs keep every day total and task total intact while removing
   under-min and over-max sessions, then reducing sessions on consecutive days.
6. **Ordering.** Pinned-first, then flexible tasks by category `SortOrder`, then pinned-last.

Property-tested invariants:
- Day totals match the budget mode.
- Weekly totals are within one quantum of the weighted ideal.
- No task appears twice in a day.
- Zero-weight tasks never appear.
- The same seed produces the same plan.
- Pinned tasks come first and last.

Worst case is about 16 ms per plan (40 tasks, 4 h/day), so the preview can regenerate on every slider change.

## Milestones

- [x] **M1** Domain model, planner and property tests; solution scaffold
- [x] **M2** Library and routine editing (categories, subcategories, songs, weights, per-weekday minutes)
- [~] **M3** Week view and session runner, week lock/save/load/navigation — drag between days not built
- [x] **M4** PortAudio backend and sample-accurate metronome (gap training, tempo trainer) — macOS packaging/mic entitlement not proven yet
- [x] **M5** YIN tuner with DSP tests; bend-accuracy mode
- [x] Recorder (record, playback, A–B loop with count-in) and progress tracking (exercises per block with BPM and note
  value, own targets, rating, notes; charts; remove an exercise keeping its history)
- [x] Metronome drums (kick/snare/hat step grid, saved patterns); practice-with-metronome for the selected item
- [x] Ear trainer (intervals and diatonic chords in a chosen key, scored)
- [x] MIDI player for playing along (Guitar Pro exports, speed, mute/solo, bar loops, count-in, linked to practice items)
- [x] Trellis name and icon; Settings (Light/Dark theme); windows remember their size and position
- [x] Recorder slow-down (50–100 %, pitch kept), takes attached to practice items, overdub looper with latency calibration
- [x] Tuner sensitivity + strobe; ear trainer sus/add9/6/9th chords, melodic dictation, score logging; metronome output
  picker; MIDI per-track volume and bar map; category remove/restore; exercise rename/merge; choose what Progress tracks
- [ ] **M6** Velopack installers and release pipeline (a CI build/test matrix exists)

Development notes for Claude sessions (`plans/`, `CLAUDE.md`) are kept locally and are not in this repository.