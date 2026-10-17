# Guitar Practice

Cross-platform desktop app (Windows / macOS / Linux) that turns a weighted selection of practice tasks
into a weekly plan, with a metronome, tuner, recorder and progress tracking. C# / .NET 10, Avalonia 11, EF Core + SQLite.

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

## Practising

Click an item in the week (or a day's name to jump to its first unfinished item), then **Start**.

- **Pause / Resume** keeps the session; switching items pauses and saves the running one.
- **Stop** finishes the item early: the time you played is kept and the rest comes off the day.
- **Reset** clears the item's logged time.
- The item and the day count down live; a ding plays when an item's time is up and the next unfinished item is lined up.
- Practised time is stored per (date, subcategory) in `PracticeLogs`, saved every 15 s while running, so it survives
  reshuffles, weight changes, locking and restarts (loading a saved week into a week clears it, see below). The seed
  is saved too, so the plan doesn't move between launches.
- **Log for the day** (collapsible section under the timer): the exercises you played in the block, each with BPM and
  note value (names you used before are suggested), plus a 1–5 rating and notes, with a one-line summary.
- **♩ Metronome** (Tools → Practice with metronome, Ctrl+Shift+M): opens the metronome for the selected item and starts
  it. It picks the exercise (the first named line in the log, else the one you logged last), starts at the tempo typed
  on that line or your latest logged tempo for it (else the block's start BPM), uses its note value as the subdivision
  and its target (own, else the block's) as the tempo trainer's target. On Stop the tempo reached is written into the
  log strip (same exercise line, else a new one); press Save log to keep it.

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

- **Metronome…** (Ctrl+M): 20–300 BPM, tap tempo, 1–12 beats per bar, quarter/eighth/triplet/sixteenth subdivisions,
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
- **Tuner…** (Ctrl+T): YIN pitch detection (±1 cent in tests), needle ±50 ¢, tunings from the library (Standard,
  Drop D, D Standard, Drop C, B Standard, 7-string) or chromatic, adjustable A4, input device/channel picker
  (pick your interface's guitar input; the system default may be a webcam mic). Settings are remembered.
  - *Bend trainer*: play a fretted note, bend it ½ / 1 / 1½ / 2 steps; the needle shows cents from the target and each
    held bend is scored (average offset while held, glide in/out excluded).
  - Level readout in dBFS and **Save last 20 s** (raw input as WAV, for troubleshooting).
- **Recorder…** (Ctrl+R): record takes from your input, play them back on a waveform (click to jump, drag to select a
  loop), loop an A–B region seamlessly with an optional 4-click count-in, rename/delete takes. Takes are WAV files in
  `%LOCALAPPDATA%\GuitarPractice\recordings`. To hear yourself while playing along, use your interface's direct
  monitoring (the app doesn't route the live input to the output).
- **Progress…** (Ctrl+P): per block: streak, recent rating, minutes practised; per exercise (name + note value): best
  and latest BPM and progress toward its target. Set your own targets per exercise, or a block target as the fallback.
  A BPM chart with a line per exercise (up to four) and dashed target lines, a minutes-per-day chart, and a day table
  you can edit for any date. While it's open it follows the block you select in the week.

The metronome, tuner and recorder each open their own audio stream, so they can run at the same time.

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
| `GuitarPractice.Infrastructure` | `PracticeDbContext`, entity configs, seed data, migrations, stores, startup migration/backup. |
| `GuitarPractice.Audio` | PortAudio input/output, metronome engine, YIN tuner pipeline, bend tracker, notification ding. |
| `GuitarPractice.App` | Avalonia UI: library/routine panel, week view, session timer, Week and Tools menus. |
| `GuitarPractice.Domain.Tests` | xUnit + FsCheck: planner properties and examples, progress math, week cloning, timer. |
| `GuitarPractice.Infrastructure.Tests` | xUnit on in-memory SQLite: stores, week rules, settings, migrations in sync. |
| `GuitarPractice.Audio.Tests` | xUnit: metronome timing (sample positions, no drift, no allocation), YIN accuracy, tuner, bends. |
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
- [x] Recorder (record, playback, A–B loop with count-in) and progress tracking (per-day BPM, rating, notes; charts)
- [ ] **M6** Velopack installers and release pipeline (a CI build/test matrix exists)

Development notes for Claude sessions (`plans/`, `CLAUDE.md`) are kept locally and are not in this repository.