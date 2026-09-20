using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Application = System.Windows.Application;
using EveDeck.Models;

namespace EveDeck.Services;

// On-disk ship-icon cache, mirroring PortraitCacheService's shape exactly: download once to
// %LOCALAPPDATA%\EveDeck\cache\ship-icons\{typeId}.png with HttpClient (never WinINET), hand every
// surface a single shared observable ShipIcon per type id so a newly cached icon appears everywhere
// bound to it at once. Unlike a portrait, a hull's icon never changes -- there is no TTL here, only
// "have we ever fetched this one".
public sealed class ShipIconCacheService
{
    public static ShipIconCacheService Instance { get; } = new();

    private const int IconSize = 64;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly string _dir;
    private readonly ConcurrentDictionary<int, ShipIcon> _byTypeId = new();
    private readonly ConcurrentDictionary<int, byte> _inflight = new();

    // Raised (on the UI thread) whenever an icon lands, for surfaces that aren't data-bound to the
    // shared ShipIcon directly.
    public event Action? Changed;

    private ShipIconCacheService()
    {
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "EveDeck", "cache", "ship-icons");
        try { Directory.CreateDirectory(_dir); } catch { /* first read/write will surface the error */ }
    }

    private string PathFor(int typeId) => Path.Combine(_dir, $"{typeId}.png");

    private static string UrlFor(int typeId) =>
        $"https://images.evetech.net/types/{typeId}/icon?size={IconSize}";

    // The shared observable icon for a ship type id. Loads any on-disk copy immediately and schedules
    // a background download only when nothing is cached yet.
    public ShipIcon ForId(int typeId)
    {
        if (typeId <= 0) return new ShipIcon(typeId);

        if (_byTypeId.TryGetValue(typeId, out var existing)) return existing;

        var icon = _byTypeId.GetOrAdd(typeId, id => new ShipIcon(id));

        // Only do real work inside a running WPF app (keeps unit tests / headless paths inert).
        if (Application.Current is null) return icon;

        var path = PathFor(typeId);
        if (File.Exists(path) && icon.Image is null)
        {
            try { icon.Image = LoadFrozen(path); } catch { /* corrupt cache file -> re-download below */ }
        }

        if (!File.Exists(path)) _ = DownloadAsync(typeId);
        return icon;
    }

    private async Task DownloadAsync(int typeId)
    {
        if (!_inflight.TryAdd(typeId, 0)) return; // already downloading
        try
        {
            var path = PathFor(typeId);
            if (File.Exists(path)) return;

            var bytes = await Http.GetByteArrayAsync(UrlFor(typeId)).ConfigureAwait(false);
            if (bytes.Length == 0) return;

            var tmp = path + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);

            var frozen = LoadFrozen(path);
            AssignOnUi(typeId, frozen);
        }
        catch { /* transient network / IO error -- next call to ForId retries */ }
        finally { _inflight.TryRemove(typeId, out _); }
    }

    private static BitmapImage LoadFrozen(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bmp.DecodePixelWidth = IconSize;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void AssignOnUi(int typeId, ImageSource image)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp is null) return;
        disp.Invoke(() =>
        {
            if (_byTypeId.TryGetValue(typeId, out var icon)) icon.Image = image;
            Changed?.Invoke();
        });
    }
}
