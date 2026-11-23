using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using FanControl.Models;
using FanControl.Services;

namespace FanControl.Controls;

/// <summary>
/// Interaktivní graf křivky teplota → otáčky.
/// Tažení bodu = změna, dvojklik do prázdna = nový bod, pravé tlačítko na bodu = smazání.
/// </summary>
public sealed class CurveEditor : FrameworkElement
{
    private const double PadLeft = 44, PadRight = 24, PadTop = 12, PadBottom = 28;
    private const double PointRadius = 6.5, HitRadius = 13;
    private const double MinTemp = 20, MaxTemp = 100;

    private int _dragIndex = -1;
    private int _hoverIndex = -1;

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(List<CurvePoint>), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentTemperatureProperty = DependencyProperty.Register(
        nameof(CurrentTemperature), typeof(double?), typeof(CurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EditedCommandProperty = DependencyProperty.Register(
        nameof(EditedCommand), typeof(ICommand), typeof(CurveEditor));

    public CurveEditor()
    {
        Focusable = false;
        MinHeight = 200;
        ClipToBounds = true;
        Loaded += (_, _) => Wpf.Ui.Appearance.ApplicationThemeManager.Changed += OnThemeChanged;
        Unloaded += (_, _) => Wpf.Ui.Appearance.ApplicationThemeManager.Changed -= OnThemeChanged;
    }

    private void OnThemeChanged(Wpf.Ui.Appearance.ApplicationTheme theme, Color accent) =>
        Dispatcher.BeginInvoke(InvalidateVisual);

    public List<CurvePoint>? Points
    {
        get => (List<CurvePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double? CurrentTemperature
    {
        get => (double?)GetValue(CurrentTemperatureProperty);
        set => SetValue(CurrentTemperatureProperty, value);
    }

    public ICommand? EditedCommand
    {
        get => (ICommand?)GetValue(EditedCommandProperty);
        set => SetValue(EditedCommandProperty, value);
    }

    // ---------- Souřadnice ----------

    private Rect Plot => new(PadLeft, PadTop,
        Math.Max(1, ActualWidth - PadLeft - PadRight),
        Math.Max(1, ActualHeight - PadTop - PadBottom));

    private Point ToScreen(double temp, double percent)
    {
        var r = Plot;
        return new Point(
            r.Left + (temp - MinTemp) / (MaxTemp - MinTemp) * r.Width,
            r.Bottom - percent / 100 * r.Height);
    }

    private (double Temp, double Percent) FromScreen(Point p)
    {
        var r = Plot;
        var temp = MinTemp + (p.X - r.Left) / r.Width * (MaxTemp - MinTemp);
        var percent = (r.Bottom - p.Y) / r.Height * 100;
        return (Math.Round(Math.Clamp(temp, MinTemp, MaxTemp)), Math.Round(Math.Clamp(percent, 0, 100)));
    }

    private int HitTestPoint(Point p)
    {
        var pts = Points;
        if (pts is null) return -1;
        for (var i = 0; i < pts.Count; i++)
        {
            var s = ToScreen(pts[i].Temperature, pts[i].Percent);
            if ((s - p).Length <= HitRadius) return i;
        }
        return -1;
    }

    // ---------- Myš ----------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var pts = Points;
        if (pts is null) return;
        var pos = e.GetPosition(this);
        var hit = HitTestPoint(pos);

        if (hit < 0 && e.ClickCount == 2 && Plot.Contains(pos))
        {
            var (t, p) = FromScreen(pos);
            if (pts.Any(x => Math.Abs(x.Temperature - t) < 1)) return;
            pts.Add(new CurvePoint(t, p));
            pts.Sort((a, b) => a.Temperature.CompareTo(b.Temperature));
            hit = pts.FindIndex(x => x.Temperature == t);
            Commit();
        }

        if (hit >= 0)
        {
            _dragIndex = hit;
            CaptureMouse();
            e.Handled = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var pts = Points;
        if (pts is null) return;
        var pos = e.GetPosition(this);

        if (_dragIndex >= 0 && _dragIndex < pts.Count)
        {
            var (t, p) = FromScreen(pos);
            var lo = _dragIndex > 0 ? pts[_dragIndex - 1].Temperature + 1 : MinTemp;
            var hi = _dragIndex < pts.Count - 1 ? pts[_dragIndex + 1].Temperature - 1 : MaxTemp;
            pts[_dragIndex].Temperature = Math.Clamp(t, lo, hi);
            pts[_dragIndex].Percent = p;
            InvalidateVisual();
            return;
        }

        var hover = HitTestPoint(pos);
        if (hover != _hoverIndex)
        {
            _hoverIndex = hover;
            Cursor = hover >= 0 ? Cursors.Hand : null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        ReleaseMouseCapture();
        Commit();
        InvalidateVisual();
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        var pts = Points;
        if (pts is null || pts.Count <= 2) return;
        var hit = HitTestPoint(e.GetPosition(this));
        if (hit < 0) return;
        pts.RemoveAt(hit);
        _hoverIndex = -1;
        e.Handled = true;
        Commit();
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex >= 0)
        {
            _hoverIndex = -1;
            InvalidateVisual();
        }
    }

    private void Commit()
    {
        if (EditedCommand?.CanExecute(null) == true) EditedCommand.Execute(null);
    }

    // ---------- Vykreslení ----------

    private Brush Res(string key, Color fallback) =>
        TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    protected override void OnRender(DrawingContext dc)
    {
        var r = Plot;
        if (r.Width < 20 || r.Height < 20) return;

        var text = Res("TextFillColorPrimaryBrush", Colors.White);
        var textSecondary = Res("TextFillColorSecondaryBrush", Colors.Gray);
        var stroke = Res("ControlStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128));
        var accentBrush = Res("SystemAccentColorPrimaryBrush", Color.FromRgb(0, 120, 212));
        var accent = accentBrush is SolidColorBrush sb ? sb.Color : Color.FromRgb(0, 120, 212);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var font = new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

        // Pozadí pro zachycení myši v celé ploše
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        // Mřížka a popisky
        var gridPen = new Pen(stroke, 1);
        gridPen.Freeze();
        for (var p = 0; p <= 100; p += 20)
        {
            var y = ToScreen(MinTemp, p).Y;
            dc.DrawLine(gridPen, new Point(r.Left, y), new Point(r.Right, y));
            DrawText(dc, $"{p} %", textSecondary, font, dpi, new Point(r.Left - 6, y), alignRight: true, vCenter: true);
        }
        for (var t = (int)MinTemp; t <= MaxTemp; t += 10)
        {
            var x = ToScreen(t, 0).X;
            dc.DrawLine(gridPen, new Point(x, r.Top), new Point(x, r.Bottom));
            DrawText(dc, $"{t} °C", textSecondary, font, dpi, new Point(x, r.Bottom + 6), center: true);
        }

        var pts = Points;
        if (pts is null || pts.Count == 0) return;

        // Plocha pod křivkou + čára
        var screen = pts.Select(p => ToScreen(p.Temperature, p.Percent)).ToList();
        var start = new Point(r.Left, screen[0].Y);
        var end = new Point(r.Right, screen[^1].Y);

        var area = new StreamGeometry();
        using (var ctx = area.Open())
        {
            ctx.BeginFigure(new Point(r.Left, r.Bottom), true, true);
            ctx.LineTo(start, false, false);
            foreach (var s in screen) ctx.LineTo(s, false, false);
            ctx.LineTo(end, false, false);
            ctx.LineTo(new Point(r.Right, r.Bottom), false, false);
        }
        area.Freeze();
        var fill = new LinearGradientBrush(Color.FromArgb(90, accent.R, accent.G, accent.B), Color.FromArgb(10, accent.R, accent.G, accent.B), 90);
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(start, false, false);
            foreach (var s in screen) ctx.LineTo(s, true, true);
            ctx.LineTo(end, true, true);
        }
        line.Freeze();
        var linePen = new Pen(new SolidColorBrush(accent), 2.5) { LineJoin = PenLineJoin.Round };
        linePen.Freeze();
        dc.DrawGeometry(null, linePen, line);

        // Aktuální teplota – čára a bod (popisek až nad body, aby nebyl zakrytý)
        var warm = Color.FromRgb(232, 112, 32);
        Point? livePos = null;
        string? liveLabel = null;
        if (CurrentTemperature is { } cur && cur > 0)
        {
            var clamped = Math.Clamp(cur, MinTemp, MaxTemp);
            var percent = CurveMath.Evaluate(pts.OrderBy(p => p.Temperature).ToList(), cur);
            var pos = ToScreen(clamped, percent);
            var dash = new Pen(new SolidColorBrush(warm), 1.5) { DashStyle = new DashStyle([4, 3], 0) };
            dash.Freeze();
            dc.DrawLine(dash, new Point(pos.X, r.Top), new Point(pos.X, r.Bottom));
            livePos = pos;
            liveLabel = $"Teď {cur:0} °C → {percent:0} %";
        }

        // Body
        var pointFill = new SolidColorBrush(accent);
        var surface = Res("SolidBackgroundFillColorBaseBrush", Colors.White);
        var ring = new Pen(surface, 2);
        for (var i = 0; i < screen.Count; i++)
        {
            var active = i == _dragIndex || i == _hoverIndex;
            var radius = active ? PointRadius + 2 : PointRadius;
            dc.DrawEllipse(pointFill, ring, screen[i], radius, radius);
        }

        if (livePos is { } lp0)
        {
            dc.DrawEllipse(new SolidColorBrush(warm), ring, lp0, 5.5, 5.5);
            DrawLabel(dc, liveLabel!, new SolidColorBrush(warm), surface, font, dpi, lp0, r);
        }

        // Popisek bodu pod myší
        var activeIndex = _dragIndex >= 0 ? _dragIndex : _hoverIndex;
        if (activeIndex >= 0 && activeIndex < pts.Count)
        {
            var label = $"{pts[activeIndex].Temperature:0} °C · {pts[activeIndex].Percent:0} %";
            DrawLabel(dc, label, text, surface, font, dpi, screen[activeIndex], r);
        }
    }

    /// <summary>Popisek v „bublině“ nad bodem; u okrajů grafu se přesune dovnitř.</summary>
    private static void DrawLabel(DrawingContext dc, string s, Brush brush, Brush background, Typeface font, double dpi, Point anchor, Rect bounds)
    {
        var face = new Typeface(font.FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 11.5, brush, dpi);
        var w = ft.Width + 14;
        var h = ft.Height + 6;
        var x = anchor.X + 12;
        if (x + w > bounds.Right) x = anchor.X - 12 - w;
        var y = anchor.Y - h - 10;
        if (y < bounds.Top) y = anchor.Y + 12;
        var box = new Rect(x, y, w, h);
        dc.DrawRoundedRectangle(background, new Pen(brush, 1), box, 6, 6);
        dc.DrawText(ft, new Point(x + 7, y + 3));
    }

    private static void DrawText(DrawingContext dc, string s, Brush brush, Typeface font, double dpi, Point at,
        bool alignRight = false, bool center = false, bool vCenter = false)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, font, 11.5, brush, dpi);
        var x = alignRight ? at.X - ft.Width : center ? at.X - ft.Width / 2 : at.X;
        var y = vCenter ? at.Y - ft.Height / 2 : at.Y;
        dc.DrawText(ft, new Point(x, y));
    }
}
