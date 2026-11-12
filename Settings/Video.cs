using System.Drawing;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace InfoHubLite;

/// <summary>MP4/AVI -> .ihl using the decoders Windows already ships (Media Foundation via Windows.Media.Editing).</summary>
static class Video
{
    public static readonly string[] Extensions = [".mp4", ".avi", ".mov", ".wmv", ".m4v", ".mkv"];

    /// <summary>Blocks; call from a worker thread. frameMs 66 = 15 fps, about what the official app could show.</summary>
    public static int Import(string src, string dst, bool portrait, int frameMs = 66, int maxFrames = 1200, Fit? fit = null)
    {
        var file = StorageFile.GetFileFromPathAsync(src).AsTask().GetAwaiter().GetResult();
        var clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var comp = new MediaComposition();
        comp.Clips.Add(clip);

        // ponytail: clips are cut at maxFrames (1200 frames = 80 s, 184 MB on disk); raise it if longer loops matter
        int n = (int)Math.Clamp(clip.OriginalDuration.TotalMilliseconds / frameMs, 1, maxFrames);
        var enc = clip.GetVideoEncodingProperties();
        int w = portrait ? Lcd.NativeW : Lcd.NativeH, h = portrait ? Lcd.NativeH : Lcd.NativeW;
        // decode just large enough to cover the panel at the requested zoom
        double scale = Math.Max((double)w / enc.Width, (double)h / enc.Height) * Math.Clamp(fit?.Zoom ?? 1, 1, 8);
        int tw = (int)Math.Ceiling(enc.Width * scale), th = (int)Math.Ceiling(enc.Height * scale);

        const int Batch = 24; // bounded memory: decode a second and a half at a time
        IReadOnlyList<ImageStream>? batch = null;
        Media.Write(dst, portrait, Enumerable.Repeat((ushort)frameMs, n).ToArray(), (i, g, cw, ch) =>
        {
            if (i % Batch == 0)
            {
                var times = Enumerable.Range(i, Math.Min(Batch, n - i)).Select(k => TimeSpan.FromMilliseconds((double)k * frameMs)).ToList();
                batch = comp.GetThumbnailsAsync(times, tw, th, VideoFramePrecision.NearestFrame).AsTask().GetAwaiter().GetResult();
            }
            using var frame = batch![i % Batch];
            using var stream = frame.AsStreamForRead();
            using var bmp = new Bitmap(stream);
            Media.DrawCover(g, bmp, cw, ch, Media.CropFor(bmp.Width, bmp.Height, cw, ch, fit));
        });
        return n;
    }

    /// <summary>First frame of a clip, for positioning it in the editor. Blocks; call from a worker thread.</summary>
    public static Bitmap FirstFrame(string src, int maxDim = 1280)
    {
        var file = StorageFile.GetFileFromPathAsync(src).AsTask().GetAwaiter().GetResult();
        var clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        var comp = new MediaComposition();
        comp.Clips.Add(clip);
        var enc = clip.GetVideoEncodingProperties();
        double s = Math.Min(1, (double)maxDim / Math.Max(enc.Width, enc.Height));
        using var frame = comp.GetThumbnailAsync(TimeSpan.Zero, Math.Max(1, (int)(enc.Width * s)), Math.Max(1, (int)(enc.Height * s)), VideoFramePrecision.NearestFrame).AsTask().GetAwaiter().GetResult();
        using var stream = frame.AsStreamForRead();
        using var decoded = new Bitmap(stream);
        return new Bitmap(decoded); // a copy that no longer needs the stream
    }
}
