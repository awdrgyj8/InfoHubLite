using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace InfoHubLite;

static class Program
{
    static int Main(string[] args)
    {
        switch (args.FirstOrDefault())
        {
            case null: return Resident();
            case "--takeover": // started elevated from the tray: replace the running instance
                foreach (var p in Process.GetProcessesByName("InfoHubLite"))
                    using (p) if (p.Id != Environment.ProcessId) { p.Kill(); p.WaitForExit(3000); }
                return Resident();
            case "selftest": return SelfTest();
            case "render" when args.Length == 2: // compose one frame from the current config into a PNG, device untouched
                using (var e = new Engine()) e.RenderOnce(Path.GetFullPath(args[1]));
                return 0;
            case "template" when args.Length >= 3: // render a built-in template to a PNG: template <name|index> <out.png> [--portrait]
                using (var e = new Engine())
                {
                    bool portrait = args.Contains("--portrait");
                    string name = int.TryParse(args[1], out int n) ? Templates.Names[n] : args[1];
                    e.Override = new Config { Rotation = portrait ? 90 : 0, Elements = Templates.Build(name, portrait) }.Normalize();
                    e.RenderOnce(Path.GetFullPath(args[2]));
                }
                return 0;
            case "sensors":
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                foreach (var (id, hw, name, kind, value) in Sensors.ListAll())
                    Console.WriteLine($"{id}\t{hw}\t{name}\t{kind}\t{Fmt.Value(value, kind, false)}\t{value?.ToString("R") ?? ""}");
                return 0;
            case "import" when args.Length >= 3:
                return Import(args);
            case "probe": // hardware check: send the current frame N times and time each USB write
                using (var e = new Engine())
                using (var lcd = new Lcd())
                {
                    e.RenderOnce(null);
                    if (!lcd.Open()) { Console.WriteLine($"open failed, win32 error {Marshal.GetLastPInvokeError()}"); return 1; }
                    byte[] packet = e.Packet.ToArray();
                    int n = args.Length > 1 ? int.Parse(args[1]) : 20;
                    var sw = new Stopwatch();
                    for (int i = 0; i < n; i++)
                    {
                        sw.Restart();
                        bool ok = lcd.Write(packet);
                        Console.WriteLine($"write {i}: {(ok ? "ok" : "FAILED " + Marshal.GetLastPInvokeError())} {sw.Elapsed.TotalMilliseconds:0.0} ms");
                        if (!ok) return 1;
                    }
                }
                return 0;
            default:
                Console.Error.WriteLine("InfoHubLite [selftest | render <out.png> | sensors | import <source> <name> [--portrait] [--no-dither] [--crop x,y,w,h]]");
                return 2;
        }
    }

    static int Resident()
    {
        using var single = new Mutex(true, "InfoHubLite.Single" + Config.Scope, out bool first);
        if (!first) return 0;
        using var self = Process.GetCurrentProcess();
        self.PriorityClass = ProcessPriorityClass.AboveNormal; // we use next to no CPU; this keeps animation steady under load

        // INFOHUBLITE_OFFLINE=1: compose and publish as usual but never open the device (test runs beside the real engine).
        using var engine = new Engine { Offline = Environment.GetEnvironmentVariable("INFOHUBLITE_OFFLINE") == "1" };
        var loop = new Thread(engine.Run) { IsBackground = true, Name = "engine" };
        loop.Start();

        new Tray(
            () => (engine.Config.ScreenOn, Autostart.IsOn()),
            cmd =>
            {
                if (cmd == Tray.CmdScreen && Config.Load() is { } c) { c.ScreenOn = !c.ScreenOn; c.Save(); engine.Wake(); }
                else if (cmd == Tray.CmdAutostart) Autostart.Set(!Autostart.IsOn(), Environment.ProcessPath!);
                else if (cmd == Tray.CmdAdmin)
                    try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--takeover") { UseShellExecute = true, Verb = "runas" })?.Dispose(); }
                    catch (System.ComponentModel.Win32Exception) { /* UAC prompt declined */ }
                else if (cmd == Tray.CmdSettings)
                {
                    string ui = Path.Combine(AppContext.BaseDirectory, "Settings", "InfoHubLite.Settings.exe");
                    Process.Start(new ProcessStartInfo(File.Exists(ui) ? ui : Config.FilePath) { UseShellExecute = true })?.Dispose();
                }
            },
            engine.OnPower).Run();

        engine.Quit();
        loop.Join(2000);
        return 0;
    }

    static int Import(string[] a)
    {
        RectangleF? crop = null;
        int ci = Array.IndexOf(a, "--crop");
        if (ci > 0 && ci + 1 < a.Length)
        {
            float[] v = a[ci + 1].Split(',').Select(float.Parse).ToArray();
            if (v.Length != 4 || v[2] <= 0 || v[3] <= 0) { Console.Error.WriteLine("--crop needs x,y,w,h"); return 2; }
            crop = new RectangleF(v[0], v[1], v[2], v[3]);
        }
        Directory.CreateDirectory(Config.MediaDir);
        bool portrait = a.Contains("--portrait");
        string dst = Media.PathFor(Path.GetFileNameWithoutExtension(a[2]) + ".ihl", portrait);
        int frames = Media.Import(Path.GetFullPath(a[1]), dst, portrait, crop, !a.Contains("--no-dither"));
        Console.WriteLine($"{frames}\t{dst}");
        return 0;
    }

    /// <summary>Smallest checks that fail if the wire format, rotation mapping or media round trip break.</summary>
    static int SelfTest()
    {
        int failed = 0;
        void Check(bool ok, string what) { if (!ok) { failed++; Console.WriteLine("FAIL " + what); } }

        // Header bytes as built by ASUS_InfoHub.exe for a full landscape frame at brightness 100.
        Span<byte> h = stackalloc byte[Lcd.HeaderLen];
        Lcd.WriteHeader(h, 100, true);
        Check(h.SequenceEqual<byte>([0x5A, 0xA5, 0x00, 0x02, 0x58, 0x04, 0xE7, 0x9B, 100, 0x01, 0x40, 0x00, 0xF0]), "landscape header");
        Lcd.WriteHeader(h, 250, false);
        Check(h[8] == 100 && h[9] == 0 && h[10] == 0xF0 && h[11] == 1 && h[12] == 0x40, "portrait header / brightness clamp");
        Check(Lcd.Crc16("123456789"u8) == 0x4B37, "CRC16/MODBUS check value");

        // Rotation: mark three corners of a landscape canvas and see where they land in the native 240x320 buffer.
        var canvas = new ushort[Lcd.Pixels];
        var packet = new byte[Lcd.PacketLen];
        canvas[0] = 2; canvas[319] = 1; canvas[239 * 320] = 3; // top-left, top-right, bottom-left
        ReadOnlySpan<ushort> N() => MemoryMarshal.Cast<byte, ushort>(packet.AsSpan(Lcd.HeaderLen));
        Renderer.Blit(canvas, 0, packet);
        Check(N()[0] == 1 && N()[319 * 240] == 2 && N()[319 * 240 + 239] == 3, "0°: 90° counter-clockwise like InfoHub");
        Renderer.Blit(canvas, 180, packet);
        Check(N()[0] == 3 && N()[239] == 2 && N()[319 * 240 + 239] == 1, "180°");
        Renderer.Blit(canvas, 90, packet);
        Check(N()[0] == 2 && N()[319] == 1, "90°: sent as-is");
        Renderer.Blit(canvas, 270, packet);
        Check(N()[Lcd.Pixels - 1] == 2, "270°");

        // Media: a solid red picture must come back as solid RGB565 red, for both orientations.
        string png = Path.Combine(Path.GetTempPath(), "ihl-selftest.png"), ihl = Path.Combine(Path.GetTempPath(), "ihl-selftest.ihl");
        using (var b = new Bitmap(64, 48)) { using var g = Graphics.FromImage(b); g.Clear(Color.Red); b.Save(png); }
        foreach (bool portrait in new[] { false, true })
        {
            Check(Media.Import(png, ihl, portrait, null) == 1, "import frame count");
            using var m = Media.Open(ihl);
            Check(m is { Count: 1 } && m.W == (portrait ? 240 : 320), "media header");
            m?.Read(0, canvas);
            Check(canvas.AsSpan().IndexOfAnyExcept((ushort)0xF800) < 0, "red survives conversion");
        }
        File.Delete(png); File.Delete(ihl);

        Check(Fmt.Value(45.4f, Kind.Temperature, true) == "114°F" && Fmt.Value(null, Kind.Fan, false) == "--" && Fmt.Value(4850, Kind.Clock, false) == "4.85 GHz", "sensor formatting");

        // Layout: every built-in template stays on the canvas in both orientations and survives a JSON round trip.
        foreach (string name in Templates.Names)
            foreach (bool portrait in new[] { false, true })
            {
                var cfg = new Config { Rotation = portrait ? 90 : 0, Elements = Templates.Build(name, portrait) }.Normalize();
                Check(cfg.Elements!.Count is > 0 and <= 24 && cfg.Elements.TrueForAll(e => e.X >= 0 && e.Y >= 0 && e.X + e.W <= cfg.CanvasW && e.Y + e.H <= cfg.CanvasH), $"template {name} fits {(portrait ? "portrait" : "landscape")}");
                Check(cfg.Clone().ToJson() == cfg.ToJson(), $"template {name} round-trips");
            }
        var el = new Element { Type = "value", Text = "CPU " };
        Check(Fmt.Text(el, 23.4f, Kind.Load, false, DateTime.Now) == "CPU 23%" && float.IsNaN(Fmt.Fraction(1200, Kind.Fan)) && Fmt.Fraction(50, Kind.Load) == 0.5f, "element text and fraction");
        Check(Renderer.ParseColor("#4FD1C5", Color.Black) == Color.FromArgb(255, 0x4F, 0xD1, 0xC5) && Renderer.ParseColor("nope", Color.Red) == Color.Red, "colour parsing");

        Console.WriteLine(failed == 0 ? "selftest ok" : $"selftest: {failed} failed");
        return failed;
    }
}
