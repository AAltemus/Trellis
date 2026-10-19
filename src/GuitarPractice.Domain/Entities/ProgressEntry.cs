namespace GuitarPractice.Domain;

/// <summary>
/// What one BPM counts: the note value played at that tempo (the number is notes per beat). Same numbers and names as
/// the metronome's <c>Subdivision</c>, which "Practice with metronome" casts it to: add a value to both (and to the
/// ProgressExercises NoteValue check, which needs a migration).
/// </summary>
public enum NoteValue
{
    Quarters = 1,
    Eighths = 2,
    Triplets = 3,
    Sixteenths = 4,
    Quintuplets = 5,
    Sextuplets = 6,
    Septuplets = 7,
    ThirtySeconds = 8,
}

/// <summary>
/// How one practice block (a subcategory) went on one day: the exercises played in it (each with tempo and note
/// value), a 1–5 rating and notes. Keyed by (Date, SubcategoryId) like <see cref="PracticeLog"/> but kept separate on
/// purpose: loading a saved week clears that week's practice time, and must never wipe progress history.
/// </summary>
public class ProgressEntry
{
    public const int MaxNotesLength = 2000;

    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int SubcategoryId { get; set; }
    public Subcategory Subcategory { get; set; } = null!;

    /// <summary>1 (rough) to 5 (clean), for the block as a whole.</summary>
    public int? Rating { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>The exercises played in the block, in the order they were logged.</summary>
    public List<ProgressExercise> Exercises { get; } = [];

    public bool IsEmpty => Exercises.Count == 0 && Rating is null && string.IsNullOrWhiteSpace(Notes);
}

/// <summary>
/// The user's own tempo goal for one exercise in a block. <see cref="Key"/> is the exercise identity (normalised name +
/// note value, see <c>ProgressStats.ExerciseKey</c>); exercises without one fall back to the block's
/// <see cref="Subcategory.TargetBpm"/>.
/// </summary>
public class ExerciseTarget
{
    public int Id { get; set; }
    public int SubcategoryId { get; set; }
    public Subcategory Subcategory { get; set; } = null!;
    public required string Key { get; set; }
    public int TargetBpm { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One exercise played within a block, e.g. "3nps scale" at 132 BPM in sixteenths.</summary>
public class ProgressExercise
{
    public const int MaxNameLength = 100;
    public const int MinBpm = 1;
    public const int MaxBpm = 400;

    public int Id { get; set; }
    public int ProgressEntryId { get; set; }
    public ProgressEntry ProgressEntry { get; set; } = null!;

    /// <summary>Free text; empty only for entries migrated from the single-BPM version.</summary>
    public string Name { get; set; } = "";

    public int? Bpm { get; set; }
    public NoteValue? NoteValue { get; set; }
    public int SortOrder { get; set; }

    /// <summary>
    /// Null = part of the exercise's current run. A number = the line belonged to a run the user removed from the block
    /// (1 = first removal of that name + note value, 2 = second …): kept as history, left out of current progress, so
    /// logging the same name again starts over.
    /// </summary>
    public int? RemovedRun { get; set; }
}
