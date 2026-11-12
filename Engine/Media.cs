using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace InfoHubLite;

/// <summary>
/// .ihl = media pre-converted once to exactly what the panel eats, so playback is a file read and a USB write.
///   "IHL1" | width u16 | height u16 | frame count u32 | reserved u32 | delay ms u16[count] | RGB565 frames
/// </summary>
sealed class Media : IDisposable
{
    const int HeaderLen = 16;
    readonly FileStream _fs;
    readonly ushort[] _delays;
    public readonly int W, H, Count;

    Media(FileStream fs, int w, int h, ushort[] delays) { _fs = fs; W = w; H = h; _delays = delays; Count = delays.Length; }

    public static Media? Open(string path)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1); // unbuffered: reads land straight in our buffer
            Span<byte> h = stackalloc byte[HeaderLen];
            fs.ReadExactly(h);
            int w = BitConverter.ToUInt16(h[4..]), ht = BitConverter.ToUInt16(h[6..]);
            long n = BitConverter.ToUInt32(h[8..]);
            if (h[0] != 'I' || h[1] != 'H' || h[2] != 'L' || h[3] != '1' || w * ht != Lcd.Pixels || n < 1
                || fs.Length != HeaderLen + n * 2 + n * Lcd.Pixels * 2) { fs.Dispose(); return null; }
            var delays = new ushort[n];
            fs.ReadExactly(MemoryMarshal.AsBytes(delays.AsSpan()));
            return new Media(fs, w, ht, delays);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { fs?.Dispose(); return null; }
    }

    public int Delay(int i) => Math.Max(1, (int)_delays[i]);

    /// <summary>
    /// Where an item's frame cache lives. One file per orientation and per placement ("name.ihl", "name.p.ihl",
    /// "name~2.5_0.3_0.ihl", ...): a new placement is a new file, so a cache the engine is playing is never overwritten.
    /// </summary>
    public static string PathFor(string name, bool portrait, Fit? fit = null) =>
        Path.Combine(Config.MediaDir, Path.GetFileNameWithoutExtension(Path.GetFileName(name))
            + (fit == null ? "" : "~" + fit.Key.Replace('|', '_')) + (portrait ? ".p.ihl" : ".ihl"));

    public void Read(int i, Span<ushort> dst)
    {
        _fs.Position = HeaderLen + Count * 2L + (long)i * Lcd.Pixels * 2;
        _fs.ReadExactly(MemoryMarshal.AsBytes(dst));
    }

    public void Dispose() => _fs.Dispose();

    // ---- one-shot conversion; runs in a short-lived process ("InfoHubLite import"), never in the resident engine ----

    /// <param name="crop">source-pixel rectangle, or null for a centred cover fit</param>
    /// <param name="minFrameMs">frames closer together than this are merged. The official app sends at most one frame per
    /// 70 ms; 60 keeps typical 15 fps GIFs intact. Tune once the panel's real throughput is measured.</param>
    public static int Import(string src, string dst, bool portrait, RectangleF? crop, bool dither = true, int minFrameMs = 60, int maxFrames = 1200, Fit? fit = null)
    {
        using var img = Image.FromFile(src);
        bool animated = Array.IndexOf(img.FrameDimensionsList, FrameDimension.Time.Guid) >= 0;
        int srcFrames = animated ? img.GetFrameCount(FrameDimension.Time) : 1;

        // Pass 1: decide which source frames survive and how long each is shown.
        var keep = new List<(int index, ushort delay)>();
        if (srcFrames > 1)
        {
            byte[] raw = img.GetPropertyItem(0x5100)!.Value!; // PropertyTagFrameDelay: 4-byte LE, 1/100 s per frame
            int pending = 0;
            for (int i = 0; i < srcFrames; i++)
            {
                int d = BitConverter.ToInt32(raw, i * 4) * 10;
                pending += d <= 10 ? 100 : d;                  // 0/1 cs means "unspecified"; browsers play those at 100 ms
                if (pending < minFrameMs && i < srcFrames - 1) continue;
                keep.Add((i, (ushort)Math.Min(pending, ushort.MaxValue)));
                pending = 0;
                // ponytail: hard frame cap (1200 frames = 184 MB on disk); raise maxFrames if longer clips matter
                if (keep.Count == maxFrames) break;
            }
        }
        else keep.Add((0, 0));

        Write(dst, portrait, keep.Select(k => k.delay).ToArray(), (i, g, w, h) =>
        {
            if (srcFrames > 1) img.SelectActiveFrame(FrameDimension.Time, keep[i].index);
            DrawCover(g, img, w, h, crop ?? CropFor(img.Width, img.Height, w, h, fit));
        }, dither);
        return keep.Count;
    }

    /// <summary>Scales <paramref name="img"/> to fill w x h (centred, cropping the overflow) unless a crop is given.</summary>
    public static void DrawCover(Graphics g, Image img, int w, int h, RectangleF? crop)
    {
        RectangleF s = crop ?? Cover(img.Width, img.Height, w, h);
        using var edge = new ImageAttributes();
        edge.SetWrapMode(WrapMode.TileFlipXY); // without this, bicubic blends the outermost pixels with nothing and leaves a dark rim
        g.DrawImage(img, new Rectangle(0, 0, w, h), s.X, s.Y, s.Width, s.Height, GraphicsUnit.Pixel, edge);
    }

    /// <summary>Writes an .ihl; <paramref name="draw"/>(frame index, graphics, width, height) paints each frame in order.</summary>
    public static unsafe void Write(string dst, bool portrait, IReadOnlyList<ushort> delays, Action<int, Graphics, int, int> draw, bool dither = true)
    {
        int w = portrait ? Lcd.NativeW : Lcd.NativeH, h = portrait ? Lcd.NativeH : Lcd.NativeW;
        using var canvas = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(canvas);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        string tmp = dst + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
        {
            Span<byte> hdr = stackalloc byte[HeaderLen];
            hdr.Clear();
            "IHL1"u8.CopyTo(hdr);
            BitConverter.TryWriteBytes(hdr[4..], (ushort)w);
            BitConverter.TryWriteBytes(hdr[6..], (ushort)h);
            BitConverter.TryWriteBytes(hdr[8..], (uint)delays.Count);
            fs.Write(hdr);
            foreach (ushort d in delays) fs.Write(BitConverter.GetBytes(d));

            var frame = new ushort[Lcd.Pixels];
            for (int i = 0; i < delays.Count; i++)
            {
                g.Clear(Color.Black);
                draw(i, g, w, h);
                var bd = canvas.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                To565((byte*)bd.Scan0, bd.Stride, w, h, frame, dither);
                canvas.UnlockBits(bd);
                fs.Write(MemoryMarshal.AsBytes(frame.AsSpan()));
            }
        }
        File.Move(tmp, dst, true);
    }

    static RectangleF Cover(int sw, int sh, int w, int h)
    {
        float scale = Math.Max((float)w / sw, (float)h / sh), cw = w / scale, ch = h / scale;
        return new RectangleF((sw - cw) / 2, (sh - ch) / 2, cw, ch);
    }

    /// <summary>The part of a sw x sh picture that ends up on a w x h panel for a given placement.</summary>
    public static RectangleF CropFor(int sw, int sh, int w, int h, Fit? fit)
    {
        RectangleF c = Cover(sw, sh, w, h);
        if (fit == null) return c;
        float z = (float)Math.Clamp(fit.Zoom, 1, 8), cw = c.Width / z, ch = c.Height / z;
        return new RectangleF((sw - cw) * (0.5f + (float)Math.Clamp(fit.X, -1, 1) / 2), (sh - ch) * (0.5f + (float)Math.Clamp(fit.Y, -1, 1) / 2), cw, ch);
    }

    static ReadOnlySpan<byte> Bayer => [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];

    /// <summary>BGRA8888 -> RGB565. Ordered dithering hides the banding plain truncation leaves in gradients.</summary>
    public static unsafe void To565(byte* bgra, int stride, int w, int h, Span<ushort> dst, bool dither)
    {
        for (int y = 0; y < h; y++)
        {
            byte* p = bgra + y * stride;
            for (int x = 0; x < w; x++, p += 4)
            {
                int t = dither ? Bayer[(y & 3) << 2 | (x & 3)] : 0;
                int r = Math.Min(255, p[2] + (t >> 1)) >> 3, gg = Math.Min(255, p[1] + (t >> 2)) >> 2, b = Math.Min(255, p[0] + (t >> 1)) >> 3;
                dst[y * w + x] = (ushort)(r << 11 | gg << 5 | b);
            }
        }
    }
}
