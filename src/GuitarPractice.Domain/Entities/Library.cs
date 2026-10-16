namespace GuitarPractice.Domain;

/// <summary>Where a pinned subcategory sits in each practice day. Pinned items bypass weighting.</summary>
public enum PinMode
{
    None = 0,
    DailyFirst = 1,
    DailyLast = 2,
}

public enum CategoryKind
{
    /// <summary>Technique, theory, ear, creative work: subcategories are drills/topics.</summary>
    Exercise = 0,

    /// <summary>Subcategories are songs and carry song details (artist, tuning, status).</summary>
    Songs = 1,
}

public enum SongStatus
{
    Learning = 0,
    Polishing = 1,
    Maintaining = 2,
}

/// <summary>
/// A top-level exercise area (Legato, Rhythm, Songs...). Built-in and user-created categories are identical;
/// <see cref="IsBuiltIn"/> only scopes "reset to defaults".
/// </summary>
public class Category
{
    public int Id { get; set; }
    public required string Name { get; set; }

    /// <summary>Library ordering and in-day ordering (technique while fresh, creative work last).</summary>
    public int SortOrder { get; set; }

    public string? ColorHex { get; set; }
    public CategoryKind Kind { get; set; } = CategoryKind.Exercise;
    public bool IsBuiltIn { get; set; }

    /// <summary>Soft delete: archived items disappear from pickers but old plans keep integrity.</summary>
    public bool IsArchived { get; set; }

    public List<Subcategory> Subcategories { get; } = [];
}

/// <summary>
/// The weighted, schedulable unit. Routines put a weight on subcategories and the planner turns those
/// weights into practice time. Built-in drills, user-made subcategories and songs all live here.
/// </summary>
public class Subcategory
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category Category { get; set; } = null!;

    /// <summary>Drill/topic name, or the song title for Songs categories.</summary>
    public required string Name { get; set; }

    public string? Notes { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Smallest useful session; also the weekly minimum guaranteed to any selected subcategory.</summary>
    public int MinSessionMinutes { get; set; } = 5;

    /// <summary>Soft cap per session; above it the planner prefers splitting across more days.</summary>
    public int MaxSessionMinutes { get; set; } = 20;

    public int? StartBpm { get; set; }
    public int? TargetBpm { get; set; }

    public PinMode Pin { get; set; } = PinMode.None;

    /// <summary>Daily minutes for a pinned subcategory. Falls back to <see cref="MinSessionMinutes"/>.</summary>
    public int? PinnedMinutes { get; set; }

    public bool IsBuiltIn { get; set; }
    public bool IsArchived { get; set; }

    // ---- Song details (Category.Kind == Songs); null for exercises ----
    public string? Artist { get; set; }
    public int? TuningId { get; set; }
    public Tuning? Tuning { get; set; }
    public string? SongKey { get; set; }
    public string? ReferenceUrl { get; set; }
    public SongStatus? Status { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Artist) ? Name : $"{Name} — {Artist}";
}

/// <summary>A tuning for the tuner and for songs. Notes are scientific pitch, low string first, space-separated.</summary>
public class Tuning
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Notes { get; set; }
    public int SortOrder { get; set; }
    public bool IsBuiltIn { get; set; }

    public string[] NoteNames => Notes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
