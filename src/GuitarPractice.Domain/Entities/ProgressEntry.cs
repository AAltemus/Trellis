namespace GuitarPractice.Domain;

/// <summary>
/// How one exercise went on one day: tempo reached, a 1–5 rating, notes. Keyed by (Date, SubcategoryId) like
/// <see cref="PracticeLog"/> but kept separate on purpose: loading a saved week clears that week's practice time,
/// and must never wipe progress history.
/// </summary>
public class ProgressEntry
{
    public const int MaxNotesLength = 2000;
    public const int MinBpm = 1;
    public const int MaxBpm = 400;

    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int SubcategoryId { get; set; }
    public Subcategory Subcategory { get; set; } = null!;

    /// <summary>Tempo played cleanly that day.</summary>
    public int? BpmReached { get; set; }

    /// <summary>1 (rough) to 5 (clean).</summary>
    public int? Rating { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public bool IsEmpty => BpmReached is null && Rating is null && string.IsNullOrWhiteSpace(Notes);
}
