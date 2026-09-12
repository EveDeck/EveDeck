using System.Text.Json;

namespace EveDeck.VrSpike;

// Persists tuned VR geometry so a good arrangement survives a restart.
//
// Deliberately its OWN file next to EveDeck's settings, never inside settings.json -- the app owns
// that file and rewrites it on every save, so anything the spike wrote there would be destroyed
// (and a write race could cost real config).
internal sealed record VrGeometry(
    float Master = 3.0f,
    float Preview = 0.75f,
    float Dist = 1.7f,
    float Drop = 1.0f,
    float Curve = 0.18f,
    bool Flat = false,
    bool World = false);

internal static class GeometryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EveDeck",
        "vr-spike.json");

    public static VrGeometry Load(Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(Path)) return new VrGeometry();
            var json = File.ReadAllText(Path);
            var loaded = JsonSerializer.Deserialize<VrGeometry>(json);
            if (loaded is null) return new VrGeometry();
            log?.Invoke($"geometry: loaded from {Path}");
            return loaded;
        }
        catch (Exception ex)
        {
            log?.Invoke($"geometry: could not read saved geometry ({ex.GetType().Name}), using defaults");
            return new VrGeometry();
        }
    }

    public static void Save(VrGeometry geometry, Action<string>? log = null)
    {
        try
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(Path, JsonSerializer.Serialize(geometry, Options));
            log?.Invoke($"geometry: saved to {Path}");
        }
        catch (Exception ex)
        {
            log?.Invoke($"geometry: save failed ({ex.GetType().Name})");
        }
    }
}
