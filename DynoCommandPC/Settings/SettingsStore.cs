using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynoCommandPC.Settings;

/// <summary>Loads/saves AppSettings as JSON next to the executable (portable).</summary>
public static class SettingsStore
{
    // Directory the app runs from — keeps settings.json beside the .exe.
    private static readonly string Dir = AppContext.BaseDirectory;

    public static string FileLocation { get; } = Path.Combine(Dir, "settings.json");

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FileLocation))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FileLocation), Opts) ?? new AppSettings();
        }
        catch { /* fall through to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FileLocation, JsonSerializer.Serialize(s, Opts));
        }
        catch { /* best effort */ }
    }

    /// <summary>Deep copy via round-trip, for editing in a dialog without committing.</summary>
    public static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, Opts), Opts)!;
}
