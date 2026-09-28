using Avalonia;
using EveDeck.Linux.Platform.X11;
using EveDeck.Models;
using EveDeck.Services;

namespace EveDeck.Linux;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless dev commands, so the X11 layer can be checked from a script without the UI.
        if (args.Contains("--probe")) return Probe();
        if (args.Contains("--apply-grid")) return ApplyGrid();
        if (args.Contains("--activate")) return Activate(args);
        if (args.Contains("--preview")) return Preview(args);

        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(args);
        return 0;
    }

    private static int Probe()
    {
        using var x11 = new X11WindowService();
        foreach (var m in x11.GetMonitors())
            Console.WriteLine($"monitor {m.Id} {Rect(m.Bounds)}{(m.IsPrimary ? " primary" : "")}");
        foreach (var c in x11.GetEveClients())
            Console.WriteLine($"client 0x{c.Handle:x} class={c.WmClass} {Rect(c.Bounds)} \"{c.Title}\"");
        Console.WriteLine($"active 0x{x11.GetActiveWindow():x}");
        return 0;
    }

    // Phase-1 proof: tile every EVE client into a plain grid on the primary monitor, then read each
    // window back and report how far it landed from its slot.
    private static int ApplyGrid()
    {
        using var x11 = new X11WindowService();
        var monitor = x11.GetMonitors().OrderByDescending(m => m.IsPrimary).FirstOrDefault();
        var clients = x11.GetEveClients().OrderBy(c => c.Title, StringComparer.Ordinal).ToList();
        if (monitor is null || clients.Count == 0)
        {
            Console.Error.WriteLine("no monitor or no EVE clients");
            return 1;
        }

        var profile = PresetFactory.CreateCustomProfile("Grid", monitor.Bounds.Width, monitor.Bounds.Height, clients.Count);
        var targets = new List<(X11Client Client, WindowRect Target)>();
        for (var i = 0; i < clients.Count && i < profile.Slots.Count; i++)
        {
            var slot = profile.Slots[i];
            var target = new WindowRect
            {
                X = monitor.Bounds.X + slot.X,
                Y = monitor.Bounds.Y + slot.Y,
                Width = slot.Width,
                Height = slot.Height,
            };
            x11.SetBorderless(clients[i].Handle, slot.Borderless);
            x11.MoveResize(clients[i].Handle, target);
            targets.Add((clients[i], target));
        }

        x11.Sync();
        Thread.Sleep(500); // let the WM reframe and apply the configure requests

        var worst = 0;
        foreach (var (client, target) in targets)
        {
            x11.TryGetBounds(client.Handle, out var actual);
            var delta = Math.Max(Math.Max(Math.Abs(actual.X - target.X), Math.Abs(actual.Y - target.Y)),
                Math.Max(Math.Abs(actual.Width - target.Width), Math.Abs(actual.Height - target.Height)));
            worst = Math.Max(worst, delta);
            Console.WriteLine($"\"{client.Title}\" target {Rect(target)} actual {Rect(actual)} delta {delta}px");
        }
        return worst == 0 ? 0 : 2;
    }

    private static int Activate(string[] args)
    {
        using var x11 = new X11WindowService();
        var title = args.SkipWhile(a => a != "--activate").Skip(1).FirstOrDefault() ?? "";
        var client = x11.GetEveClients().FirstOrDefault(c => c.Title == title);
        if (client is null)
        {
            Console.Error.WriteLine($"no EVE client titled \"{title}\"");
            return 1;
        }
        x11.Activate(client.Handle);
        x11.Sync();
        Thread.Sleep(300);
        var active = x11.GetActiveWindow();
        Console.WriteLine($"requested 0x{client.Handle:x} active 0x{active:x}");
        return active == client.Handle ? 0 : 2;
    }

    // Phase-2 proof: a column of live preview tiles down the right edge of the primary monitor, one per
    // client at a quarter of its size, kept up for the given number of seconds (default 30).
    private static int Preview(string[] args)
    {
        var seconds = int.TryParse(args.SkipWhile(a => a != "--preview").Skip(1).FirstOrDefault(), out var s) ? s : 30;
        List<PreviewTileSpec> specs;
        using (var x11 = new X11WindowService())
        {
            var monitor = x11.GetMonitors().OrderByDescending(m => m.IsPrimary).FirstOrDefault();
            var clients = x11.GetEveClients().OrderBy(c => c.Title, StringComparer.Ordinal).ToList();
            if (monitor is null || clients.Count == 0)
            {
                Console.Error.WriteLine("no monitor or no EVE clients");
                return 1;
            }
            specs = new List<PreviewTileSpec>();
            var y = monitor.Bounds.Y + 10;
            foreach (var c in clients)
            {
                var w = Math.Max(1, c.Bounds.Width / 4);
                var h = Math.Max(1, c.Bounds.Height / 4);
                var rect = new WindowRect { X = monitor.Bounds.X + monitor.Bounds.Width - w - 10, Y = y, Width = w, Height = h };
                specs.Add(new PreviewTileSpec(c.Handle, rect));
                Console.WriteLine($"tile \"{c.Title}\" {Rect(rect)}");
                y += h + 10;
            }
        }

        using var engine = new X11PreviewEngine();
        engine.TileClicked += source => Console.WriteLine($"clicked 0x{source:x}");
        engine.Start();
        engine.SetTiles(specs);
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    private static string Rect(WindowRect r) => $"{r.Width}x{r.Height}+{r.X}+{r.Y}";
}
