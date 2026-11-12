namespace InfoHubLite;

/// <summary>
/// The built-in layouts. Each is just a starting list of elements, authored for the 320x240 landscape canvas and
/// remapped for portrait; after that the user owns every rectangle.
/// Sensors use portable aliases (cpu.load, gpu.load, cpu.temp, ...) that the engine resolves on whatever machine it runs.
/// </summary>
static class Templates
{
    public static readonly string[] Names =
    [
        "極簡大字", "雙環儀表", "儀表板列", "角落資訊", "主角數字", "時鐘加資訊列",
        "單環主角", "三環", "雙弧儀表", "左右分欄", "直條儀表", "指針時鐘加資訊", "純時鐘", "底部資訊列", "溫度雙主角", "單色大字",
    ];

    const string Muted = "#AAB2C0", White = "#FFFFFF", Teal = "#4FD1C5", Coral = "#F0997B", Violet = "#A78BFA", Amber = "#FBBF24",
        Cyan = "#22D3EE", Pink = "#F472B6", Line = "#3A4150";

    static Element Text(string text, int x, int y, int w, int h, string color = Muted, int align = 1, bool bold = false) =>
        new() { Type = "text", Text = text, X = x, Y = y, W = w, H = h, Color = color, Align = align, Bold = bold };

    static Element Value(string sensor, int x, int y, int w, int h, string prefix = "", string color = White, int align = 1, bool bold = true, bool pill = false) =>
        new() { Type = "value", Sensor = sensor, Text = prefix, X = x, Y = y, W = w, H = h, Color = color, Align = align, Bold = bold, Pill = pill };

    static Element Shape(string type, string sensor, int x, int y, int w, int h, string color) =>
        new() { Type = type, Sensor = sensor, X = x, Y = y, W = w, H = h, Color = color };

    static Element Divider(int x, int y, int w, int h) => Shape("bar", "", x, y, w, h, Line);

    public static List<Element> Build(string name, bool portrait)
    {
        List<Element> e = name switch
        {
            "雙環儀表" =>
            [
                Shape("ring", "cpu.load", 42, 52, 100, 100, Teal), Text("CPU", 42, 164, 100, 28),
                Shape("ring", "gpu.load", 178, 52, 100, 100, Coral), Text("GPU", 178, 164, 100, 28),
            ],
            "儀表板列" => Rows(("CPU", "cpu.load"), ("GPU", "gpu.load"), ("溫度", "cpu.temp"), ("RAM", "ram.load")),
            "角落資訊" =>
            [
                Value("cpu.load", 10, 196, 132, 34, "CPU ", bold: false, pill: true),
                Value("gpu.load", 178, 196, 132, 34, "GPU ", bold: false, pill: true),
            ],
            "主角數字" =>
            [
                Text("CPU 溫度", 24, 20, 200, 28, align: 0),
                Value("cpu.temp", 20, 46, 280, 128, align: 0),
                Divider(24, 182, 272, 1),
                Value("cpu.load", 24, 196, 90, 26, "CPU ", Muted, 0, false),
                Value("gpu.load", 116, 196, 90, 26, "GPU ", Muted, 1, false),
                Value("ram.load", 208, 196, 90, 26, "RAM ", Muted, 2, false),
            ],
            "時鐘加資訊列" =>
            [
                new() { Type = "clock", X = 0, Y = 36, W = 320, H = 108, Color = White, Bold = true },
                new() { Type = "date", X = 0, Y = 138, W = 320, H = 28, Color = Muted },
                Divider(24, 182, 272, 1),
                Value("cpu.load", 24, 196, 136, 26, "CPU ", Muted, 1, false),
                Value("gpu.load", 160, 196, 136, 26, "GPU ", Muted, 1, false),
            ],
            "單環主角" =>
            [
                Shape("ring", "cpu.load", 95, 14, 130, 130, Teal), Text("CPU", 95, 148, 130, 26),
                Divider(24, 186, 272, 1),
                Value("gpu.load", 24, 198, 136, 26, "GPU ", Muted, 1, false),
                Value("ram.load", 160, 198, 136, 26, "RAM ", Muted, 1, false),
            ],
            "三環" =>
            [
                Shape("ring", "cpu.load", 20, 54, 86, 86, Teal), Text("CPU", 20, 150, 86, 26),
                Shape("ring", "gpu.load", 117, 54, 86, 86, Coral), Text("GPU", 117, 150, 86, 26),
                Shape("ring", "ram.load", 214, 54, 86, 86, Violet), Text("RAM", 214, 150, 86, 26),
            ],
            "雙弧儀表" =>
            [
                Shape("gauge", "cpu.load", 18, 44, 134, 134, Cyan), Text("CPU", 18, 162, 134, 28),
                Shape("gauge", "gpu.load", 168, 44, 134, 134, Pink), Text("GPU", 168, 162, 134, 28),
            ],
            "左右分欄" =>
            [
                Text("CPU", 12, 22, 140, 26), Value("cpu.load", 12, 52, 140, 78), Shape("bar", "cpu.load", 30, 140, 104, 6, Teal),
                Divider(159, 28, 1, 124),
                Text("GPU", 168, 22, 140, 26), Value("gpu.load", 168, 52, 140, 78), Shape("bar", "gpu.load", 186, 140, 104, 6, Coral),
                Value("ram.load", 0, 184, 320, 30, "RAM ", Muted, 1, false),
            ],
            "直條儀表" => Columns(("CPU", "cpu.load", Teal), ("GPU", "gpu.load", Coral), ("RAM", "ram.load", Violet), ("溫度", "cpu.temp", Amber)),
            "指針時鐘加資訊" =>
            [
                new() { Type = "analog", X = 12, Y = 30, W = 180, H = 180, Color = White },
                new() { Type = "date", X = 196, Y = 44, W = 116, H = 24, Color = Muted },
                Value("cpu.load", 196, 92, 116, 28, "CPU ", bold: false),
                Value("gpu.load", 196, 130, 116, 28, "GPU ", bold: false),
                Value("ram.load", 196, 168, 116, 28, "RAM ", bold: false),
            ],
            "純時鐘" =>
            [
                new() { Type = "clock", X = 0, Y = 46, W = 320, H = 124, Color = White, Bold = true },
                new() { Type = "date", X = 0, Y = 166, W = 320, H = 30, Color = Muted },
            ],
            "底部資訊列" =>
            [
                Value("cpu.load", 8, 202, 98, 28, "CPU ", bold: false, pill: true),
                Value("gpu.load", 111, 202, 98, 28, "GPU ", bold: false, pill: true),
                Value("ram.load", 214, 202, 98, 28, "RAM ", bold: false, pill: true),
            ],
            "溫度雙主角" =>
            [
                Text("CPU", 0, 18, 160, 26), Value("cpu.temp", 0, 46, 160, 72), Shape("bar", "cpu.load", 30, 126, 100, 5, Teal),
                Text("GPU", 160, 18, 160, 26), Value("gpu.temp", 160, 46, 160, 72), Shape("bar", "gpu.load", 190, 126, 100, 5, Coral),
                Divider(24, 158, 272, 1),
                Value("ram.load", 0, 176, 320, 34, "RAM ", Muted, 1, false),
            ],
            "單色大字" =>
            [
                Value("cpu.load", 0, 22, 320, 140, color: Cyan),
                Text("CPU 使用率", 0, 158, 320, 28),
                Shape("bar", "cpu.load", 60, 204, 200, 6, Cyan),
            ],
            _ =>
            [
                Text("CPU", 0, 18, 320, 30), Value("cpu.load", 0, 44, 320, 76),
                Text("RAM", 0, 124, 320, 30), Value("ram.load", 0, 150, 320, 76),
            ],
        };
        if (portrait) Remap(e, Lcd.NativeH, Lcd.NativeW, Lcd.NativeW, Lcd.NativeH);
        return e;
    }

    static List<Element> Rows(params (string label, string sensor)[] rows)
    {
        var e = new List<Element>();
        for (int i = 0; i < rows.Length; i++)
        {
            int y = 14 + i * 56;
            e.Add(Text(rows[i].label, 22, y, 130, 30, align: 0));
            e.Add(Value(rows[i].sensor, 168, y - 2, 130, 34, align: 2));
            e.Add(Shape("bar", rows[i].sensor, 22, y + 36, 276, 5, Teal));
        }
        return e;
    }

    /// <summary>Upright bars side by side: reading on top, bar, label underneath.</summary>
    static List<Element> Columns(params (string label, string sensor, string color)[] cols)
    {
        var e = new List<Element>();
        int slot = Lcd.NativeH / cols.Length;
        for (int i = 0; i < cols.Length; i++)
        {
            int x = i * slot;
            e.Add(Value(cols[i].sensor, x, 14, slot, 24));
            e.Add(Shape("bar", cols[i].sensor, x + slot / 2 - 16, 46, 32, 126, cols[i].color));
            e.Add(Text(cols[i].label, x, 180, slot, 24));
        }
        return e;
    }

    /// <summary>Carries a layout to another canvas size: centres keep their relative position, sizes scale uniformly.</summary>
    public static void Remap(List<Element> elements, int fromW, int fromH, int toW, int toH)
    {
        float sx = (float)toW / fromW, sy = (float)toH / fromH, s = Math.Min(sx, sy);
        foreach (var e in elements)
        {
            float cx = (e.X + e.W / 2f) * sx, cy = (e.Y + e.H / 2f) * sy;
            e.W = Math.Max(1, (int)MathF.Round(e.W * s)); e.H = Math.Max(1, (int)MathF.Round(e.H * s));
            e.X = (int)MathF.Round(cx - e.W / 2f); e.Y = (int)MathF.Round(cy - e.H / 2f);
        }
    }
}
