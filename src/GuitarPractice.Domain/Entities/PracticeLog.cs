namespace GuitarPractice.Domain;

/// <summary>
/// Time actually practised on one subcategory on one date. Keyed by (Date, SubcategoryId) rather than by a plan
/// entry, so progress survives regenerating or reshuffling the week and restarting the app.
/// </summary>
public class PracticeLog
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public int SubcategoryId { get; set; }
    public Subcategory Subcategory { get; set; } = null!;

    public int ElapsedSeconds { get; set; }

    /// <summary>Finished: either the timer ran out or the item was stopped early.</summary>
    public bool Completed { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
