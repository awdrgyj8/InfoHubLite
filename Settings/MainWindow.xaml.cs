using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using VirtualKey = Windows.System.VirtualKey;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace InfoHubLite;

/// <summary>
/// The layout editor. It edits a draft copy of the config and draws it with the same Renderer the engine uses,
/// so the canvas is exactly what the panel will show. Nothing reaches the panel until "Apply".
/// This file: state, canvas, mouse and keyboard, apply / revert. MainWindow.Panels.cs: the side panels.
/// </summary>
public sealed partial class MainWindow : Window
{
    static readonly string EngineExe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "InfoHubLite.exe"));
    const int Up = 2; // the canvas bitmap is the panel image pixel-doubled, so the editor shows the panel's real pixels

    Config _applied, _draft;
    readonly Renderer _r = new();
    readonly ushort[] _bg = new ushort[Lcd.Pixels];
    readonly byte[] _bgra = new byte[Lcd.Pixels * Up * Up * 4];
    string[] _texts = [];
    float[] _fracs = [];
    WriteableBitmap? _canvasBmp;
    // Selection is held by reference, so reordering or deleting elements cannot leave it pointing at the wrong one.
    readonly List<Element> _picked = [];
    int _sel => _picked.Count == 1 ? Els.IndexOf(_picked[0]) : -1; // index of the single selected element, else -1
    bool _bgMode; // "move background" is on: drags slide the background instead of touching elements
    bool _closing;

    // selection chrome, drawn over the canvas image
    readonly Shapes.Rectangle _marquee;
    readonly Brush _accent;
    readonly List<Shapes.Rectangle> _outlines = [];
    readonly List<(Element e, int x, int y, int w, int h)> _origins = []; // each selected element as it was when a drag started
    readonly Shapes.Rectangle _groupRect; // box around a multi-selection; its corners scale the whole group
    int _gx, _gy, _gw, _gh;               // that box when a group resize started
    bool _syncing;                        // we are setting a control ourselves; ignore its change event

    // Undo / redo: whole-draft snapshots. A burst of changes (a slider drag, typing) settles into one step.
    readonly List<string> _undo = [], _redo = [];
    string _history = "";
    readonly DispatcherQueueTimer _settle;
    string _bgSourceFailed = ""; // media whose original could not be decoded
    double _curX, _curY;
    readonly Shapes.Rectangle[] _handles = new Shapes.Rectangle[4];
    readonly Shapes.Line _guideV, _guideH;

    // The original picture behind the layout (its first frame), so its placement can be adjusted and previewed live.
    System.Drawing.Bitmap? _bgSource, _fitCanvas;
    string _bgSourceName = "";
    double _panX, _panY; // placement when a background drag started

    // drag state, in panel pixels
    int _dragMode, _dragHandle; // mode: 0 none, 1 move, 2 resize one element, 3 slide background, 4 rubber band, 5 resize group
    double _startX, _startY;
    int _ox, _oy, _ow, _oh;

    // live view of what the engine is sending
    readonly DispatcherQueueTimer _poll;
    readonly ushort[] _livePx = new ushort[Lcd.Pixels];
    readonly byte[] _liveBgra = new byte[Lcd.Pixels * 4];
    WriteableBitmap? _liveBmp;
    MemoryMappedFile? _map;
    MemoryMappedViewAccessor? _view;
    int _serial = -1, _ticks;

    // "Apply" is only done when the engine reports it is running this exact config; until then we keep checking.
    int _expectHash, _verifyDeadline;
    bool _mismatch;

    double Zoom => _draft.Portrait ? 1.5 : 2; // on-screen pixels per panel pixel
    List<Element> Els => _draft.Elements!;

    public MainWindow()
    {
        InitializeComponent();
        Title = "InfoHub Lite";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        string ico = Path.Combine(AppContext.BaseDirectory, "InfoHubLite.ico"), png = Path.Combine(AppContext.BaseDirectory, "logo-256.png");
        if (File.Exists(ico)) AppWindow.SetIcon(ico); // taskbar and Alt+Tab
        if (File.Exists(png)) TitleLogo.Source = new BitmapImage(new Uri(png));
        double scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1310 * scale), (int)(820 * scale)));

        _applied = Config.Load() ?? new Config().Normalize();
        _draft = _applied.Clone();
        Expect(_applied.LoadedHash, 40); // also catches an engine that is already running on some other config
        _history = _draft.ToJson();
        _settle = DispatcherQueue.CreateTimer();
        _settle.Interval = TimeSpan.FromMilliseconds(600); _settle.IsRepeating = false;
        _settle.Tick += (_, _) => Record();

        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _accent = accent;
        _marquee = new Shapes.Rectangle { Stroke = accent, StrokeThickness = 1, Fill = new SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(40, 76, 194, 255)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _guideV = new Shapes.Line { Stroke = accent, StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _guideH = new Shapes.Line { Stroke = accent, StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        _groupRect = new Shapes.Rectangle { Stroke = accent, StrokeThickness = 1, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
        Overlay.Children.Add(_guideV); Overlay.Children.Add(_guideH); Overlay.Children.Add(_marquee); Overlay.Children.Add(_groupRect);
        for (int i = 0; i < 4; i++)
        {
            _handles[i] = new Shapes.Rectangle { Width = 10, Height = 10, Fill = accent, RadiusX = 2, RadiusY = 2, IsHitTestVisible = false };
            Overlay.Children.Add(_handles[i]);
        }

        BuildAddMenu();
        Reconfigure();
        BuildProps();
        UpdateDirty();
        _ = LoadSensors();

        if (!Config.Isolated && Process.GetProcessesByName("InfoHubLite").Length == 0 && File.Exists(EngineExe)) Process.Start(EngineExe).Dispose();
        _ = RepairCaches();

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(200);
        _poll.Tick += (_, _) => Poll();
        _poll.Start();
        AppWindow.Closing += OnClosing;
        Closed += (_, _) => { _poll.Stop(); _view?.Dispose(); _map?.Dispose(); _r.Dispose(); _thumbR.Dispose(); _bgSource?.Dispose(); _fitCanvas?.Dispose(); };
    }

    // ---------- canvas ----------

    /// <summary>Everything that depends on the orientation: renderer, bitmap, on-screen size, background, thumbnails.</summary>
    void Reconfigure()
    {
        _r.Configure(_draft.Portrait);
        _canvasBmp = new WriteableBitmap(_r.W * Up, _r.H * Up);
        CanvasImage.Source = _canvasBmp;
        CanvasImage.Width = Overlay.Width = _r.W * Zoom;
        CanvasImage.Height = Overlay.Height = _r.H * Zoom;
        LoadBackground();
        BuildTemplates();
        Render();
    }

    string? CurrentMedia => _draft.Playlist.Count > 0 ? _draft.Playlist[Math.Clamp(_draft.Current, 0, _draft.Playlist.Count - 1)] : null;

    /// <summary>True when the selected media's original is loaded, so it can be zoomed and slid around.</summary>
    bool CanPlaceBackground => CurrentMedia is { } n && n == _bgSourceName && _bgSource != null;

    /// <summary>First frame of the selected media, or the solid colour. The editor shows a still; the panel animates.</summary>
    unsafe void LoadBackground()
    {
        string? name = CurrentMedia;
        if ((name ?? "") != _bgSourceName)
        {
            _bgSourceName = name ?? ""; _bgSource?.Dispose(); _bgSource = null;
            if (name != null) _ = LoadSource(name);
        }
        SyncBgMode();
        if (name != null && _bgSource != null)
        {
            // Draw the original with the draft's placement, through the same crop and 565 conversion the importer uses,
            // so an unapplied placement previews exactly as it will be converted.
            if (_fitCanvas == null || _fitCanvas.Width != _r.W) { _fitCanvas?.Dispose(); _fitCanvas = new System.Drawing.Bitmap(_r.W, _r.H, System.Drawing.Imaging.PixelFormat.Format32bppArgb); }
            using (var g = System.Drawing.Graphics.FromImage(_fitCanvas))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.Clear(System.Drawing.Color.Black); // this canvas is reused: without it a picture with transparency shows the previous one through
                Media.DrawCover(g, _bgSource, _r.W, _r.H, Media.CropFor(_bgSource.Width, _bgSource.Height, _r.W, _r.H, _draft.Fits.GetValueOrDefault(name)));
            }
            var bd = _fitCanvas.LockBits(new System.Drawing.Rectangle(0, 0, _r.W, _r.H), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            Media.To565((byte*)bd.Scan0, bd.Stride, _r.W, _r.H, _bg, true);
            _fitCanvas.UnlockBits(bd);
            return;
        }
        using var m = name != null ? Media.Open(Cache(_draft, name)) : null;
        if (m != null && m.W == _r.W) m.Read(0, _bg);
        else Array.Fill(_bg, Renderer.To565(Renderer.ParseColor(_draft.Background, System.Drawing.Color.Black)));
    }

    /// <summary>Decodes the first frame of a media item's original file off the UI thread.</summary>
    async Task LoadSource(string name)
    {
        if (!_draft.Sources.TryGetValue(name, out string? src) || !File.Exists(src)) return;
        System.Drawing.Bitmap? bmp = null;
        try
        {
            bmp = await Task.Run(() =>
            {
                if (Video.Extensions.Contains(Path.GetExtension(src).ToLowerInvariant())) return Video.FirstFrame(src);
                using var img = System.Drawing.Image.FromFile(src);
                double s = Math.Min(1, 1600.0 / Math.Max(img.Width, img.Height)); // plenty for a 320x240 preview, even zoomed
                return new System.Drawing.Bitmap(img, Math.Max(1, (int)(img.Width * s)), Math.Max(1, (int)(img.Height * s)));
            });
        }
        catch (Exception) { /* unreadable original: placement stays unavailable for this item, and the panel says so */ }
        if (_bgSourceName != name) { bmp?.Dispose(); return; } // the selection moved on while decoding
        _bgSourceFailed = bmp == null ? name : "";
        _bgSource = bmp;
        LoadBackground(); Render();
        if (_picked.Count == 0) BuildProps();
    }

    /// <summary>Slides the background by a drag distance in panel pixels, within the room the picture has.</summary>
    void Pan(double dx, double dy)
    {
        string name = CurrentMedia!;
        var src = _bgSource!;
        double zoom = _draft.Fits.GetValueOrDefault(name)?.Zoom ?? 1;
        var start = Media.CropFor(src.Width, src.Height, _r.W, _r.H, new Fit { Zoom = zoom, X = _panX, Y = _panY });
        double k = _r.W / (double)start.Width; // panel pixels per source pixel
        double roomX = src.Width - start.Width, roomY = src.Height - start.Height;
        double x = Math.Clamp(start.X - dx / k, 0, roomX), y = Math.Clamp(start.Y - dy / k, 0, roomY);
        SetFit(name, zoom, roomX > 0.5 ? x / roomX * 2 - 1 : 0, roomY > 0.5 ? y / roomY * 2 - 1 : 0);
        // At 100% a picture only has spare room along its longer side (none at all if it matches the panel's shape).
        if (roomX <= 0.5 && roomY <= 0.5) HintText.Text = "這張圖剛好填滿螢幕，沒有移動空間。先用旁邊的滑桿或滾輪放大，就能移動。";
        else if ((roomX <= 0.5 && Math.Abs(dx) > Math.Abs(dy) * 2) || (roomY <= 0.5 && Math.Abs(dy) > Math.Abs(dx) * 2)) HintText.Text = "這個方向沒有多餘的畫面可移動。放大後就能往這個方向移動。";
        else UpdateHint();
    }

    internal void SetFit(string name, double zoom, double x, double y)
    {
        var f = new Fit { Zoom = Math.Round(Math.Clamp(zoom, 1, 4), 3), X = Math.Round(Math.Clamp(x, -1, 1), 3), Y = Math.Round(Math.Clamp(y, -1, 1), 3) };
        // The default placement is "no entry", so resetting really does leave nothing to apply.
        if (f.Zoom == 1 && f.X == 0 && f.Y == 0) _draft.Fits.Remove(name); else _draft.Fits[name] = f;
        LoadBackground();
        Render();
    }

    void Overlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        // The wheel zooms the background while "move background" is on, or when no element is selected.
        if (!CanPlaceBackground || (!_bgMode && _picked.Count > 0)) return;
        string name = CurrentMedia!;
        var f = _draft.Fits.GetValueOrDefault(name);
        int delta = e.GetCurrentPoint(Overlay).Properties.MouseWheelDelta;
        SetFit(name, (f?.Zoom ?? 1) * (delta > 0 ? 1.1 : 1 / 1.1), f?.X ?? 0, f?.Y ?? 0);
        e.Handled = true;
        UpdateDirty();
    }

    void BgZoom_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        // The slider also raises this while the window's XAML is still loading, before there is a draft.
        if (_syncing || _draft is null || !CanPlaceBackground) return;
        string name = CurrentMedia!;
        var f = _draft.Fits.GetValueOrDefault(name);
        SetFit(name, e.NewValue / 100.0, f?.X ?? 0, f?.Y ?? 0);
        UpdateDirty();
    }

    void BgReset_Click(object sender, RoutedEventArgs e)
    {
        if (!CanPlaceBackground) return;
        SetFit(CurrentMedia!, 1, 0, 0);
        UpdateDirty();
    }

    (float? value, Kind kind) Read(Dictionary<string, (float?, Kind)> values, Element e) =>
        !e.UsesSensor || e.Sensor == "" ? (null, Kind.Other)
        : values.TryGetValue(e.Sensor, out var v) ? v
        : (null, e.Sensor.EndsWith(".temp") ? Kind.Temperature : e.Sensor.EndsWith(".load") ? Kind.Load : Kind.Other);

    void Render()
    {
        if (_texts.Length != Els.Count) { _texts = new string[Els.Count]; _fracs = new float[Els.Count]; }
        var now = DateTime.Now;
        for (int i = 0; i < Els.Count; i++)
        {
            var (value, kind) = Read(_values, Els[i]);
            _texts[i] = Fmt.Text(Els[i], value, kind, _draft.Fahrenheit, now);
            _fracs[i] = Fmt.Fraction(value, kind);
        }
        _bg.AsSpan().CopyTo(_r.Canvas);
        if (_draft.Dim > 0 && Els.Count > 0) _r.Dim(_draft.Dim);
        _r.Draw(Els, _texts, _fracs, _draft.Shadow, now);

        ToBgra(_r.Canvas, _r.W, _r.H, Up, _bgra);
        using (var s = _canvasBmp!.PixelBuffer.AsStream()) s.Write(_bgra);
        _canvasBmp.Invalidate();
        UpdateChrome();
    }

    /// <summary>RGB565 -> BGRA8888, each source pixel repeated <paramref name="up"/> times in both directions.</summary>
    static void ToBgra(ReadOnlySpan<ushort> src, int w, int h, int up, Span<byte> dst)
    {
        int rowBytes = w * up * 4;
        for (int y = 0; y < h; y++)
        {
            Span<byte> row = dst.Slice(y * up * rowBytes, rowBytes);
            for (int x = 0, o = 0; x < w; x++)
            {
                int p = src[y * w + x], r = p >> 11, g = (p >> 5) & 63, b = p & 31;
                byte bb = (byte)(b << 3 | b >> 2), gg = (byte)(g << 2 | g >> 4), rr = (byte)(r << 3 | r >> 2);
                for (int k = 0; k < up; k++, o += 4) { row[o] = bb; row[o + 1] = gg; row[o + 2] = rr; row[o + 3] = 255; }
            }
            for (int k = 1; k < up; k++) row.CopyTo(dst.Slice((y * up + k) * rowBytes, rowBytes));
        }
    }

    /// <summary>The box the corner handles sit on: the element itself, or the bounding box of a multi-selection.</summary>
    (int x, int y, int w, int h) PickBounds()
    {
        int x = _picked.Min(e => e.X), y = _picked.Min(e => e.Y);
        return (x, y, _picked.Max(e => e.X + e.W) - x, _picked.Max(e => e.Y + e.H) - y);
    }

    void UpdateChrome()
    {
        // One dashed outline per selected element, plus corner handles on the element or on the whole group.
        while (_outlines.Count < _picked.Count)
        {
            var r = new Shapes.Rectangle { Stroke = _accent, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false };
            _outlines.Add(r);
            Overlay.Children.Add(r);
        }
        double z = Zoom;
        for (int i = 0; i < _outlines.Count; i++)
        {
            bool on = i < _picked.Count;
            _outlines[i].Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on) continue;
            var e = _picked[i];
            Canvas.SetLeft(_outlines[i], e.X * z - 1); Canvas.SetTop(_outlines[i], e.Y * z - 1);
            _outlines[i].Width = e.W * z + 2; _outlines[i].Height = e.H * z + 2;
        }
        bool any = _picked.Count > 0;
        foreach (var h in _handles) h.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        _groupRect.Visibility = _picked.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (!any) return;
        var b = PickBounds();
        Canvas.SetLeft(_groupRect, b.x * z - 3); Canvas.SetTop(_groupRect, b.y * z - 3);
        _groupRect.Width = b.w * z + 6; _groupRect.Height = b.h * z + 6;
        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_handles[i], (i % 2 == 0 ? b.x : b.x + b.w) * z - 5);
            Canvas.SetTop(_handles[i], (i < 2 ? b.y : b.y + b.h) * z - 5);
        }
    }

    void UpdateHint() => HintText.Text = _bgMode
        ? "拖曳畫布可移動背景，滾輪縮放。再按一次「移動背景」回到編輯元件。"
        : "拖曳元件可移動，拉四個角可縮放。在空白處拉出範圍，可一次選取多個一起移動。";

    /// <summary>"Move background" is only offered when the selected media's original is available to re-crop.</summary>
    void SyncBgMode()
    {
        bool can = CanPlaceBackground;
        BgModeButton.IsEnabled = BgZoom.IsEnabled = BgResetButton.IsEnabled = can;
        if (!can && _bgMode) { _bgMode = false; BgModeButton.IsChecked = false; }
        int zoom = can ? (int)Math.Round((_draft.Fits.GetValueOrDefault(CurrentMedia!)?.Zoom ?? 1) * 100) : 100;
        _syncing = true; BgZoom.Value = zoom; _syncing = false;
        BgZoomText.Text = zoom + "%";
        if (_dragMode != 3) UpdateHint();
    }

    void BgMode_Click(object sender, RoutedEventArgs e)
    {
        _bgMode = BgModeButton.IsChecked == true;
        if (_bgMode) Pick([]);
        UpdateHint();
    }

    // ---------- mouse: thin wrappers over Press / Move / Release, which work in panel pixels ----------

    void Overlay_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Overlay).Position;
        ApplyButton.Focus(FocusState.Programmatic); // take focus out of any text box so Delete and the arrow keys act on the elements
        bool add = (e.KeyModifiers & (Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift)) != 0;
        Press(p.X / Zoom, p.Y / Zoom, add);
        Overlay.CapturePointer(e.Pointer);
    }

    void Overlay_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragMode == 0) return;
        var p = e.GetCurrentPoint(Overlay).Position;
        Move(p.X / Zoom, p.Y / Zoom);
    }

    void Overlay_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        Overlay.ReleasePointerCapture(e.Pointer);
        Release();
    }

    /// <param name="add">Ctrl or Shift is held: toggle the clicked element in the selection instead of replacing it</param>
    internal void Press(double x, double y, bool add = false)
    {
        const double grab = 6; // how close to a corner counts as grabbing its handle
        _startX = _curX = x; _startY = _curY = y;
        if (_bgMode && CanPlaceBackground)
        {
            var f = _draft.Fits.GetValueOrDefault(CurrentMedia!);
            _dragMode = 3; _panX = f?.X ?? 0; _panY = f?.Y ?? 0;
            return;
        }
        if (_picked.Count > 0)
        {
            var b = PickBounds();
            for (int i = 0; i < 4; i++)
            {
                double hx = i % 2 == 0 ? b.x : b.x + b.w, hy = i < 2 ? b.y : b.y + b.h;
                if (Math.Abs(x - hx) > grab || Math.Abs(y - hy) > grab) continue;
                if (_picked.Count == 1) { Begin(2, i, _picked[0]); return; }
                // several elements: the corner scales all of them together
                _dragMode = 5; _dragHandle = i; (_gx, _gy, _gw, _gh) = b;
                Capture();
                return;
            }
        }
        Element? hit = null;
        for (int i = Els.Count - 1; i >= 0 && hit == null; i--) // topmost first
        {
            var e = Els[i];
            double pad = e.H < 10 ? 5 : 0; // thin bars and dividers get a taller hit area
            if (x >= e.X && x <= e.X + e.W && y >= e.Y - pad && y <= e.Y + e.H + pad) hit = e;
        }
        if (hit == null)
        {
            // Empty canvas: start a rubber band. What it catches is decided on release.
            if (!add && _picked.Count > 0) Pick([]);
            _dragMode = 4;
            return;
        }
        if (add)
        {
            var next = _picked.ToList();
            if (!next.Remove(hit)) next.Add(hit);
            Pick(next);
            _dragMode = 0;
            return;
        }
        if (!_picked.Contains(hit)) Pick([hit]);
        // A drag moves everything that is selected, not just the element under the pointer.
        Capture();
        _dragMode = 1;
    }

    /// <summary>Remembers every selected element's rectangle, so a drag is always computed from where it started.</summary>
    void Capture()
    {
        _origins.Clear();
        foreach (var e in _picked) _origins.Add((e, e.X, e.Y, e.W, e.H));
    }

    /// <summary>Scales the captured elements by <paramref name="s"/> around a fixed point.</summary>
    static void Scale(IEnumerable<(Element e, int x, int y, int w, int h)> origins, double s, double ax, double ay)
    {
        foreach (var (e, x, y, w, h) in origins)
        {
            e.W = Math.Max(1, (int)Math.Round(w * s)); e.H = Math.Max(1, (int)Math.Round(h * s));
            e.X = (int)Math.Round(ax + (x - ax) * s); e.Y = (int)Math.Round(ay + (y - ay) * s);
        }
    }

    void Begin(int mode, int handle, Element e) { _dragMode = mode; _dragHandle = handle; _ox = e.X; _oy = e.Y; _ow = e.W; _oh = e.H; }

    internal void Move(double x, double y)
    {
        _curX = x; _curY = y;
        if (_dragMode == 3) { Pan(x - _startX, y - _startY); return; }
        if (_dragMode == 4)
        {
            double l = Math.Clamp(Math.Min(x, _startX), 0, _r.W), t = Math.Clamp(Math.Min(y, _startY), 0, _r.H);
            double r = Math.Clamp(Math.Max(x, _startX), 0, _r.W), b = Math.Clamp(Math.Max(y, _startY), 0, _r.H);
            Canvas.SetLeft(_marquee, l * Zoom); Canvas.SetTop(_marquee, t * Zoom);
            _marquee.Width = (r - l) * Zoom; _marquee.Height = (b - t) * Zoom;
            _marquee.Visibility = Visibility.Visible;
            return;
        }
        if (_dragMode == 0 || _picked.Count == 0) return;
        int dx = (int)Math.Round(x - _startX), dy = (int)Math.Round(y - _startY);
        bool snapV = false, snapH = false;
        if (_dragMode == 1)
        {
            // Snap and clamp the group as a whole (its bounding box), then carry every member by the same offset.
            int gx = _origins.Min(o => o.x), gy = _origins.Min(o => o.y);
            int gw = _origins.Max(o => o.x + o.w) - gx, gh = _origins.Max(o => o.y + o.h) - gy;
            int nx = gx + dx, ny = gy + dy;
            (nx, snapV) = Snap(nx, gw, _r.W);
            (ny, snapH) = Snap(ny, gh, _r.H);
            nx = Math.Clamp(nx, 8 - gw, _r.W - 8); ny = Math.Clamp(ny, 8 - gh, _r.H - 8); // never let it leave the canvas entirely
            foreach (var (e, ox, oy, _, _) in _origins) { e.X = ox + nx - gx; e.Y = oy + ny - gy; }
        }
        else if (_dragMode == 5)
        {
            // The dragged corner follows the pointer, the opposite corner stays put, proportions are kept.
            double ax = _dragHandle % 2 == 0 ? _gx + _gw : _gx, ay = _dragHandle < 2 ? _gy + _gh : _gy;
            double cx = _dragHandle % 2 == 0 ? _gx : _gx + _gw, cy = _dragHandle < 2 ? _gy : _gy + _gh;
            double sx = _gw > 0 ? (cx + dx - ax) / (cx - ax) : 1, sy = _gh > 0 ? (cy + dy - ay) / (cy - ay) : 1;
            Scale(_origins, Math.Clamp(Math.Max(sx, sy), 0.1, 8), ax, ay);
        }
        else
        {
            var e = _picked[0];
            int min = e.Type == "bar" ? 1 : 6;
            int left = _ox, top = _oy, right = _ox + _ow, bottom = _oy + _oh;
            if (_dragHandle % 2 == 0) left = Math.Min(left + dx, right - min); else right = Math.Max(right + dx, left + min);
            if (_dragHandle < 2) top = Math.Min(top + dy, bottom - min); else bottom = Math.Max(bottom + dy, top + min);
            e.X = left; e.Y = top; e.W = right - left; e.H = bottom - top;
        }
        Guide(_guideV, snapV, _r.W / 2.0 * Zoom, 0, _r.W / 2.0 * Zoom, _r.H * Zoom);
        Guide(_guideH, snapH, 0, _r.H / 2.0 * Zoom, _r.W * Zoom, _r.H / 2.0 * Zoom);
        Render();
    }

    /// <summary>Pulls a position onto the canvas centre line or an edge when it is within a few pixels.</summary>
    static (int pos, bool centred) Snap(int pos, int size, int canvas)
    {
        const int near = 4;
        if (Math.Abs(pos + size / 2.0 - canvas / 2.0) <= near) return ((int)Math.Round(canvas / 2.0 - size / 2.0), true);
        if (Math.Abs(pos) <= near) return (0, false);
        if (Math.Abs(pos + size - canvas) <= near) return (canvas - size, false);
        return (pos, false);
    }

    static void Guide(Shapes.Line line, bool on, double x1, double y1, double x2, double y2)
    {
        line.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        line.X1 = x1; line.Y1 = y1; line.X2 = x2; line.Y2 = y2;
    }

    internal void Release()
    {
        if (_dragMode == 4)
        {
            _dragMode = 0;
            _marquee.Visibility = Visibility.Collapsed;
            double l = Math.Min(_startX, _curX), t = Math.Min(_startY, _curY), r = Math.Max(_startX, _curX), b = Math.Max(_startY, _curY);
            if (r - l < 3 && b - t < 3) return; // a plain click on empty canvas: selection was already cleared
            Pick(Els.Where(e => e.X < r && e.X + e.W > l && e.Y < b && e.Y + e.H > t).ToList()); // everything the box touches
            return; // selecting is not an edit
        }
        if (_dragMode == 0) return;
        bool panned = _dragMode == 3;
        _dragMode = 0;
        _guideV.Visibility = _guideH.Visibility = Visibility.Collapsed;
        if (panned) UpdateHint();
        UpdateDirty();
        Record(); // a finished drag is one undo step
    }

    /// <summary>Replaces the selection and shows the matching properties (one element, a group, or the screen).</summary>
    void Pick(IReadOnlyList<Element> elements)
    {
        _picked.Clear();
        _picked.AddRange(elements);
        BuildProps();
        UpdateChrome();
    }

    /// <summary>Selects one element by index (-1 = none, -2 = all of them).</summary>
    public void Select(int index) => Pick(index == -2 ? Els.ToList() : index >= 0 && index < Els.Count ? [Els[index]] : []);

    void Key_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Typing in a field, or using a list or slider: the key belongs to that control.
        if (_picked.Count == 0 || FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or NumberBox or ComboBox or ComboBoxItem or Slider or ColorPicker) return;
        if (sender.Key == VirtualKey.Delete)
        {
            foreach (var e in _picked.ToList()) Els.Remove(e);
            Pick([]);
        }
        else
        {
            int dx = sender.Key == VirtualKey.Left ? -1 : sender.Key == VirtualKey.Right ? 1 : 0;
            int dy = sender.Key == VirtualKey.Up ? -1 : sender.Key == VirtualKey.Down ? 1 : 0;
            foreach (var e in _picked) { e.X += dx; e.Y += dy; }
        }
        args.Handled = true;
        Edited();
    }

    // ---------- draft, apply, revert ----------

    /// <summary>Call after any change to the draft.</summary>
    void Edited() { Render(); UpdateDirty(); }

    // ---------- undo / redo ----------

    /// <summary>Closes the current burst of changes into one undo step (no-op if nothing changed).</summary>
    internal void Record()
    {
        _settle.Stop();
        string now = _draft.ToJson();
        if (now == _history) return;
        _undo.Add(_history);
        if (_undo.Count > 100) _undo.RemoveAt(0);
        _redo.Clear();
        _history = now;
        SyncHistory();
    }

    void SyncHistory() { UndoButton.IsEnabled = _undo.Count > 0; RedoButton.IsEnabled = _redo.Count > 0; }

    internal void Undo()
    {
        Record(); // anything not yet recorded becomes its own step first, so it can be undone too
        if (_undo.Count == 0) return;
        _redo.Add(_history);
        string to = _undo[^1]; _undo.RemoveAt(_undo.Count - 1);
        Restore(to);
    }

    internal void Redo()
    {
        Record();
        if (_redo.Count == 0) return;
        _undo.Add(_history);
        string to = _redo[^1]; _redo.RemoveAt(_redo.Count - 1);
        Restore(to);
    }

    void Restore(string json)
    {
        bool wasPortrait = _draft.Portrait;
        _draft = System.Text.Json.JsonSerializer.Deserialize<Config>(json, Config.Json)!.Normalize();
        _history = _draft.ToJson();
        _picked.Clear(); _dragMode = 0;
        if (wasPortrait != _draft.Portrait) Reconfigure(); else { LoadBackground(); Render(); }
        BuildProps(); UpdateChrome(); UpdateDirty(); SyncHistory();
        _settle.Stop();
    }

    void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    void History_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (FocusManager.GetFocusedElement(Root.XamlRoot) is TextBox or NumberBox) return; // text fields keep their own Ctrl+Z
        if (sender.Key == VirtualKey.Y || sender.Modifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) Redo(); else Undo();
        args.Handled = true;
    }

    bool IsDirty => _draft.ToJson() != _applied.ToJson();

    void UpdateDirty()
    {
        bool dirty = IsDirty;
        var (brush, text) =
            _mismatch ? ("SystemFillColorCriticalBrush", "引擎沒有採用這份設定，螢幕不會改變。按「重新啟動引擎」修復。")
            : dirty ? ("SystemFillColorCautionBrush", "有變更尚未套用到螢幕")
            : _expectHash != 0 ? ("SystemFillColorCautionBrush", "正在套用…")
            : ("SystemFillColorSuccessBrush", "螢幕已是最新設定");
        DirtyDot.Fill = (Brush)Application.Current.Resources[brush];
        DirtyText.Text = text;
        RestartButton.Visibility = _mismatch ? Visibility.Visible : Visibility.Collapsed;
        ApplyButton.IsEnabled = RevertButton.IsEnabled = dirty;
        _settle.Stop(); _settle.Start(); // every change path ends here: once things go quiet, it becomes an undo step
    }

    void Apply_Click(object sender, RoutedEventArgs e) => _ = ApplyAsync();

    /// <summary>The frame cache a config expects for an item: its orientation and its placement.</summary>
    static string Cache(Config c, string name) => Media.PathFor(name, c.Portrait, c.Fits.GetValueOrDefault(name));

    /// <summary>Converts whatever the draft needs that is not cached yet (a moved or zoomed background), then applies.</summary>
    internal async Task<bool> ApplyAsync()
    {
        var missing = _draft.Playlist.Where(n => !File.Exists(Cache(_draft, n)) && _draft.Sources.TryGetValue(n, out string? s) && File.Exists(s)).ToList();
        if (missing.Count > 0)
        {
            ApplyButton.IsEnabled = RevertButton.IsEnabled = false;
            DirtyText.Text = "正在依新的背景位置轉檔…";
            foreach (string name in missing)
                if (!await Convert(_draft.Sources[name], name))
                {
                    UpdateDirty();
                    DirtyText.Text = "背景轉檔失敗，尚未套用：" + _mediaInfo.Message;
                    return false;
                }
        }
        return Apply();
    }
    void Revert_Click(object sender, RoutedEventArgs e) => Revert();

    internal bool Apply()
    {
        try { _draft.Normalize().Save(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { DirtyText.Text = "無法寫入設定檔：" + e.Message; return false; }
        try { using var wake = EventWaitHandle.OpenExisting(Config.WakeEvent); wake.Set(); }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { /* engine not running, or elevated: it polls the file */ }
        Expect(_draft.LoadedHash, 30);
        var before = _applied;
        _applied = _draft.Clone();
        CleanupMedia(before);
        UpdateDirty();
        return true;
    }

    internal void Revert()
    {
        _draft = _applied.Clone();
        _picked.Clear();
        Reconfigure();
        BuildProps();
        UpdateDirty();
        Record(); // discarding is itself undoable
    }

    /// <summary>
    /// Removing media, or moving a background, only edits the draft; the cache files that are no longer needed go
    /// once that is applied. Only files belonging to media this editor knew about (before or after the apply) are
    /// candidates: a cache it has never heard of is never deleted, whoever put it there.
    /// </summary>
    void CleanupMedia(Config before)
    {
        try
        {
            var keep = _applied.Playlist.SelectMany(n => new[] { Media.PathFor(n, false, _applied.Fits.GetValueOrDefault(n)), Media.PathFor(n, true, _applied.Fits.GetValueOrDefault(n)) })
                .ToHashSet(StringComparer.OrdinalIgnoreCase); // caches for older placements fall outside this set
            var known = before.Playlist.Concat(_applied.Playlist).Select(n => Path.GetFileNameWithoutExtension(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            bool Ours(string file)
            {
                string f = Path.GetFileName(file);
                return known.Exists(k => f.StartsWith(k + ".", StringComparison.OrdinalIgnoreCase) || f.StartsWith(k + "~", StringComparison.OrdinalIgnoreCase));
            }
            foreach (string f in Directory.GetFiles(Config.MediaDir, "*.ihl"))
                if (!keep.Contains(f) && Ours(f)) File.Delete(f);
            foreach (string f in Directory.GetFiles(Config.MediaDir, "*.tmp"))
                if (Ours(f)) File.Delete(f); // left by a conversion that was cut short
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* still in use; retried at the next apply */ }
    }

    /// <summary>
    /// If a background the panel is supposed to be showing has lost its cache file (deleted by hand, by a cleaner,
    /// by anything), rebuild it from the original and tell the engine, instead of leaving the panel black.
    /// </summary>
    async Task RepairCaches()
    {
        var missing = _applied.Playlist.Where(n => !File.Exists(Cache(_applied, n)) && _applied.Sources.TryGetValue(n, out string? s) && File.Exists(s)).ToList();
        if (missing.Count == 0) return;
        DirtyText.Text = "正在重建遺失的背景…";
        bool any = false;
        foreach (string name in missing)
            if (_draft.Playlist.Contains(name) && Cache(_draft, name) == Cache(_applied, name)) // still the same thing the panel wants
                any |= await Convert(_applied.Sources[name], name);
        if (any)
        {
            try { using var wake = EventWaitHandle.OpenExisting(Config.WakeEvent); wake.Set(); }
            catch (Exception e) when (e is WaitHandleCannotBeOpenedException or UnauthorizedAccessException) { }
            LoadBackground(); Render();
            if (_picked.Count == 0) BuildProps();
        }
        UpdateDirty();
    }

    async void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing || !IsDirty) return;
        args.Cancel = true; // unapplied work: ask before throwing it away
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot, Title = "有變更尚未套用", Content = "要把目前的版面套用到螢幕嗎？",
            PrimaryButtonText = "套用並關閉", SecondaryButtonText = "不套用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None || (result == ContentDialogResult.Primary && !await ApplyAsync())) return;
        _closing = true;
        Close();
    }

    // ---------- engine status and live view ----------

    /// <summary>Start waiting for the engine to report it runs the config with this fingerprint.</summary>
    void Expect(int hash, int ticks) { _expectHash = hash; _verifyDeadline = _ticks + ticks; _mismatch = false; }

    /// <summary>Compares what the engine says it is running with what we last saved. Null = engine not reachable.</summary>
    void Verify(int? engineHash)
    {
        if (_expectHash == 0) return;
        if (engineHash == _expectHash) { _expectHash = 0; _mismatch = false; UpdateDirty(); }
        else if (!_mismatch && _ticks > _verifyDeadline) { _mismatch = true; UpdateDirty(); } // keep watching: a slow engine still clears it
    }

    void Restart_Click(object sender, RoutedEventArgs e)
    {
        // An engine started from here sees exactly the files this window sees, whatever the old one was looking at.
        foreach (var p in Process.GetProcessesByName("InfoHubLite"))
            using (p)
                try { p.Kill(); p.WaitForExit(3000); }
                catch (Exception x) when (x is System.ComponentModel.Win32Exception or InvalidOperationException) { /* elevated engine: restart it from its tray icon */ }
        _view?.Dispose(); _view = null; _map?.Dispose(); _map = null; _serial = -1;
        if (File.Exists(EngineExe)) Process.Start(EngineExe).Dispose();
        Expect(_applied.LoadedHash, 40);
        UpdateDirty();
    }

    void Poll()
    {
        if (_ticks++ % 10 == 0 && Process.GetProcessesByName("InfoHubLite").Length == 0)
        {
            _view?.Dispose(); _view = null; _map?.Dispose(); _map = null; _serial = -1;
            Status("引擎沒有在執行", "SystemFillColorCriticalBrush");
            Verify(null);
            return;
        }
        try
        {
            _map ??= MemoryMappedFile.OpenExisting(Config.PreviewMap, MemoryMappedFileRights.Read);
            _view ??= _map.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch (FileNotFoundException) { Verify(null); return; }
        catch (UnauthorizedAccessException)
        {
            // ponytail: no live view (and no apply confirmation) across the elevation boundary; open this window from the elevated tray icon instead
            Status("引擎以系統管理員執行中", "SystemFillColorCautionBrush");
            if (_expectHash != 0) { _expectHash = 0; UpdateDirty(); }
            return;
        }
        Verify(_view.ReadInt32(16));
        int w = _view.ReadInt32(0), h = _view.ReadInt32(4), serial = _view.ReadInt32(12);
        if (_view.ReadInt32(8) != 0) Status("已連線到 LCD", "SystemFillColorSuccessBrush");
        else Status("找不到 LCD（原版 InfoHub 是否還開著？）", "SystemFillColorCriticalBrush");
        if (serial == _serial || w * h != Lcd.Pixels) return;
        _serial = serial;
        _view.ReadArray(Config.PreviewHeader, _livePx, 0, Lcd.Pixels);
        ToBgra(_livePx, w, h, 1, _liveBgra);
        if (_liveBmp == null || _liveBmp.PixelWidth != w) { _liveBmp = new WriteableBitmap(w, h); LiveImage.Source = _liveBmp; }
        using (var s = _liveBmp.PixelBuffer.AsStream()) s.Write(_liveBgra);
        _liveBmp.Invalidate();
    }

    void Status(string text, string brushKey)
    {
        if (StatusText.Text == text) return;
        StatusText.Text = text;
        StatusDot.Fill = (Brush)Application.Current.Resources[brushKey];
    }

    // ---------- review aids ----------

    /// <summary>Renders the window content to a PNG (the system-drawn caption buttons and Mica are not included).</summary>
    public async Task SaveScreenshot(string path)
    {
        Root.Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        await Task.Delay(300);
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(Root);
        byte[] pixels = (await rtb.GetPixelsAsync()).ToArray();
        using var file = File.Open(path, FileMode.Create);
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels);
        await enc.FlushAsync();
    }

    /// <summary>
    /// Exercises the editor's logic end to end (template, select, drag, snap, resize, apply, revert) and reports
    /// one line per check. Puts the original config back afterwards.
    /// </summary>
    public string SelfTest()
    {
        var log = new List<string>();
        int failed = 0;
        void Check(bool ok, string what) { if (!ok) failed++; log.Add((ok ? "PASS " : "FAIL ") + what); }
        var original = _applied.Clone();

        _draft.Rotation = 0; Reconfigure();
        ApplyElements(Templates.Build("雙環儀表", false));
        Check(Els.Count == 4 && IsDirty, "applying a template replaces the layout and marks it unapplied");

        var ring = Els[0];
        int x0 = ring.X, y0 = ring.Y;
        Press(ring.X + ring.W / 2.0, ring.Y + ring.H / 2.0);
        Check(_sel == 0, "clicking an element selects it");
        Move(_startX + 10, _startY + 30); Release();
        Check(ring.X == x0 + 10 && ring.Y == y0 + 30, "dragging moves it by the pointer distance");

        Press(ring.X + ring.W / 2.0, ring.Y + ring.H / 2.0);
        Move(_startX + (160 - (ring.X + ring.W / 2.0)) + 3, _startY); Release();
        Check(ring.X + ring.W / 2 == 160, "dropping near the centre line snaps onto it");

        int w0 = ring.W, h0 = ring.H;
        Press(ring.X + ring.W, ring.Y + ring.H);
        Move(_startX + 20, _startY + 12); Release();
        Check(ring.W == w0 + 20 && ring.H == h0 + 12 && _sel == 0, "dragging a corner handle resizes it");

        Press(2, 2); Release();
        Check(_sel == -1 && _picked.Count == 0, "clicking empty canvas clears the selection");

        // rubber band over the left half, then move what it caught as one group
        int expected = Els.Count(e => e.X < 159 && e.X + e.W > 2 && e.Y < 238 && e.Y + e.H > 2);
        Press(2, 2); Move(159, 238); Release();
        Check(expected >= 2 && _picked.Count == expected, $"dragging a box over empty canvas selects everything it touches ({_picked.Count} of {Els.Count})");
        var ga = _picked[0]; var gb = _picked[1];
        int ax = ga.X, ay = ga.Y, bx = gb.X, by = gb.Y;
        Press(ga.X + ga.W / 2.0, ga.Y + ga.H / 2.0); Move(_startX + 17, _startY - 9); Release();
        Check(ga.X != ax && ga.X - ax == gb.X - bx && ga.Y - ay == gb.Y - by && _picked.Count == expected, "dragging one of several selected elements moves them all by the same amount");
        var outsider = Els.First(e => !_picked.Contains(e));
        Press(outsider.X + outsider.W / 2.0, outsider.Y + outsider.H / 2.0, add: true); Release();
        Check(_picked.Count == expected + 1, "Ctrl/Shift-click adds an element to the selection");
        Press(outsider.X + outsider.W / 2.0, outsider.Y + outsider.H / 2.0, add: true); Release();
        Check(_picked.Count == expected, "Ctrl/Shift-click on a selected element removes it again");
        Press(2, 2); Release();

        // undo / redo
        Record();
        int steps = _undo.Count, ux = Els[0].X;
        Press(Els[0].X + 3, Els[0].Y + Els[0].H / 2.0); Move(_startX + 23, _startY); Release(); // near its left edge: by now another ring overlaps its middle
        Check(Els[0].X != ux && _undo.Count == steps + 1, "a drag is recorded as one undo step");
        Undo();
        Check(Els[0].X == ux && _picked.Count == 0, "undo puts the element back");
        Redo();
        Check(Els[0].X != ux, "redo moves it again");
        Undo();
        Check(Els[0].X == ux && _redo.Count == 1, "undo again; the step is still available to redo");

        // group resize: select everything, pull the bottom-right corner of the group box inwards
        Select(-2);
        int bl = Els.Min(e => e.X), bt = Els.Min(e => e.Y), br = Els.Max(e => e.X + e.W), bb = Els.Max(e => e.Y + e.H);
        int w0g = Els[0].W, h0g = Els[0].H;
        Press(br, bb); Move(br - (br - bl) / 2.0, bb - (bb - bt) / 2.0); Release();
        int nl = Els.Min(e => e.X), nt = Els.Min(e => e.Y), nr = Els.Max(e => e.X + e.W), nb = Els.Max(e => e.Y + e.H);
        Check(Math.Abs(nl - bl) <= 1 && Math.Abs(nt - bt) <= 1 && Math.Abs((nr - nl) - (br - bl) / 2.0) <= 3 && Math.Abs(Els[0].W - w0g / 2.0) <= 1 && Math.Abs(Els[0].H - h0g / 2.0) <= 1,
            "dragging a corner of the group box scales every element, anchored at the opposite corner");
        Undo();
        Check(Els[0].W == w0g && Els[0].H == h0g, "undo restores the group's size");
        Press(2, 2); Release();

        // A cache file this editor has never heard of (another install's, the user's own copy) must survive an apply.
        Directory.CreateDirectory(Config.MediaDir);
        string stranger = Path.Combine(Config.MediaDir, "not-ours.ihl");
        File.WriteAllText(stranger, "x");
        string before = File.ReadAllText(Config.FilePath);
        Check(Apply() && !IsDirty && File.ReadAllText(Config.FilePath) != before && Config.Load()!.Elements!.Count == 4, "apply writes the layout to config.json");
        Check(File.Exists(stranger), "applying never deletes a media cache the editor does not know about");
        Check(Config.Isolated, "the self-test runs in its own data folder, not the real one");

        Els.RemoveAt(0);
        Check(IsDirty, "an edit after applying is unapplied again");
        Revert();
        Check(!IsDirty && Els.Count == 4, "revert restores the applied layout");

        // background placement: a 640x240 picture behind a 320x240 panel has 320 px of room sideways and none vertically
        _draft.Playlist = ["selftest.ihl"]; _draft.Current = 0; _draft.Fits.Clear();
        _bgSource?.Dispose(); _bgSource = new System.Drawing.Bitmap(640, 240); _bgSourceName = "selftest.ihl";
        _bgMode = true;
        Press(4, 4); Move(_startX - 50, _startY + 30); Release();
        _bgMode = false;
        var fit = _draft.Fits.GetValueOrDefault("selftest.ihl");
        Check(fit != null && Math.Abs(fit.X - 0.3125) < 0.01 && fit.Y == 0, "with move-background on, dragging slides the background, only where the picture has room");
        SetFit("selftest.ihl", 2, fit?.X ?? 0, 0);
        var crop = Media.CropFor(640, 240, 320, 240, _draft.Fits.GetValueOrDefault("selftest.ihl"));
        Check(Math.Abs(crop.Width - 160) < 0.5 && Math.Abs(crop.Height - 120) < 0.5 && crop.X >= 0 && crop.Right <= 640, "zoom 200% shows half the width and height, still inside the picture");
        SetFit("selftest.ihl", 9, 5, -5);
        var max = _draft.Fits["selftest.ihl"];
        Check(max.Zoom == 4 && max.X == 1 && max.Y == -1, "placement is clamped to sane limits");
        SetFit("selftest.ihl", 1, 0, 0);
        Check(!_draft.Fits.ContainsKey("selftest.ihl"), "reset returns to the default placement");

        // a fully transparent picture after an opaque red one: nothing of the red may show through
        _bgSource.Dispose(); _bgSource = new System.Drawing.Bitmap(640, 240);
        using (var g = System.Drawing.Graphics.FromImage(_bgSource)) g.Clear(System.Drawing.Color.Red);
        LoadBackground();
        bool wasRed = _bg[0] == 0xF800;
        _bgSource.Dispose(); _bgSource = new System.Drawing.Bitmap(640, 240); // all pixels transparent
        LoadBackground();
        Check(wasRed && Array.TrueForAll(_bg, px => px == 0), "a transparent background does not show the previous background through it");

        _draft = original; Reconfigure();
        Check(Apply() && Config.Load()!.ToJson() == original.ToJson(), "original config restored");
        Select(-1);
        log.Add($"failed: {failed}");
        return string.Join(Environment.NewLine, log);
    }

    [DllImport("user32")] static extern uint GetDpiForWindow(nint hwnd);
}
