# Guitar Practice

Cross-platform desktop app (Windows / macOS / Linux) that turns a weighted selection of practice tasks
into a weekly plan, with a tuner and metronome. C# / .NET 10, Avalonia 11, EF Core + SQLite.

## Library model

`Category` (Legato, Rhythm, Songs…) → `Subcategory` (the weighted, schedulable unit).

- A routine stores one weight (1–10) per selected subcategory; the planner turns weights into minutes.
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
- Loading starts the week fresh: practised time logged on that week's dates is cleared. Locking, unlocking, saving
  and deleting never touch practised time.

## Tools menu

- **Metronome…** (Ctrl+M): 20–300 BPM, tap tempo, 1–12 beats per bar, quarter/eighth/triplet/sixteenth subdivisions,
  accent on beat 1, volume. Clicks are scheduled by counting audio samples (no timer jitter, no drift).
  - *Gap training*: play N bars, mute M bars; the beat lights keep going (hollow) so you can check your time.
  - *Tempo trainer*: +X BPM every N bars up to a target, stepping exactly on the downbeat.
  - Keys: Space start/stop, ↑/↓ ±1, PgUp/PgDn ±5, T tap. Settings are remembered.
- **Tuner…** (Ctrl+T): YIN pitch detection (±1 cent in tests), needle ±50 ¢, tunings from the library (Standard,
  Drop D, D Standard, Drop C, B Standard, 7-string) or chromatic, adjustable A4, input device/channel picker
  (pick your interface's guitar input; the system default may be a webcam mic). Settings are remembered.
  - *Bend trainer*: play a fretted note, bend it ½ / 1 / 1½ / 2 steps; the needle shows cents from the target and each
    held bend is scored (average offset while held, glide in/out excluded).
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
- [~] **M2** Library and routine editing (categories, subcategories, songs, weights) — per-weekday budgets in the UI still open
- [~] **M3** Week view and session runner, week lock/save/load/navigation — drag between days not built
- [x] **M4** PortAudio backend and sample-accurate metronome (gap training, tempo trainer) — macOS packaging/mic entitlement not proven yet
- [x] **M5** YIN tuner with DSP tests; bend-accuracy mode
- [ ] **M6** Velopack installers and release pipeline (a CI build/test matrix exists)

Development notes for Claude sessions (`plans/`, `CLAUDE.md`) are kept locally and are not in this repository.