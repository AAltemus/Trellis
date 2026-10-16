namespace GuitarPractice.Domain;

/// <summary>A UI preference stored as JSON under a key (e.g. "metronome"). Not part of plans or history.</summary>
public class AppSetting
{
    public required string Key { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
