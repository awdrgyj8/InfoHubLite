using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace InfoHubLite;

/// <summary>Side panels of the editor: template gallery (left) and properties (right).</summary>
public sealed partial class MainWindow
{
    sealed record SensorItem(string Id, string Text) { public override string ToString() => Text; }
    sealed class UserTemplate { public int W { get; set; } public int H { get; set; } public List<Element> Elements { get; set; } = []; }

    static readonly string[] Palette = ["#FFFFFF", "#AAB2C0", "#4FD1C5", "#4CC2FF", "#7F77DD", "#F0997B", "#EF9F27", "#E24B4A", "#97C459", "#000000"];

    /// <summary>Stand-in readings, so templates and thumbnails look alive before (or without) real sensor data.</summary>
    static readonly Dictionary<string, (float?, Kind)> Samples = new()
    {
        ["cpu.load"] = (23, Kind.Load), ["ram.load"] = (45, Kind.Load), ["gpu.load"] = (41, Kind.Load),
        ["cpu.temp"] = (52, Kind.Temperature), ["gpu.temp"] = (61, Kind.Temperature),
    };

    Dictionary<string, (float?, Kind)> _values = new(Samples);
    List<SensorItem> _sensorItems =
    [
        new("cpu.load", "CPU 使用率"), new("ram.load", "記憶體使用率"), new("gpu.load", "GPU 使用率"),
        new("cpu.temp", "CPU 溫度"), new("gpu.temp", "GPU 溫度"),
    ];
    readonly Renderer _thumbR = new();
    InfoBar _mediaInfo = new() { IsClosable = true, IsOpen = false };
    ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed };

    // ---------- building blocks ----------

    Style S(string key) => (Style)Root.Resources[key];
    TextBlock Section(string text) => new() { Text = text, Style = S("Section") };

    static void Label(FrameworkElement control, string name)
    {
        // Screen readers (and UI automation) need each control to carry the name of its row.
        foreach (var c in control is Panel p ? p.Children.OfType<Control>() : control is Control one ? [one] : [])
            AutomationProperties.SetName(c, name);
    }

    /// <summary>Row: title (+ optional description) left, control right.</summary>
    Border Card(string title, string? description, FrameworkElement control)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (description != null) text.Children.Add(new TextBlock { Text = description, Style = S("Secondary"), TextWrapping = TextWrapping.Wrap });
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center; control.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        Label(control, title);
        return new Border { Style = S("Card"), Child = grid };
    }

    /// <summary>Row with the control underneath the title, for wide controls.</summary>
    Border VCard(string title, FrameworkElement control)
    {
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        stack.Children.Add(new TextBlock { Text = title });
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        stack.Children.Add(control);
        Label(control, title);
        return new Border { Style = S("Card"), Child = stack };
    }

    static FrameworkElement Switch(bool on, Action<bool> changed)
    {
        var state = new TextBlock { Text = on ? "開啟" : "關閉", VerticalAlignment = VerticalAlignment.Center };
        var t = new ToggleSwitch { IsOn = on, MinWidth = 0, OnContent = null, OffContent = null, Margin = new Thickness(0, 0, -8, 0) };
        t.Toggled += (_, _) => { state.Text = t.IsOn ? "開啟" : "關閉"; changed(t.IsOn); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(state); row.Children.Add(t);
        return row;
    }

    static FrameworkElement SliderWithValue(int min, int max, int value, string unit, Action<int> changed)
    {
        var label = new TextBlock { Text = value + unit, Width = 40, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var s = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), Width = 130, VerticalAlignment = VerticalAlignment.Center, IsThumbToolTipEnabled = false };
        s.ValueChanged += (_, e) => { label.Text = (int)e.NewValue + unit; changed((int)e.NewValue); };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(s); row.Children.Add(label);
        return row;
    }

    static ComboBox Choice(string[] items, int index, double width, Action<int> changed)
    {
        var c = new ComboBox { ItemsSource = items, SelectedIndex = index, Width = width };
        c.SelectionChanged += (_, _) => { if (c.SelectedIndex >= 0) changed(c.SelectedIndex); };
        return c;
    }

    static Color Parse(string hex)
    {
        var c = Renderer.ParseColor(hex, System.Drawing.Color.White);
        return Color.FromArgb(255, c.R, c.G, c.B);
    }

    static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>A custom-colour button: swatch that opens a colour picker.</summary>
    Button ColorButton(string hex, Action<string> changed, bool wide = true)
    {
        var brush = new SolidColorBrush(Parse(hex));
        var swatch = new Border { Width = wide ? 44 : 18, Height = wide ? 20 : 18, CornerRadius = new CornerRadius(wide ? 3 : 9), Background = brush, BorderThickness = new Thickness(1), BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"] };
        var picker = new ColorPicker { Color = brush.Color, IsAlphaEnabled = false, IsMoreButtonVisible = false, IsColorChannelTextInputVisible = false, IsHexInputVisible = true, ColorSpectrumShape = ColorSpectrumShape.Box };
        picker.ColorChanged += (_, e) => { brush.Color = e.NewColor; changed(Hex(e.NewColor)); };
        return new Button { Content = swatch, Padding = new Thickness(wide ? 6 : 4), Flyout = new Flyout { Content = picker } };
    }

    /// <summary>Quick palette plus a "pick any colour" button.</summary>
    FrameworkElement ColorRow(string current, Action<string> changed)
    {
        var row = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 28, ItemHeight = 30, MaximumRowsOrColumns = 11 };
        var rings = new List<Border>();
        foreach (string hex in Palette)
        {
            var dot = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(Parse(hex)),
                BorderThickness = new Thickness(2), Tag = hex,
                BorderBrush = (Brush)Application.Current.Resources[hex.Equals(current, StringComparison.OrdinalIgnoreCase) ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"],
            };
            rings.Add(dot);
            var b = new Button { Content = dot, Padding = new Thickness(2), Background = new SolidColorBrush(Colors.Transparent), BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(b, "顏色 " + hex);
            b.Click += (_, _) =>
            {
                foreach (var r in rings) r.BorderBrush = (Brush)Application.Current.Resources[r == dot ? "AccentFillColorDefaultBrush" : "ControlStrokeColorDefaultBrush"];
                changed(hex);
            };
            row.Children.Add(b);
        }
        var custom = ColorButton(current, changed, wide: false);
        ToolTipService.SetToolTip(custom, "自訂顏色");
        AutomationProperties.SetName(custom, "自訂顏色");
        row.Children.Add(custom);
        return row;
    }

    static Button IconButton(string glyph, string text, Action click)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        content.Children.Add(new TextBlock { Text = text });
        var b = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch };
        b.Click += (_, _) => click();
        return b;
    }

    // ---------- add element ----------

    void BuildAddMenu()
    {
        (string label, string glyph, Func<Element> make)[] kinds =
        [
            ("文字", "", () => new Element { Type = "text", Text = "文字", W = 120, H = 30, Color = "#AAB2C0" }),
            ("感測器數值", "", () => new Element { Type = "value", Sensor = "cpu.load", W = 140, H = 46, Bold = true }),
            ("圓環儀表", "", () => new Element { Type = "ring", Sensor = "cpu.load", W = 90, H = 90, Color = "#4FD1C5" }),
            ("半圓儀表", "", () => new Element { Type = "gauge", Sensor = "cpu.load", W = 110, H = 110, Color = "#22D3EE" }),
            ("進度條", "", () => new Element { Type = "bar", Sensor = "cpu.load", W = 200, H = 6, Color = "#4FD1C5" }),
            ("直條", "", () => new Element { Type = "bar", Sensor = "cpu.load", W = 28, H = 120, Color = "#4FD1C5" }),
            ("分隔線", "", () => new Element { Type = "bar", Sensor = "", W = 200, H = 1, Color = "#3A4150" }),
            ("數位時鐘", "", () => new Element { Type = "clock", W = 220, H = 72, Bold = true }),
            ("日期", "", () => new Element { Type = "date", W = 200, H = 28, Color = "#AAB2C0" }),
            ("指針時鐘", "", () => new Element { Type = "analog", W = 140, H = 140 }),
        ];
        var menu = new MenuFlyout();
        foreach (var (label, glyph, make) in kinds)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) =>
            {
                var e = make();
                e.W = Math.Min(e.W, _r.W); e.H = Math.Min(e.H, _r.H);
                e.X = (_r.W - e.W) / 2; e.Y = (_r.H - e.H) / 2;
                Els.Add(e);
                Select(Els.Count - 1);
                Edited();
            };
            menu.Items.Add(item);
        }
        AddButton.Flyout = menu;
    }

    static string TypeName(Element e) => e.Type switch
    {
        "text" => "文字", "value" => "感測器數值", "ring" => "圓環儀表", "gauge" => "半圓儀表",
        "bar" => e.Sensor == "" ? "分隔線" : e.H > e.W ? "直條" : "進度條",
        "clock" => "數位時鐘", "date" => "日期", "analog" => "指針時鐘", _ => e.Type,
    };

    // ---------- templates (left) ----------

    WriteableBitmap Thumbnail(List<Element> els)
    {
        _thumbR.Configure(_draft.Portrait);
        Array.Clear(_thumbR.Canvas);
        var now = new DateTime(2026, 1, 1, 10, 9, 36);
        var texts = new string[els.Count]; var fracs = new float[els.Count];
        for (int i = 0; i < els.Count; i++)
        {
            var (v, k) = Read(Samples, els[i]);
            texts[i] = Fmt.Text(els[i], v, k, false, now); fracs[i] = Fmt.Fraction(v, k);
        }
        _thumbR.Draw(els, texts, fracs, false, now);
        var bgra = new byte[Lcd.Pixels * 4];
        ToBgra(_thumbR.Canvas, _thumbR.W, _thumbR.H, 1, bgra);
        var bmp = new WriteableBitmap(_thumbR.W, _thumbR.H);
        using (var s = bmp.PixelBuffer.AsStream()) s.Write(bgra);
        return bmp;
    }

    Button TemplateButton(string name, List<Element> els)
    {
        var content = new StackPanel { Spacing = 4 };
        content.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = new Image { Source = Thumbnail(els), Stretch = Stretch.Uniform, MaxHeight = 150 } });
        content.Children.Add(new TextBlock { Text = name, Style = S("Secondary"), HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        var b = new Button { Content = content, Padding = new Thickness(6, 6, 6, 4), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(b, "樣板 " + name);
        b.Click += (_, _) => ApplyElements(els.Select(e => e.Clone()).ToList());
        return b;
    }

    void BuildTemplates()
    {
        TemplateList.Children.Clear();
        foreach (string name in Templates.Names) TemplateList.Children.Add(TemplateButton(name, Templates.Build(name, _draft.Portrait)));

        UserTemplateList.Children.Clear();
        if (!Directory.Exists(Config.TemplateDir)) return;
        foreach (string file in Directory.GetFiles(Config.TemplateDir, "*.json").Order())
        {
            UserTemplate? t;
            try { t = JsonSerializer.Deserialize<UserTemplate>(File.ReadAllText(file), Config.Json); }
            catch (Exception e) when (e is JsonException or IOException) { continue; }
            if (t == null || t.Elements.Count == 0 || t.W <= 0 || t.H <= 0) continue;
            if (t.W != _r.W) Templates.Remap(t.Elements, t.W, t.H, _r.W, _r.H); // saved in the other orientation
            var b = TemplateButton(Path.GetFileNameWithoutExtension(file), t.Elements);
            var delete = new MenuFlyoutItem { Text = "刪除這個樣板", Icon = new FontIcon { Glyph = "" } };
            delete.Click += (_, _) => { try { File.Delete(file); } catch (IOException) { } BuildTemplates(); };
            b.ContextFlyout = new MenuFlyout { Items = { delete } };
            ToolTipService.SetToolTip(b, "按右鍵可刪除");
            UserTemplateList.Children.Add(b);
        }
    }

    internal void ApplyElements(List<Element> els)
    {
        _draft.Elements = els;
        Select(-1);
        Edited();
    }

    async void SaveTemplate_Click(object sender, RoutedEventArgs e)
    {
        var box = new TextBox { PlaceholderText = "樣板名稱", MaxLength = 24 };
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "存成我的樣板", Content = box, PrimaryButtonText = "儲存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string name = box.Text.Trim();
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        if (name == "") name = "我的樣板 " + DateTime.Now.ToString("MMdd-HHmm");
        Directory.CreateDirectory(Config.TemplateDir);
        var t = new UserTemplate { W = _r.W, H = _r.H, Elements = Els.Select(x => x.Clone()).ToList() };
        File.WriteAllText(Path.Combine(Config.TemplateDir, name + ".json"), JsonSerializer.Serialize(t, Config.Json));
        BuildTemplates();
    }

    // ---------- properties (right) ----------

    void BuildProps()
    {
        Props.Children.Clear();
        _picked.RemoveAll(e => !Els.Contains(e)); // elements deleted or replaced since they were selected
        if (_picked.Count > 1) GroupProps(); else if (_picked.Count == 1) ElementProps(_picked[0]); else ScreenProps();
    }

    void ElementProps(Element e)
    {
        var header = new Grid { Margin = new Thickness(2, 0, 0, 4) };
        header.Children.Add(new TextBlock { Text = TypeName(e), Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8) };
        ToolTipService.SetToolTip(remove, "刪除（Delete）");
        AutomationProperties.SetName(remove, "刪除元件");
        remove.Click += (_, _) => { Els.Remove(e); Select(-1); Edited(); };
        header.Children.Add(remove);
        Props.Children.Add(header);

        if (e.UsesSensor && !(e.Type == "bar" && e.Sensor == ""))
        {
            var items = _sensorItems.ToList();
            if (!items.Exists(s => s.Id == e.Sensor)) items.Add(new SensorItem(e.Sensor, e.Sensor + "（這台電腦找不到）"));
            var combo = new ComboBox { ItemsSource = items, SelectedItem = items.Find(s => s.Id == e.Sensor), MaxDropDownHeight = 420 };
            var hint = new TextBlock { Style = S("Secondary"), TextWrapping = TextWrapping.Wrap };
            void Hint() { hint.Visibility = Read(_values, e).value == null ? Visibility.Visible : Visibility.Collapsed; }
            hint.Text = "目前讀不到數值，螢幕上會顯示「--」。溫度、風扇、電壓需要以系統管理員身分執行引擎（系統匣圖示按右鍵）。";
            Hint();
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is SensorItem s) { e.Sensor = s.Id; Hint(); Edited(); } };
            var stack = new StackPanel { Spacing = 6 };
            combo.HorizontalAlignment = HorizontalAlignment.Stretch;
            stack.Children.Add(combo); stack.Children.Add(hint);
            Props.Children.Add(VCard("顯示什麼", stack));
        }

        if (e.Type is "text" or "value")
        {
            var box = new TextBox { Text = e.Text, MaxLength = 24, PlaceholderText = e.Type == "value" ? "例如「CPU 」，可留空" : "" };
            box.TextChanged += (_, _) => { e.Text = box.Text; Edited(); };
            Props.Children.Add(VCard(e.Type == "value" ? "數值前面的文字" : "文字", box));
        }

        Props.Children.Add(VCard("顏色", ColorRow(e.Color, hex => { e.Color = hex; Edited(); })));

        // Size sliders grow the element around its centre, so it stays where you put it.
        void Resize(int w, int h) { e.X += (e.W - w) / 2; e.Y += (e.H - h) / 2; e.W = w; e.H = h; Edited(); }
        if (e.IsText) Props.Children.Add(Card("大小", null, SliderWithValue(8, Math.Min(200, _r.H), e.H, "", v => Resize(e.W, v))));
        else if (e.Type == "bar")
        {
            if (e.H > e.W) // upright
            {
                Props.Children.Add(Card("高度", null, SliderWithValue(4, _r.H, e.H, "", v => Resize(e.W, v))));
                Props.Children.Add(Card("粗細", null, SliderWithValue(1, 60, e.W, "", v => Resize(Math.Min(v, e.H - 1), e.H))));
            }
            else
            {
                Props.Children.Add(Card("長度", null, SliderWithValue(4, _r.W, e.W, "", v => Resize(v, e.H))));
                Props.Children.Add(Card("粗細", null, SliderWithValue(1, 40, e.H, "", v => Resize(e.W, v))));
            }
        }
        else Props.Children.Add(Card("大小", null, SliderWithValue(20, Math.Min(_r.W, _r.H), Math.Min(e.W, e.H), "", v => Resize(v, v))));

        if (e.IsText)
        {
            Props.Children.Add(Card("對齊", null, Choice(["靠左", "置中", "靠右"], Math.Clamp(e.Align, 0, 2), 110, i => { e.Align = i; Edited(); })));
            Props.Children.Add(Card("粗體", null, Switch(e.Bold, v => { e.Bold = v; Edited(); })));
        }
        if (e.Type != "bar") Props.Children.Add(Card("深色底框", "壓在圖片上時比較清楚", Switch(e.Pill, v => { e.Pill = v; Edited(); })));
        if (e.Type is "clock" or "analog") Props.Children.Add(Card("顯示秒數", null, Switch(e.Seconds, v => { e.Seconds = v; Edited(); })));

        var arrange = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        arrange.ColumnDefinitions.Add(new ColumnDefinition()); arrange.ColumnDefinitions.Add(new ColumnDefinition());
        arrange.RowDefinitions.Add(new RowDefinition()); arrange.RowDefinitions.Add(new RowDefinition());
        void Put(Button b, int col, int row) { Grid.SetColumn(b, col); Grid.SetRow(b, row); arrange.Children.Add(b); }
        Put(IconButton("", "水平置中", () => { e.X = (_r.W - e.W) / 2; Edited(); }), 0, 0);
        Put(IconButton("", "垂直置中", () => { e.Y = (_r.H - e.H) / 2; Edited(); }), 1, 0);
        Put(IconButton("", "移到最上層", () => { Els.Remove(e); Els.Add(e); Edited(); }), 0, 1);
        Put(IconButton("", "移到最下層", () => { Els.Remove(e); Els.Insert(0, e); Edited(); }), 1, 1);
        Props.Children.Add(VCard("排列", arrange));
    }

    /// <summary>Several elements selected: everything a single element offers that also makes sense for a set.</summary>
    void GroupProps()
    {
        var group = _picked.ToList();
        var header = new Grid { Margin = new Thickness(2, 0, 0, 4) };
        header.Children.Add(new TextBlock { Text = $"已選取 {group.Count} 個元件", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], VerticalAlignment = VerticalAlignment.Center });
        var remove = new Button { Content = new FontIcon { Glyph = "\uE74D", FontSize = 14 }, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(8) };
        ToolTipService.SetToolTip(remove, "全部刪除（Delete）");
        AutomationProperties.SetName(remove, "刪除選取的元件");
        remove.Click += (_, _) => { foreach (var e in group) Els.Remove(e); Select(-1); Edited(); };
        header.Children.Add(remove);
        Props.Children.Add(header);
        Props.Children.Add(new TextBlock { Text = "拖曳其中任何一個，整組一起移動；拉外框的四個角，整組一起放大縮小。按住 Ctrl 或 Shift 再點，可加入或移除元件。", Style = S("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 6) });

        // Size: scale from how things were when this panel was built, around the middle of the group.
        var baseline = group.Select(e => (e, e.X, e.Y, e.W, e.H)).ToList();
        double midX = (group.Min(e => e.X) + group.Max(e => e.X + e.W)) / 2.0, midY = (group.Min(e => e.Y) + group.Max(e => e.Y + e.H)) / 2.0;
        Props.Children.Add(Card("整組大小", null, SliderWithValue(25, 300, 100, "%", v => { Scale(baseline, v / 100.0, midX, midY); Edited(); })));

        Props.Children.Add(VCard("顏色（全部套用）", ColorRow(group[0].Color, hex => { foreach (var e in group) e.Color = hex; Edited(); })));

        // The same switches a single element has, applied to every member they are meaningful for.
        var texts = group.Where(e => e.IsText).ToList();
        if (texts.Count > 0)
        {
            int align = texts.All(e => e.Align == texts[0].Align) ? Math.Clamp(texts[0].Align, 0, 2) : -1; // -1: mixed, shows nothing picked
            Props.Children.Add(Card("文字對齊", null, Choice(["靠左", "置中", "靠右"], align, 110, i => { foreach (var e in texts) e.Align = i; Edited(); })));
            Props.Children.Add(Card("粗體", null, Switch(texts.All(e => e.Bold), v => { foreach (var e in texts) e.Bold = v; Edited(); })));
        }
        var backed = group.Where(e => e.Type != "bar").ToList();
        if (backed.Count > 0) Props.Children.Add(Card("深色底框", "壓在圖片上時比較清楚", Switch(backed.All(e => e.Pill), v => { foreach (var e in backed) e.Pill = v; Edited(); })));
        var clocks = group.Where(e => e.Type is "clock" or "analog").ToList();
        if (clocks.Count > 0) Props.Children.Add(Card("顯示秒數", null, Switch(clocks.All(e => e.Seconds), v => { foreach (var e in clocks) e.Seconds = v; Edited(); })));

        int Left() => group.Min(e => e.X); int Right() => group.Max(e => e.X + e.W);
        int Top() => group.Min(e => e.Y); int Bottom() => group.Max(e => e.Y + e.H);
        // Positions changed under the size slider's baseline, so rebuild the panel after each of these.
        void Done() { Edited(); BuildProps(); }
        void Shift(int dx, int dy) { foreach (var e in group) { e.X += dx; e.Y += dy; } Done(); }
        var arrange = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        arrange.ColumnDefinitions.Add(new ColumnDefinition()); arrange.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < 5; i++) arrange.RowDefinitions.Add(new RowDefinition());
        void Put(Button b, int col, int row) { Grid.SetColumn(b, col); Grid.SetRow(b, row); arrange.Children.Add(b); }
        Put(IconButton("\uE8E3", "整組水平置中", () => Shift((_r.W - (Right() - Left())) / 2 - Left(), 0)), 0, 0);
        Put(IconButton("\uE8E4", "整組垂直置中", () => Shift(0, (_r.H - (Bottom() - Top())) / 2 - Top())), 1, 0);
        Put(IconButton("\uE8E4", "靠左對齊", () => { int x = Left(); foreach (var e in group) e.X = x; Done(); }), 0, 1);
        Put(IconButton("\uE8E2", "靠右對齊", () => { int x = Right(); foreach (var e in group) e.X = x - e.W; Done(); }), 1, 1);
        Put(IconButton("\uE74A", "靠上對齊", () => { int y = Top(); foreach (var e in group) e.Y = y; Done(); }), 0, 2);
        Put(IconButton("\uE74B", "靠下對齊", () => { int y = Bottom(); foreach (var e in group) e.Y = y - e.H; Done(); }), 1, 2);
        Put(IconButton("\uE8E3", "左右置中對齊", () => { int c = (Left() + Right()) / 2; foreach (var e in group) e.X = c - e.W / 2; Done(); }), 0, 3);
        Put(IconButton("\uE8E4", "上下置中對齊", () => { int c = (Top() + Bottom()) / 2; foreach (var e in group) e.Y = c - e.H / 2; Done(); }), 1, 3);
        // layer order: the group keeps its own internal order
        Put(IconButton("\uE74A", "移到最上層", () => { var o = group.OrderBy(Els.IndexOf).ToList(); foreach (var e in o) { Els.Remove(e); Els.Add(e); } Done(); }), 0, 4);
        Put(IconButton("\uE74B", "移到最下層", () => { var o = group.OrderByDescending(Els.IndexOf).ToList(); foreach (var e in o) { Els.Remove(e); Els.Insert(0, e); } Done(); }), 1, 4);
        Props.Children.Add(VCard("排列", arrange));
    }

    void ScreenProps()
    {
        Props.Children.Add(new TextBlock { Text = "背景與螢幕", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"], Margin = new Thickness(2, 0, 0, 4) });

        // background media
        var add = IconButton("", "新增圖片或影片", () => _ = AddMedia());
        add.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        var tiles = new GridView { SelectionMode = ListViewSelectionMode.Single, Margin = new Thickness(-4, 0, -4, 0) };
        foreach (string name in _draft.Playlist) tiles.Items.Add(Tile(name));
        tiles.SelectedIndex = _draft.Playlist.Count > 0 ? Math.Clamp(_draft.Current, 0, _draft.Playlist.Count - 1) : -1;
        tiles.SelectionChanged += (_, _) =>
        {
            if (tiles.SelectedIndex < 0) return;
            _draft.Current = tiles.SelectedIndex; LoadBackground(); Edited();
        };
        // An element can only ever sit in one panel, so each rebuild gets fresh bars that carry the old state over.
        _busy = new ProgressBar { IsIndeterminate = true, Visibility = _busy.Visibility };
        _mediaInfo = new InfoBar { IsClosable = true, IsOpen = _mediaInfo.IsOpen, Severity = _mediaInfo.Severity, Title = _mediaInfo.Title, Message = _mediaInfo.Message };
        var media = new StackPanel { Spacing = 8 };
        media.Children.Add(add); media.Children.Add(_busy); media.Children.Add(_mediaInfo);
        if (_draft.Playlist.Count > 0) media.Children.Add(tiles);
        else media.Children.Add(new TextBlock { Text = "還沒有媒體，目前使用純色底。支援 PNG、JPG、BMP、GIF、MP4、AVI。", Style = S("Secondary"), TextWrapping = TextWrapping.Wrap });
        Props.Children.Add(VCard("背景媒體", media));
        if (CurrentMedia is { } cur)
        {
            bool missing = !_draft.Sources.TryGetValue(cur, out string? src) || !File.Exists(src);
            Props.Children.Add(new TextBlock
            {
                Text = CanPlaceBackground ? "要調整背景的位置：按上方工具列的「移動背景」後在畫布上拖曳；旁邊的滑桿（或滾輪）可縮放，最右邊的按鈕置中重設。"
                    : missing ? "找不到這個媒體的原始檔，所以無法調整位置。把它移除後重新加入即可。"
                    : _bgSourceFailed == cur ? "無法讀取這個媒體的原始檔，所以無法調整位置。"
                    : "正在載入原始檔，稍後即可調整位置…",
                Style = S("Secondary"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 2, 4, 6),
            });
        }

        string[] cycles = ["one", "list", "random"];
        Props.Children.Add(Card("輪播", null, Choice(["固定這一個", "依序輪播", "隨機輪播"], Math.Max(0, Array.IndexOf(cycles, _draft.Cycle)), 140, i => { _draft.Cycle = cycles[i]; Edited(); })));
        var dwell = new NumberBox { Minimum = 1, Maximum = 3600, Value = _draft.ImageSeconds, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Width = 110 };
        dwell.ValueChanged += (_, a) => { if (!double.IsNaN(a.NewValue)) { _draft.ImageSeconds = (int)a.NewValue; Edited(); } };
        Props.Children.Add(Card("圖片停留秒數", "輪播靜態圖片時", dwell));
        Props.Children.Add(Card("純色底", "沒有媒體時使用", ColorButton(_draft.Background, h => { _draft.Background = h; LoadBackground(); Edited(); })));
        Props.Children.Add(Card("背景變暗", null, SliderWithValue(0, 80, _draft.Dim, "%", v => { _draft.Dim = v; Edited(); })));
        Props.Children.Add(Card("文字陰影", null, Switch(_draft.Shadow, v => { _draft.Shadow = v; Edited(); })));

        Props.Children.Add(Section("螢幕"));
        Props.Children.Add(Card("亮度", null, SliderWithValue(0, 100, _draft.Brightness, "%", v => { _draft.Brightness = v; Edited(); })));
        Props.Children.Add(Card("螢幕", null, Switch(_draft.ScreenOn, v => { _draft.ScreenOn = v; Edited(); })));
        Props.Children.Add(Card("方向", null, Choice(["0°（橫向）", "90°（直向）", "180°（橫向倒置）", "270°（直向倒置）"], _draft.Rotation / 90, 160, i => _ = Rotate(i * 90))));

        Props.Children.Add(Section("系統"));
        Props.Children.Add(Card("睡眠時關閉螢幕", null, Switch(_draft.SleepOff, v => { _draft.SleepOff = v; Edited(); })));
        Props.Children.Add(Card("溫度用華氏", null, Switch(_draft.Fahrenheit, v => { _draft.Fahrenheit = v; Edited(); })));
        Props.Children.Add(Card("開機自動啟動", "立即生效，不需套用", Switch(Autostart.IsOn(), v => Autostart.Set(v, EngineExe))));
        Props.Children.Add(new InfoBar
        {
            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Title = "溫度、風扇、電壓需要系統管理員權限",
            Message = "在系統匣的 InfoHub Lite 圖示上按右鍵，選「以系統管理員重新啟動」。使用率不需要。",
        });
        Props.Children.Add(Card("設定資料夾", null, IconButton("", "開啟", () => Process.Start(new ProcessStartInfo(Config.Dir) { UseShellExecute = true })?.Dispose())));
    }

    async Task Rotate(int rotation)
    {
        int oldW = _r.W, oldH = _r.H;
        bool flip = _draft.Portrait != (rotation is 90 or 270);
        _draft.Rotation = rotation;
        if (flip)
        {
            Templates.Remap(Els, oldW, oldH, _draft.CanvasW, _draft.CanvasH);
            await EnsureMedia();
            _picked.Clear();
            Reconfigure();
            BuildProps();
        }
        Edited();
    }

    // ---------- media ----------

    FrameworkElement Tile(string name)
    {
        var grid = new Grid { Width = 128, Padding = new Thickness(4), RowSpacing = 4 };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new Border { CornerRadius = new CornerRadius(4), Child = new Image { Source = MediaThumb(name), Stretch = Stretch.UniformToFill, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } });
        var caption = new TextBlock { Text = Path.GetFileNameWithoutExtension(name), Style = S("Secondary"), TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
        Grid.SetRow(caption, 1);
        grid.Children.Add(caption);
        var remove = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 12 }, Padding = new Thickness(6), Margin = new Thickness(4),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
        };
        ToolTipService.SetToolTip(remove, "移除");
        AutomationProperties.SetName(remove, "移除 " + name);
        remove.Click += (_, _) =>
        {
            int index = _draft.Playlist.IndexOf(name);
            if (index < 0) return;
            _draft.Playlist.RemoveAt(index); _draft.Sources.Remove(name); _draft.Fits.Remove(name);
            if (_draft.Current >= index && _draft.Current > 0) _draft.Current--;
            LoadBackground(); BuildProps(); Edited(); // the cache file itself is deleted when this is applied
        };
        grid.Children.Add(remove);
        return grid;
    }

    WriteableBitmap? MediaThumb(string name)
    {
        using var m = Media.Open(Cache(_draft, name)) ?? Media.Open(Cache(_applied, name)) ?? Media.Open(Media.PathFor(name, false)) ?? Media.Open(Media.PathFor(name, true));
        if (m == null) return null;
        var px = new ushort[Lcd.Pixels];
        var bgra = new byte[Lcd.Pixels * 4];
        m.Read(0, px);
        ToBgra(px, m.W, m.H, 1, bgra);
        var bmp = new WriteableBitmap(m.W, m.H);
        using (var s = bmp.PixelBuffer.AsStream()) s.Write(bgra);
        return bmp;
    }

    async Task AddMedia()
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
        foreach (string ext in new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif" }.Concat(Video.Extensions)) picker.FileTypeFilter.Add(ext);
        var files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0) return;
        _busy.Visibility = Visibility.Visible; _mediaInfo.IsOpen = false;
        foreach (var f in files)
        {
            string name = UniqueName(Path.GetFileNameWithoutExtension(f.Path));
            if (await Convert(f.Path, name)) { _draft.Playlist.Add(name); _draft.Sources[name] = f.Path; _draft.Current = _draft.Playlist.Count - 1; }
        }
        _busy.Visibility = Visibility.Collapsed;
        LoadBackground();
        if (_sel < 0) BuildProps();
        Edited();
    }

    string UniqueName(string stem)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        stem = stem.Replace('~', '_'); // '~' separates a name from its placement in cache file names
        string name = stem + ".ihl";
        for (int i = 2; _draft.Playlist.Contains(name) || _applied.Playlist.Contains(name) || File.Exists(Media.PathFor(name, false)) || File.Exists(Media.PathFor(name, true)); i++) name = $"{stem}-{i}.ihl";
        return name;
    }

    /// <summary>Source file -> frame cache for the draft's orientation. Decoding happens off the UI thread.</summary>
    async Task<bool> Convert(string src, string name)
    {
        bool portrait = _draft.Portrait;
        var fit = _draft.Fits.GetValueOrDefault(name);
        string dst = Media.PathFor(name, portrait, fit);
        try
        {
            Directory.CreateDirectory(Config.MediaDir);
            bool video = Video.Extensions.Contains(Path.GetExtension(src).ToLowerInvariant());
            await Task.Run(() => { if (video) Video.Import(src, dst, portrait, fit: fit); else Media.Import(src, dst, portrait, null, fit: fit); });
            return true;
        }
        catch (Exception e)
        {
            _mediaInfo.Severity = InfoBarSeverity.Error; _mediaInfo.Title = $"無法轉換 {Path.GetFileName(src)}"; _mediaInfo.Message = e.Message; _mediaInfo.IsOpen = true;
            return false;
        }
    }

    /// <summary>Each item is cached per orientation; make the missing ones from the originals when the panel is turned.</summary>
    async Task EnsureMedia()
    {
        _busy.Visibility = Visibility.Visible;
        foreach (string name in _draft.Playlist.ToArray())
            if (!File.Exists(Cache(_draft, name)) && _draft.Sources.TryGetValue(name, out string? src) && File.Exists(src))
                await Convert(src, name);
        _busy.Visibility = Visibility.Collapsed;
    }

    // ---------- sensors ----------

    async Task LoadSensors()
    {
        if (!File.Exists(EngineExe)) return;
        string[] lines = await Task.Run(() =>
        {
            using var proc = Process.Start(new ProcessStartInfo(EngineExe, "sensors") { RedirectStandardOutput = true, StandardOutputEncoding = System.Text.Encoding.UTF8, CreateNoWindow = true, UseShellExecute = false })!;
            string text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        });
        var values = new Dictionary<string, (float?, Kind)>();
        var friendly = new List<SensorItem>(); var detailed = new List<SensorItem>();
        foreach (string line in lines)
        {
            string[] c = line.TrimEnd('\r').Split('\t');
            if (c.Length < 6 || !Enum.TryParse(c[3], out Kind kind)) continue;
            values[c[0]] = (float.TryParse(c[5], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v) ? v : null, kind);
            string? alias = c[0] switch { "cpu.load" => "CPU 使用率", "ram.load" => "記憶體使用率", "gpu.load" => "GPU 使用率", "cpu.temp" => "CPU 溫度", "gpu.temp" => "GPU 溫度", _ => null };
            string kindZh = kind switch { Kind.Load => "使用率", Kind.Temperature => "溫度", Kind.Fan => "風扇", Kind.Clock => "頻率", Kind.Voltage => "電壓", Kind.Power => "功耗", _ => "其他" };
            if (alias != null) friendly.Add(new SensorItem(c[0], alias)); else detailed.Add(new SensorItem(c[0], $"{kindZh}｜{c[1]} · {c[2]}"));
        }
        if (values.Count == 0) return;
        _values = values;
        _sensorItems = [.. friendly, .. detailed.OrderBy(i => i.Text, StringComparer.Ordinal)];
        Render();
        if (_sel >= 0) BuildProps();
    }
}
