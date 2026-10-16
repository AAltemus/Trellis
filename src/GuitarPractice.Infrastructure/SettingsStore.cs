using System.Text.Json;
using GuitarPractice.Domain;
using GuitarPractice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace GuitarPractice.Infrastructure;

/// <summary>
/// UI preferences as JSON per key in the AppSettings table. Synchronous, like the other stores. A value that no longer
/// deserializes (type changed between versions) reads as missing rather than failing the caller.
/// </summary>
public sealed class SettingsStore(IDbContextFactory<PracticeDbContext> contextFactory, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public T? Get<T>(string key) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        using var db = contextFactory.CreateDbContext();
        var json = db.AppSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Json).SingleOrDefault();
        if (json is null) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Set<T>(string key, T value) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        using var db = contextFactory.CreateDbContext();
        var row = db.AppSettings.SingleOrDefault(s => s.Key == key);
        if (row is null)
        {
            row = new AppSetting { Key = key, Json = "" };
            db.AppSettings.Add(row);
        }
        row.Json = JsonSerializer.Serialize(value, Json);
        row.UpdatedAt = clock.GetUtcNow();
        db.SaveChanges();
    }
}
