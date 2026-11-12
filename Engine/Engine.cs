using System.IO.MemoryMappedFiles;

namespace InfoHubLite;

/// <summary>
/// The resident loop. Every buffer is allocated once up front; a steady-state tick allocates nothing bigger than a
/// few short strings, so the display keeps running when something else has eaten all the system's memory.
/// Any failure inside a tick leaves the last good packet in place and it is simply sent again.
/// </summary>
sealed unsafe class Engine : IDisposable
{
    readonly Lcd _lcd = new();
    readonly Renderer _r = new();
    readonly Sensors _sensors = new();
    readonly byte[] _packet = GC.AllocateArray<byte>(Lcd.PacketLen, pinned: true);
    readonly ushort[] _bg = new ushort[Lcd.Pixels];
    readonly object _io = new();
    readonly EventWaitHandle _wake = new(false, EventResetMode.AutoReset, Config.WakeEvent);
    // What the panel is showing, for the settings UI: the composed canvas in shared memory. One 150 KB copy per frame.
    readonly MemoryMappedFile _pvMap = MemoryMappedFile.CreateOrOpen(Config.PreviewMap, Config.PreviewHeader + Lcd.Pixels * 2);
    readonly MemoryMappedViewAccessor _pvView;
    readonly byte* _pv;
    readonly Random _rng = new();

    Config _cfg = new Config().Normalize();
    List<Element> _els = [];
    string[] _texts = [];
    float[] _fracs = [];
    Media? _media;
    int _item, _frame, _serial;
    long _frameDue, _itemDue, _lastSend = -100000, _lastSecond = -100000, _lastStat, _nextOpen;
    string _sensorKey = "\0", _mediaKey = "\0";
    DateTime _cfgStamp;
    volatile bool _quit, _suspended;
    bool _reload = true, _dirty = true;

    /// <summary>Compose but never touch the device ("render", "template", self-check).</summary>
    public bool Offline;
    /// <summary>When set, used instead of config.json.</summary>
    public Config? Override;

    public Engine()
    {
        _pvView = _pvMap.CreateViewAccessor();
        byte* p = null;
        _pvView.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
        _pv = p;
    }

    public Config Config => _cfg;
    public ReadOnlySpan<byte> Packet => _packet;
    public void Wake() => _wake.Set();
    public void Quit() { _quit = true; _wake.Set(); }

    public void Run()
    {
        while (!_quit)
        {
            int wait;
            try { wait = Tick(Environment.TickCount64); }
            catch (Exception) { wait = 500; } // out of memory, GDI+ hiccup, media file vanished: keep showing the last frame
            if (_wake.WaitOne(wait)) _reload = true;
        }
    }

    /// <summary>Does whatever is due and returns how long the loop may sleep (ms).</summary>
    public int Tick(long now)
    {
        if (_reload) { _reload = false; Reload(now); }
        if (_suspended) return 1000;

        if (!Offline && !_lcd.IsOpen)
        {
            // Unplugged, or the official InfoHub still owns the device: retry quietly, render nothing meanwhile.
            lock (_io) if (now >= _nextOpen && !_lcd.Open()) _nextOpen = now + 2000;
            if (!_lcd.IsOpen) return 1000;
            _dirty = true;
        }

        bool animated = _media is { Count: > 1 }, cycling = _cfg.Cycle != "one" && _cfg.Playlist.Count > 1;
        if (_cfg.ScreenOn)
        {
            if (animated && now >= _frameDue)
            {
                if (_frame + 1 >= _media!.Count && cycling) OpenItem(Next(), now);
                else
                {
                    _frame = (_frame + 1) % _media.Count;
                    _media.Read(_frame, _bg);
                    _frameDue = Math.Max(_frameDue, now - 200) + _media.Delay(_frame); // catch up after a stall, but never burst
                }
                _dirty = true;
            }
            else if (!animated && cycling && now >= _itemDue) { OpenItem(Next(), now); _dirty = true; }
            animated = _media is { Count: > 1 };

            if (now - _lastSecond >= 1000) { _lastSecond = now; if (UpdateTexts()) _dirty = true; }
        }
        // The wake event cannot cross an elevation boundary (admin engine, normal UI), so also notice the file changing.
        if (Override == null && now - _lastStat >= 1000)
        {
            _lastStat = now;
            if (File.GetLastWriteTimeUtc(Config.FilePath) != _cfgStamp) { _reload = true; return 1; }
        }

        if (_dirty) Compose();
        if (_dirty || (_cfg.KeepAliveMs > 0 && now - _lastSend >= _cfg.KeepAliveMs)) { Send(now); _dirty = false; }

        long next = _lastSecond + 1000;
        if (animated) next = Math.Min(next, _frameDue); else if (cycling) next = Math.Min(next, _itemDue);
        if (_cfg.KeepAliveMs > 0) next = Math.Min(next, _lastSend + _cfg.KeepAliveMs);
        if (!_cfg.ScreenOn) next = now + 1000;
        return (int)Math.Clamp(next - now, 1, 1000);
    }

    void Reload(long now)
    {
        _cfg = Override ?? Config.Load() ?? _cfg; // unreadable file: keep running on the previous settings
        _cfgStamp = File.GetLastWriteTimeUtc(Config.FilePath);
        _els = _cfg.Elements ?? [];
        if (_texts.Length != _els.Count) { _texts = new string[_els.Count]; _fracs = new float[_els.Count]; }
        Array.Fill(_texts, "");
        _r.Configure(_cfg.Portrait);

        string[] ids = _els.Where(e => e.UsesSensor && e.Sensor != "").Select(e => e.Sensor).Distinct().Order().ToArray();
        string key = string.Join('|', ids);
        if (key != _sensorKey) { _sensorKey = key; _sensors.Select(ids); } // re-opening the sensor library is the slow part

        // Only restart playback when the media selection itself changed, not on every brightness or layout tweak.
        int want = _cfg.Playlist.Count > 0 ? Math.Clamp(_cfg.Current, 0, _cfg.Playlist.Count - 1) : 0;
        string file = _cfg.Playlist.Count > 0 ? Media.PathFor(_cfg.Playlist[want], _cfg.Portrait, _cfg.Fits.GetValueOrDefault(_cfg.Playlist[want])) : "";
        string mediaKey = $"{string.Join('|', _cfg.Playlist)}#{want}#{_r.W}#{_cfg.Background}#{(file != "" ? File.GetLastWriteTimeUtc(file).Ticks : 0)}";
        if (mediaKey != _mediaKey) { _mediaKey = mediaKey; OpenItem(want, now); }
        _lastSecond = now; UpdateTexts();
        _dirty = true;
    }

    void OpenItem(int index, long now)
    {
        _media?.Dispose(); _media = null; _item = index; _frame = 0;
        if (index < _cfg.Playlist.Count)
        {
            var m = Media.Open(Media.PathFor(_cfg.Playlist[index], _cfg.Portrait, _cfg.Fits.GetValueOrDefault(_cfg.Playlist[index])));
            if (m != null && m.W == _r.W) _media = m; else m?.Dispose(); // missing or wrong-shaped cache: fall back to the solid colour
        }
        if (_media != null) _media.Read(0, _bg);
        else Array.Fill(_bg, Renderer.To565(Renderer.ParseColor(_cfg.Background, System.Drawing.Color.Black)));
        _frameDue = now + (_media?.Delay(0) ?? 0);
        _itemDue = now + _cfg.ImageSeconds * 1000L;
    }

    int Next()
    {
        int n = _cfg.Playlist.Count;
        if (_cfg.Cycle != "random") return (_item + 1) % n;
        int r = _rng.Next(n - 1);
        return r >= _item ? r + 1 : r; // any item except the current one
    }

    /// <summary>Once a second: refresh readings and each element's text. True when anything visible changed.</summary>
    bool UpdateTexts()
    {
        if (_sensorKey.Length > 0) _sensors.Update();
        var now = DateTime.Now;
        bool changed = false;
        for (int i = 0; i < _els.Count; i++)
        {
            var e = _els[i];
            var (value, kind) = e.UsesSensor && e.Sensor != "" ? _sensors.Read(e.Sensor) : (null, Kind.Other);
            string text = Fmt.Text(e, value, kind, _cfg.Fahrenheit, now);
            float frac = Fmt.Fraction(value, kind);
            if (text != _texts[i] || !frac.Equals(_fracs[i])) { _texts[i] = text; _fracs[i] = frac; changed = true; }
        }
        return changed;
    }

    void Compose()
    {
        _bg.AsSpan().CopyTo(_r.Canvas);
        if (_cfg.Dim > 0 && _els.Count > 0) _r.Dim(_cfg.Dim);
        _r.Draw(_els, _texts, _fracs, _cfg.Shadow, DateTime.Now);
        lock (_io)
        {
            Renderer.Blit(_r.Canvas, _cfg.Rotation, _packet);
            // Screen off = brightness byte 0, the same way InfoHub does it.
            Lcd.WriteHeader(_packet, _cfg.ScreenOn ? _cfg.Brightness : 0, !_cfg.Portrait);
        }
        int* head = (int*)_pv;
        head[0] = _r.W; head[1] = _r.H; head[2] = _lcd.IsOpen ? 1 : 0; head[4] = _cfg.LoadedHash;
        _r.Canvas.AsSpan().CopyTo(new Span<ushort>(_pv + Config.PreviewHeader, Lcd.Pixels));
        head[3] = ++_serial;
    }

    void Send(long now)
    {
        _lastSend = now;
        if (Offline) return;
        lock (_io)
            if (!_lcd.Write(_packet)) _nextOpen = now + 500; // Write closed the handle; Tick reopens it shortly
    }

    /// <summary>Called on the window thread from WM_POWERBROADCAST, so the blank frame lands before the PC sleeps.</summary>
    public void OnPower(bool suspending)
    {
        if (suspending)
        {
            if (!_cfg.SleepOff) return;
            lock (_io) { _suspended = true; _packet[8] = 0; if (_lcd.IsOpen) _lcd.Write(_packet); }
        }
        else
        {
            lock (_io) { _suspended = false; _lcd.Close(); _nextOpen = 0; } // the USB handle is stale after resume
            _wake.Set(); // reload + redraw restores the brightness byte
        }
    }

    /// <summary>One composed frame without touching the device.</summary>
    public void RenderOnce(string? pngPath)
    {
        Offline = true;
        Tick(Environment.TickCount64);
        Thread.Sleep(1100); // usage sensors need two samples a second apart
        UpdateTexts();
        _dirty = true;
        Tick(Environment.TickCount64);
        if (pngPath != null) _r.SavePng(pngPath);
    }

    public void Dispose()
    {
        _media?.Dispose(); _lcd.Dispose(); _r.Dispose(); _sensors.Dispose(); _wake.Dispose();
        _pvView.SafeMemoryMappedViewHandle.ReleasePointer(); _pvView.Dispose(); _pvMap.Dispose();
    }
}
