using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace InfoHubLite;

/// <summary>One thing drawn on the panel. Everything is positioned by its rectangle, in canvas pixels.</summary>
sealed class Element
{
    public string Type { get; set; } = "text";   // text | value | ring | gauge | bar | clock | date | analog
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 100;
    public int H { get; set; } = 30;             // for text-like elements the font size follows the height
    public string Sensor { get; set; } = "";     // value / ring / bar: "cpu.load", "gpu.temp", or a LibreHardwareMonitor id
    public string Text { get; set; } = "";       // text: the text itself; value: a prefix such as "CPU "
    public string Color { get; set; } = "#FFFFFF";
    public int Align { get; set; } = 1;          // 0 left, 1 centre, 2 right
    public bool Bold { get; set; }
    public bool Pill { get; set; }               // dark backing (rounded for text, a disc for ring / gauge / analog), for busy media
    public bool Seconds { get; set; }            // clock / analog

    [JsonIgnore] public bool IsText => Type is "text" or "value" or "clock" or "date";
    [JsonIgnore] public bool UsesSensor => Type is "value" or "ring" or "gauge" or "bar";
    public Element Clone() => (Element)MemberwiseClone();
}

/// <summary>How a media item sits behind the layout: zoom in, then slide within whatever room that leaves.</summary>
sealed class Fit
{
    public double Zoom { get; set; } = 1;  // 1 = just covers the panel
    public double X { get; set; }          // -1 = far left of the picture, 0 = centred, 1 = far right
    public double Y { get; set; }          // -1 = top, 0 = centred, 1 = bottom
    [JsonIgnore] public string Key => $"{Zoom:0.###}|{X:0.###}|{Y:0.###}";
}

enum Kind { Load, Temperature, Fan, Clock, Voltage, Power, Other }

/// <summary>Shared contract between the engine and the settings UI: %AppData%\InfoHubLite\config.json.</summary>
sealed class Config
{
    public int Brightness { get; set; } = 100;          // 0..100
    public bool ScreenOn { get; set; } = true;
    public int Rotation { get; set; } = 0;              // 0 / 90 / 180 / 270
    public bool SleepOff { get; set; } = true;          // blank the LCD when Windows suspends
    public bool Fahrenheit { get; set; }

    public List<Element>? Elements { get; set; }        // drawn in order, later ones on top
    public bool Shadow { get; set; } = true;            // dark offset copy behind text so it stays readable over media
    public int Dim { get; set; }                        // 0..80 % darkening of the media under the elements
    public string Background { get; set; } = "#000000"; // used when the playlist is empty

    public List<string> Playlist { get; set; } = [];    // .ihl file names inside MediaDir
    public Dictionary<string, string> Sources { get; set; } = []; // .ihl name -> original file, to re-convert when the orientation or placement changes
    public Dictionary<string, Fit> Fits { get; set; } = [];       // .ihl name -> placement; no entry = centred, just covering
    public int Current { get; set; }
    public string Cycle { get; set; } = "one";          // one | list | random
    public int ImageSeconds { get; set; } = 10;         // dwell time of still images when cycling

    // Hardware knob. Whether the panel keeps the last frame without being fed is unverified, so resend by default.
    public int KeepAliveMs { get; set; } = 1000;        // 0 = never resend an unchanged frame

    [JsonIgnore] public bool Portrait => Rotation is 90 or 270;
    [JsonIgnore] public int CanvasW => Portrait ? Lcd.NativeW : Lcd.NativeH;
    [JsonIgnore] public int CanvasH => Portrait ? Lcd.NativeH : Lcd.NativeW;

    // Setting INFOHUBLITE_DATA points everything at a private folder and gives the engine/editor pair their own
    // event, shared-memory and single-instance names. Tests use it, so they can never read, overwrite or clean up
    // the real settings and media, nor talk to the engine that is driving the panel.
    static readonly string? Private = Environment.GetEnvironmentVariable("INFOHUBLITE_DATA") is { Length: > 0 } d ? Path.GetFullPath(d) : null;
    public static bool Isolated => Private != null;
    public static readonly string Scope = Private == null ? "" : "." + (uint)Hash(Private.ToLowerInvariant());

    public static readonly string WakeEvent = "InfoHubLite.Wake" + Scope; // set by the settings UI after saving; the engine reloads at once
    // Shared memory the engine publishes: int w, h, connected, serial, hash of the config it is running, 3 reserved;
    // then w*h RGB565 pixels. The hash lets the editor confirm that "Apply" actually reached the engine.
    public static readonly string PreviewMap = "InfoHubLite.Preview2" + Scope;
    public const int PreviewHeader = 32;

    /// <summary>Fingerprint of the config.json text this object was loaded from or saved as (0 = neither).</summary>
    [JsonIgnore] public int LoadedHash { get; private set; }

    static int Hash(string json)
    {
        uint h = 2166136261; // FNV-1a
        foreach (char c in json) h = (h ^ c) * 16777619;
        return h == 0 ? 1 : (int)h;
    }

    public static readonly string Dir = Private ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "InfoHubLite");
    public static readonly string FilePath = Path.Combine(Dir, "config.json");
    public static readonly string MediaDir = Path.Combine(Dir, "media");
    public static readonly string TemplateDir = Path.Combine(Dir, "templates");

    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Returns null when the file exists but cannot be parsed, so a half-written edit never wipes the running setup.</summary>
    public static Config? Load()
    {
        Directory.CreateDirectory(MediaDir);
        if (!File.Exists(FilePath)) { var d = new Config().Normalize(); d.Save(); return d; }
        try
        {
            string text = File.ReadAllText(FilePath);
            var c = JsonSerializer.Deserialize<Config>(text, Json)?.Normalize();
            if (c != null) c.LoadedHash = Hash(text);
            return c;
        }
        catch (Exception e) when (e is JsonException or IOException) { return null; }
    }

    public Config Normalize()
    {
        Brightness = Math.Clamp(Brightness, 0, 100);
        Rotation = ((Rotation / 90 % 4) + 4) % 4 * 90;
        Dim = Math.Clamp(Dim, 0, 80);
        ImageSeconds = Math.Max(1, ImageSeconds);
        Elements ??= Templates.Build(Templates.Names[0], Portrait); // first run, or a config from before the layout editor
        // ponytail: hard cap on element count keeps the per-frame draw cost bounded
        if (Elements.Count > 24) Elements.RemoveRange(24, Elements.Count - 24);
        foreach (var e in Elements) { e.W = Math.Clamp(e.W, 1, 640); e.H = Math.Clamp(e.H, 1, 640); }
        return this;
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public Config Clone() => JsonSerializer.Deserialize<Config>(ToJson(), Json)!;

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        string text = ToJson();
        File.WriteAllText(tmp, text);
        File.Move(tmp, FilePath, true); // atomic replace: a crash mid-write cannot corrupt the config
        LoadedHash = Hash(text);
    }
}

/// <summary>Turns a reading into what an element shows. One place, so the engine and the editor never disagree.</summary>
static class Fmt
{
    public static string Value(float? value, Kind kind, bool fahrenheit)
    {
        if (value is not float v) return "--";
        return kind switch
        {
            Kind.Load => $"{v:0}%",
            Kind.Temperature => fahrenheit ? $"{v * 9 / 5 + 32:0}°F" : $"{v:0}°C",
            Kind.Fan => $"{v:0} RPM",
            Kind.Clock => v >= 1000 ? $"{v / 1000:0.00} GHz" : $"{v:0} MHz",
            Kind.Voltage => $"{v:0.000} V",
            Kind.Power => $"{v:0} W",
            _ => $"{v:0.#}",
        };
    }

    /// <summary>0..1 fill for rings and bars; NaN when the sensor has no natural 0..100 range or no reading.</summary>
    public static float Fraction(float? value, Kind kind) =>
        value is float v && (kind == Kind.Load || kind == Kind.Temperature) ? Math.Clamp(v / 100f, 0, 1) : float.NaN;

    static ReadOnlySpan<char> Weekdays => "日一二三四五六";

    public static string Text(Element e, float? value, Kind kind, bool fahrenheit, DateTime now) => e.Type switch
    {
        "text" => e.Text,
        "value" => e.Text + Value(value, kind, fahrenheit),
        "ring" or "gauge" => Value(value, kind, fahrenheit),
        "clock" or "analog" => now.ToString(e.Seconds ? "HH:mm:ss" : "HH:mm"),
        "date" => $"{now.Month}/{now.Day} 週{Weekdays[(int)now.DayOfWeek]}",
        _ => "",
    };
}

/// <summary>Start with Windows: the per-user Run key, pointing at the engine.</summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", Name = "InfoHubLite";

    public static bool IsOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(Name) != null;
    }

    public static void Set(bool on, string engineExe)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, $"\"{engineExe}\""); else k.DeleteValue(Name, false);
    }
}
