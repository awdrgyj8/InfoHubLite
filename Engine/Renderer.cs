using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace InfoHubLite;

/// <summary>
/// Draws the elements with GDI+ straight onto a pinned RGB565 buffer (the panel's own pixel format), so there is
/// no per-pixel conversion step at all — the step that costs the official app a fifth of a core.
/// The same class renders the editor's canvas, so what you arrange is exactly what the panel shows.
/// </summary>
sealed unsafe class Renderer : IDisposable
{
    public readonly ushort[] Canvas = GC.AllocateArray<ushort>(Lcd.Pixels, pinned: true);
    public int W { get; private set; }
    public int H { get; private set; }

    Bitmap? _bmp; Graphics? _g;
    readonly Dictionary<int, Font> _fonts = [];
    readonly SolidBrush _brush = new(Color.White), _shadowB = new(Color.FromArgb(200, 0, 0, 0)), _pillB = new(Color.FromArgb(150, 0, 0, 0)), _trackB = new(Color.FromArgb(64, 255, 255, 255)), _dimB = new(Color.Black);
    readonly Pen _pen = new(Color.White) { StartCap = LineCap.Round, EndCap = LineCap.Round }, _trackP = new(Color.FromArgb(64, 255, 255, 255)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
    readonly StringFormat _fmt = new(StringFormat.GenericTypographic) { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip | StringFormatFlags.MeasureTrailingSpaces };

    public void Configure(bool portrait)
    {
        int w = portrait ? Lcd.NativeW : Lcd.NativeH, h = portrait ? Lcd.NativeH : Lcd.NativeW;
        if (_bmp != null && w == W) return;
        _g?.Dispose(); _bmp?.Dispose();
        W = w; H = h;
        fixed (ushort* p = Canvas) _bmp = new Bitmap(W, H, W * 2, PixelFormat.Format16bppRgb565, (nint)p); // Canvas is pinned for life
        _g = Graphics.FromImage(_bmp);
        _g.SmoothingMode = SmoothingMode.AntiAlias;
        _g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    /// <summary>"#RRGGBB" without allocating; anything else gives the fallback.</summary>
    public static Color ParseColor(string s, Color fallback)
    {
        int start = s.Length > 0 && s[0] == '#' ? 1 : 0;
        if (s.Length - start != 6) return fallback;
        int v = 0;
        for (int i = start; i < s.Length; i++)
        {
            int c = s[i], d = c is >= '0' and <= '9' ? c - '0' : (c | 32) is >= 'a' and <= 'f' ? (c | 32) - 'a' + 10 : -1;
            if (d < 0) return fallback;
            v = v << 4 | d;
        }
        return Color.FromArgb(255, v >> 16 & 255, v >> 8 & 255, v & 255);
    }

    public static ushort To565(Color c) => (ushort)((c.R >> 3) << 11 | (c.G >> 2) << 5 | c.B >> 3);

    Font GetFont(float px, bool bold)
    {
        int size = Math.Clamp((int)MathF.Round(px), 6, 400), key = size << 1 | (bold ? 1 : 0);
        if (!_fonts.TryGetValue(key, out var f))
        {
            // ponytail: unbounded only in theory — sizes are whole pixels, so at most a few hundred small objects
            f = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            _fonts[key] = f;
        }
        return f;
    }

    /// <summary>Darkens the media under the elements so text stays legible.</summary>
    public void Dim(int percent)
    {
        _dimB.Color = Color.FromArgb(percent * 255 / 100, 0, 0, 0);
        _g!.FillRectangle(_dimB, 0, 0, W, H);
    }

    /// <param name="texts">per element, from <see cref="Fmt.Text"/></param>
    /// <param name="fracs">per element, 0..1 fill for rings and bars (NaN = no reading)</param>
    public void Draw(IReadOnlyList<Element> elements, string[] texts, float[] fracs, bool shadow, DateTime now)
    {
        var g = _g!;
        for (int i = 0; i < elements.Count; i++)
        {
            var e = elements[i];
            var r = new RectangleF(e.X, e.Y, e.W, e.H);
            var color = ParseColor(e.Color, Color.White);
            switch (e.Type)
            {
                case "ring":
                {
                    float d = Math.Min(r.Width, r.Height), t = Math.Max(2, d * 0.11f);
                    var box = new RectangleF(r.X + (r.Width - d) / 2 + t / 2, r.Y + (r.Height - d) / 2 + t / 2, d - t, d - t);
                    if (box.Width < 2) break;
                    if (e.Pill) g.FillEllipse(_pillB, RectangleF.Inflate(box, -t / 2, -t / 2));
                    _trackP.Width = t; g.DrawEllipse(_trackP, box);
                    if (fracs[i] > 0.002f) { _pen.Color = color; _pen.Width = t; g.DrawArc(_pen, box, -90, 360 * fracs[i]); }
                    _fmt.Alignment = StringAlignment.Center;
                    Text(texts[i], GetFont(d * 0.24f, true), Color.White, r, shadow && !e.Pill);
                    break;
                }
                case "gauge": // a 240° dial, open at the bottom
                {
                    float d = Math.Min(r.Width, r.Height), t = Math.Max(2, d * 0.1f);
                    var box = new RectangleF(r.X + (r.Width - d) / 2 + t / 2, r.Y + (r.Height - d) / 2 + t / 2, d - t, d - t);
                    if (box.Width < 2) break;
                    if (e.Pill) g.FillEllipse(_pillB, RectangleF.Inflate(box, -t / 2, -t / 2));
                    _trackP.Width = t; g.DrawArc(_trackP, box, 150, 240);
                    if (fracs[i] > 0.002f) { _pen.Color = color; _pen.Width = t; g.DrawArc(_pen, box, 150, 240 * fracs[i]); }
                    _fmt.Alignment = StringAlignment.Center;
                    Text(texts[i], GetFont(d * 0.24f, true), Color.White, r, shadow && !e.Pill);
                    break;
                }
                case "bar":
                    _brush.Color = color;
                    if (e.Sensor == "") { g.FillRectangle(_brush, r); break; } // no sensor: a plain divider line
                    g.FillRectangle(_trackB, r);
                    if (!(fracs[i] > 0)) break;
                    if (r.Height > r.Width) g.FillRectangle(_brush, r.X, r.Bottom - r.Height * fracs[i], r.Width, r.Height * fracs[i]); // upright: fills from the bottom
                    else g.FillRectangle(_brush, r.X, r.Y, r.Width * fracs[i], r.Height);
                    break;
                case "analog":
                    Analog(r, color, now, e.Seconds, e.Pill);
                    break;
                default: // text, value, clock, date
                    if (e.Pill)
                        using (var path = Round(r, r.Height / 2)) g.FillPath(_pillB, path);
                    _fmt.Alignment = e.Align == 0 ? StringAlignment.Near : e.Align == 2 ? StringAlignment.Far : StringAlignment.Center;
                    var inner = e.Pill && e.Align != 1 ? RectangleF.Inflate(r, -r.Height * 0.4f, 0) : r;
                    Text(texts[i], GetFont(e.H * 0.78f, e.Bold), color, inner, shadow && !e.Pill);
                    break;
            }
        }
    }

    void Text(string s, Font f, Color color, RectangleF r, bool shadow)
    {
        if (s.Length == 0) return;
        if (shadow)
        {
            float d = Math.Max(1.5f, f.Size / 22);
            r.Offset(d, d); _g!.DrawString(s, f, _shadowB, r, _fmt);
            r.Offset(-d, -d);
        }
        _brush.Color = color;
        _g!.DrawString(s, f, _brush, r, _fmt);
    }

    void Analog(RectangleF box, Color color, DateTime t, bool seconds, bool dim)
    {
        var g = _g!;
        float cx = box.X + box.Width / 2, cy = box.Y + box.Height / 2, r = Math.Min(box.Width, box.Height) / 2 - 2;
        if (r < 6) return;
        if (dim) g.FillEllipse(_pillB, cx - r, cy - r, r * 2, r * 2);
        _pen.Color = Color.FromArgb(170, color); _pen.Width = Math.Max(1, r * 0.02f);
        for (int i = 0; i < 12; i++)
        {
            var (s, c) = MathF.SinCos(i * MathF.PI / 6);
            float inner = i % 3 == 0 ? 0.82f : 0.9f;
            g.DrawLine(_pen, cx + s * r * inner, cy - c * r * inner, cx + s * r, cy - c * r);
        }
        Hand((t.Hour % 12 + t.Minute / 60f) / 12, 0.52f, r * 0.06f, color);
        Hand((t.Minute + t.Second / 60f) / 60, 0.78f, r * 0.04f, color);
        if (seconds) Hand(t.Second / 60f, 0.86f, r * 0.02f, Color.FromArgb(170, color));
        _brush.Color = color;
        g.FillEllipse(_brush, cx - r * 0.05f, cy - r * 0.05f, r * 0.1f, r * 0.1f);

        void Hand(float turn, float len, float width, Color c)
        {
            var (s, co) = MathF.SinCos(turn * MathF.Tau);
            _pen.Color = c; _pen.Width = Math.Max(1, width);
            g.DrawLine(_pen, cx, cy, cx + s * r * len, cy - co * r * len);
        }
    }

    static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>
    /// Logical canvas -> the panel's native 240x320 layout inside the USB packet.
    /// 0° is what InfoHub does for landscape (QTransform.rotate(-90)); 180° is the same turned upside down.
    /// </summary>
    public static void Blit(ReadOnlySpan<ushort> canvas, int rotation, Span<byte> packet)
    {
        Span<ushort> n = MemoryMarshal.Cast<byte, ushort>(packet.Slice(Lcd.HeaderLen, Lcd.Pixels * 2));
        const int NW = Lcd.NativeW, NH = Lcd.NativeH; // 240 x 320; landscape canvas is NH wide, NW tall
        switch (rotation)
        {
            case 0:
                for (int y = 0; y < NH; y++) for (int x = 0; x < NW; x++) n[y * NW + x] = canvas[x * NH + (NH - 1 - y)];
                break;
            case 180:
                for (int y = 0; y < NH; y++) for (int x = 0; x < NW; x++) n[y * NW + x] = canvas[(NW - 1 - x) * NH + y];
                break;
            case 90:
                canvas.CopyTo(n);
                break;
            default:
                for (int i = 0; i < Lcd.Pixels; i++) n[i] = canvas[Lcd.Pixels - 1 - i];
                break;
        }
    }

    public void SavePng(string path) => _bmp!.Save(path, ImageFormat.Png);

    public void Dispose()
    {
        _g?.Dispose(); _bmp?.Dispose();
        foreach (var f in _fonts.Values) f.Dispose();
        _brush.Dispose(); _shadowB.Dispose(); _pillB.Dispose(); _trackB.Dispose(); _dimB.Dispose(); _pen.Dispose(); _trackP.Dispose(); _fmt.Dispose();
    }
}
